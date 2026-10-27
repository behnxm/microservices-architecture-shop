using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shop.Ordering.Api.Clients;
using Shop.Ordering.Api.Orders;
using Shouldly;
using Xunit;

namespace Shop.Ordering.Tests.Api;

public sealed class OrderApiTests : IDisposable
{
    private readonly OrderingApiFactory _factory;
    private readonly HttpClient _client;
    private static readonly JsonSerializerOptions CamelCaseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public OrderApiTests()
    {
        _factory = new OrderingApiFactory();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Create_Order_Without_Idempotency_Key_Returns_400_Bad_Request()
    {
        // Arrange
        var request = new CreateOrderRequest(
            Guid.NewGuid(),
            "alice@example.com",
            [new CreateOrderLineRequest(Guid.NewGuid(), 1)]);

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/orders", request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(CamelCaseOptions);
        problem.ShouldNotBeNull();
        problem.Extensions["code"]?.ToString().ShouldBe("Ordering.MissingIdempotencyKey");
    }

    [Fact]
    public async Task Create_Order_With_Too_Long_Idempotency_Key_Returns_400_Bad_Request()
    {
        // Arrange
        var request = new CreateOrderRequest(
            Guid.NewGuid(),
            "alice@example.com",
            [new CreateOrderLineRequest(Guid.NewGuid(), 1)]);

        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Add("Idempotency-Key", new string('a', 65));

        // Act
        var response = await _client.SendAsync(message);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(CamelCaseOptions);
        problem.ShouldNotBeNull();
        problem.Extensions["code"]?.ToString().ShouldBe("Ordering.InvalidIdempotencyKey");
    }

    [Fact]
    public async Task Create_Order_Success_Returns_202_Accepted_With_Location_Header_And_Pending_Status()
    {
        // Arrange
        var productId = Guid.NewGuid();
        _factory.CatalogClientMock.GetProductsAsync(
            Arg.Any<IEnumerable<Guid>>(),
            Arg.Any<CancellationToken>())
            .Returns(new CatalogLookupResult([
                new CatalogProductItem(productId, "KEY-01", "Mechanical Keyboard", 150.00m, "USD", 25)
            ]));

        var request = new CreateOrderRequest(
            Guid.NewGuid(),
            "bob@example.com",
            [new CreateOrderLineRequest(productId, 2)]);

        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(request)
        };
        var key = Guid.NewGuid().ToString("N");
        message.Headers.Add("Idempotency-Key", key);

        // Act
        var response = await _client.SendAsync(message);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        response.Headers.Location.ShouldNotBeNull();

        var created = await response.Content.ReadFromJsonAsync<OrderCreatedResponse>(CamelCaseOptions);
        created.ShouldNotBeNull();
        created.Id.ShouldNotBe(Guid.Empty);
        created.Status.ShouldBe(OrderStatus.Pending.ToString());
        response.Headers.Location.ToString().ShouldBe($"/api/v1/orders/{created.Id}");

        // Verify order persisted
        var getOrder = await _client.GetAsync($"/api/v1/orders/{created.Id}");
        getOrder.StatusCode.ShouldBe(HttpStatusCode.OK);
        var orderDetails = await getOrder.Content.ReadFromJsonAsync<OrderResponse>(CamelCaseOptions);
        orderDetails.ShouldNotBeNull();
        orderDetails.Id.ShouldBe(created.Id);
        orderDetails.CustomerEmail.ShouldBe("bob@example.com");
        orderDetails.Total.ShouldBe(300.00m); // 150 * 2
        orderDetails.Status.ShouldBe("Pending");
        orderDetails.Lines.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Create_Order_Replay_Same_Key_And_Payload_Returns_Cached_202_With_Idempotent_Replayed_Header()
    {
        // Arrange
        var productId = Guid.NewGuid();
        _factory.CatalogClientMock.GetProductsAsync(
            Arg.Any<IEnumerable<Guid>>(),
            Arg.Any<CancellationToken>())
            .Returns(new CatalogLookupResult([
                new CatalogProductItem(productId, "MOUSE-01", "Gaming Mouse", 60.00m, "USD", 50)
            ]));

        var request = new CreateOrderRequest(
            Guid.NewGuid(),
            "carol@example.com",
            [new CreateOrderLineRequest(productId, 1)]);

        var idempotencyKey = "order-replay-" + Guid.NewGuid();

        // Act 1: Initial call
        using (var initialMsg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders"))
        {
            initialMsg.Content = JsonContent.Create(request);
            initialMsg.Headers.Add("Idempotency-Key", idempotencyKey);
            var res1 = await _client.SendAsync(initialMsg);
            res1.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            res1.Headers.Contains("Idempotent-Replayed").ShouldBeFalse();
        }

        // Act 2: Replay call with identical payload and key
        using (var replayMsg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders"))
        {
            replayMsg.Content = JsonContent.Create(request);
            replayMsg.Headers.Add("Idempotency-Key", idempotencyKey);
            var res2 = await _client.SendAsync(replayMsg);

            // Assert
            res2.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            res2.Headers.Contains("Idempotent-Replayed").ShouldBeTrue();
            res2.Headers.GetValues("Idempotent-Replayed").First().ShouldBe("true");
        }

        // Catalog client should have been invoked exactly ONCE
        await _factory.CatalogClientMock.Received(1).GetProductsAsync(
            Arg.Any<IEnumerable<Guid>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_Order_Replay_Same_Key_With_Different_Payload_Returns_422_Unprocessable_Entity()
    {
        // Arrange
        var productId1 = Guid.NewGuid();
        var productId2 = Guid.NewGuid();
        _factory.CatalogClientMock.GetProductsAsync(
            Arg.Any<IEnumerable<Guid>>(),
            Arg.Any<CancellationToken>())
            .Returns(new CatalogLookupResult([
                new CatalogProductItem(productId1, "PROD-1", "Product 1", 10m, "USD", 10),
                new CatalogProductItem(productId2, "PROD-2", "Product 2", 20m, "USD", 10)
            ]));

        var key = "conflict-key-" + Guid.NewGuid();

        var request1 = new CreateOrderRequest(
            Guid.NewGuid(),
            "user1@example.com",
            [new CreateOrderLineRequest(productId1, 1)]);

        var request2 = new CreateOrderRequest(
            Guid.NewGuid(),
            "user2@example.com", // Different customer/payload
            [new CreateOrderLineRequest(productId2, 2)]);

        // Act 1: Send request1
        using (var msg1 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders"))
        {
            msg1.Content = JsonContent.Create(request1);
            msg1.Headers.Add("Idempotency-Key", key);
            var res1 = await _client.SendAsync(msg1);
            res1.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        // Act 2: Send request2 with same key but different body
        using (var msg2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders"))
        {
            msg2.Content = JsonContent.Create(request2);
            msg2.Headers.Add("Idempotency-Key", key);
            var res2 = await _client.SendAsync(msg2);

            // Assert
            res2.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            var problem = await res2.Content.ReadFromJsonAsync<ProblemDetails>(CamelCaseOptions);
            problem.ShouldNotBeNull();
            problem.Extensions["code"]?.ToString().ShouldBe("Ordering.IdempotencyPayloadMismatch");
        }
    }

    [Fact]
    public async Task Create_Order_With_Unknown_Product_Returns_422_Unprocessable_Entity()
    {
        // Arrange: Catalog returns empty list (product not found)
        var unknownProductId = Guid.NewGuid();
        _factory.CatalogClientMock.GetProductsAsync(
            Arg.Any<IEnumerable<Guid>>(),
            Arg.Any<CancellationToken>())
            .Returns(new CatalogLookupResult([]));

        var request = new CreateOrderRequest(
            Guid.NewGuid(),
            "dave@example.com",
            [new CreateOrderLineRequest(unknownProductId, 1)]);

        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        // Act
        var response = await _client.SendAsync(message);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(CamelCaseOptions);
        problem.ShouldNotBeNull();
        problem.Extensions["code"]?.ToString().ShouldBe("Ordering.UnknownProduct");
    }

    [Fact]
    public async Task Create_Order_With_Currency_Mismatch_Returns_422_Unprocessable_Entity()
    {
        // Arrange: Catalog returns currency EUR instead of USD
        var productId = Guid.NewGuid();
        _factory.CatalogClientMock.GetProductsAsync(
            Arg.Any<IEnumerable<Guid>>(),
            Arg.Any<CancellationToken>())
            .Returns(new CatalogLookupResult([
                new CatalogProductItem(productId, "SKU-EUR", "European Model", 100m, "EUR", 5)
            ]));

        var request = new CreateOrderRequest(
            Guid.NewGuid(),
            "eva@example.com",
            [new CreateOrderLineRequest(productId, 1)]);

        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        // Act
        var response = await _client.SendAsync(message);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(CamelCaseOptions);
        problem.ShouldNotBeNull();
        problem.Extensions["code"]?.ToString().ShouldBe("Ordering.CurrencyMismatch");
    }

    [Fact]
    public async Task Create_Order_With_Catalog_Outage_Returns_503_Service_Unavailable_With_Retry_After_Header()
    {
        // Arrange: CatalogClient throws CatalogUnavailableException
        _factory.CatalogClientMock.GetProductsAsync(
            Arg.Any<IEnumerable<Guid>>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new CatalogUnavailableException("Catalog service is down"));

        var request = new CreateOrderRequest(
            Guid.NewGuid(),
            "frank@example.com",
            [new CreateOrderLineRequest(Guid.NewGuid(), 1)]);

        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        // Act
        var response = await _client.SendAsync(message);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Headers.Contains("Retry-After").ShouldBeTrue();
        response.Headers.GetValues("Retry-After").First().ShouldBe("15");

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(CamelCaseOptions);
        problem.ShouldNotBeNull();
        problem.Extensions["code"]?.ToString().ShouldBe("Ordering.CatalogUnavailable");
    }

    [Fact]
    public async Task Get_Order_By_Unknown_Id_Returns_404_Not_Found()
    {
        // Act
        var response = await _client.GetAsync($"/api/v1/orders/{Guid.NewGuid()}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(CamelCaseOptions);
        problem.ShouldNotBeNull();
        problem.Extensions["code"]?.ToString().ShouldBe("Ordering.OrderNotFound");
    }
}
