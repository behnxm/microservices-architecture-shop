using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Shop.Ordering.Api.Clients;
using Shouldly;
using Xunit;

namespace Shop.Ordering.Tests.Clients;

public sealed class CatalogClientResilienceTests
{
    private sealed class DelegatingTestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handlerFunc)
        : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handlerFunc(request, cancellationToken);
    }

    [Fact]
    public async Task GetProductsAsync_Retries_On_Transient_503_Errors_And_Succeeds()
    {
        // Arrange: fails twice with 503, then succeeds on 3rd attempt
        var callCount = 0;
        var productId = Guid.NewGuid();
        var sampleResponse = new CatalogLookupResult([
            new CatalogProductItem(productId, "SKU-001", "Ergonomic Chair", 249.99m, "USD", 10)
        ]);
        var json = JsonSerializer.Serialize(sampleResponse);

        var testHandler = new DelegatingTestHandler((_, _) =>
        {
            var attempt = Interlocked.Increment(ref callCount);
            if (attempt < 3)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("Service temporarily down")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var services = new ServiceCollection();
        services.AddHttpClient<ICatalogClient, CatalogClient>(c =>
        {
            c.BaseAddress = new Uri("http://catalog-api");
        })
        .ConfigurePrimaryHttpMessageHandler(() => testHandler)
        .AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 3;
            options.Retry.BackoffType = DelayBackoffType.Constant;
            options.Retry.Delay = TimeSpan.FromMilliseconds(10);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(5);
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(2);
        });

        using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<ICatalogClient>();

        // Act
        var result = await client.GetProductsAsync([productId]);

        // Assert
        callCount.ShouldBe(3);
        result.Products.Count.ShouldBe(1);
        result.Products[0].Sku.ShouldBe("SKU-001");
        result.Products[0].Price.ShouldBe(249.99m);
    }

    [Fact]
    public async Task GetProductsAsync_Does_Not_Retry_On_400_BadRequest()
    {
        // Arrange
        var callCount = 0;
        var testHandler = new DelegatingTestHandler((_, _) =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("Invalid query format")
            });
        });

        var services = new ServiceCollection();
        services.AddHttpClient<ICatalogClient, CatalogClient>(c =>
        {
            c.BaseAddress = new Uri("http://catalog-api");
        })
        .ConfigurePrimaryHttpMessageHandler(() => testHandler)
        .AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 3;
            options.Retry.BackoffType = DelayBackoffType.Constant;
            options.Retry.Delay = TimeSpan.FromMilliseconds(10);
        });

        using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<ICatalogClient>();

        // Act & Assert
        await Should.ThrowAsync<CatalogUnavailableException>(() =>
            client.GetProductsAsync([Guid.NewGuid()]));

        // Exactly 1 attempt made because 400 is non-transient
        callCount.ShouldBe(1);
    }

    [Fact]
    public async Task CircuitBreaker_Opens_After_Consecutive_Failures()
    {
        // Arrange: always fails with 500 InternalServerError
        var callCount = 0;
        var testHandler = new DelegatingTestHandler((_, _) =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("Fatal server error")
            });
        });

        var services = new ServiceCollection();
        services.AddHttpClient<ICatalogClient, CatalogClient>(c =>
        {
            c.BaseAddress = new Uri("http://catalog-api");
        })
        .ConfigurePrimaryHttpMessageHandler(() => testHandler)
        .AddStandardResilienceHandler(options =>
        {
            // Set aggressive breaker thresholds to test circuit breaking
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(1);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(5);
            options.Retry.MaxRetryAttempts = 1;
            options.Retry.BackoffType = DelayBackoffType.Constant;
            options.Retry.Delay = TimeSpan.FromMilliseconds(5);
            options.CircuitBreaker.FailureRatio = 0.5;
            options.CircuitBreaker.MinimumThroughput = 2;
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(4);
            options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
        });

        using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<ICatalogClient>();

        // Act: First request fails twice (initial + 1 retry), meeting MinimumThroughput=2 with 100% failure rate
        await Should.ThrowAsync<CatalogUnavailableException>(() =>
            client.GetProductsAsync([Guid.NewGuid()]));

        callCount.ShouldBe(2);

        // Subsequent request: The circuit should now be open, rejecting calls fast without reaching handler
        await Should.ThrowAsync<CatalogUnavailableException>(() =>
            client.GetProductsAsync([Guid.NewGuid()]));

        // The handler was NOT called for the subsequent request because circuit breaker rejected it immediately
        callCount.ShouldBe(2);
    }
}
