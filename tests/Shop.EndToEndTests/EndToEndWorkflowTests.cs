using System;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Aspire.Hosting.Testing;
using Shouldly;
using Xunit;

namespace Shop.EndToEndTests;

[Trait("Category", "E2E")]
public sealed class EndToEndWorkflowTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public async Task Complete_Order_Checkout_Saga_And_Idempotency_Workflow()
    {
        // 1. Arrange & Start Aspire distributed application test host
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Shop_AppHost>();
        await using var app = await appHost.BuildAsync();
        await app.StartAsync();

        // 2. Resolve Gateway HTTP client (the sole public entry point)
        var gatewayClient = app.CreateHttpClient("gateway");

        // 3. Verify Gateway routing to Catalog products
        var productsResponse = await gatewayClient.GetAsync("/api/v1/products");
        productsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var productsDoc = JsonDocument.Parse(await productsResponse.Content.ReadAsStringAsync());
        var items = productsDoc.RootElement.GetProperty("items");
        items.GetArrayLength().ShouldBeGreaterThan(0);

        var firstProduct = items[0];
        var productId = firstProduct.GetProperty("id").GetGuid();

        // 4. Place order with Idempotency-Key
        var idempotencyKey = $"e2e-{Guid.NewGuid():N}";
        var orderPayload = new
        {
            customerId = Guid.NewGuid(),
            customerEmail = "buyer@example.com",
            lines = new[]
            {
                new { productId = productId, quantity = 1 }
            }
        };

        using var orderMsg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(orderPayload)
        };
        orderMsg.Headers.Add("Idempotency-Key", idempotencyKey);

        var orderRes = await gatewayClient.SendAsync(orderMsg);
        orderRes.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        orderRes.Headers.Location.ShouldNotBeNull();

        using var orderCreatedDoc = JsonDocument.Parse(await orderRes.Content.ReadAsStringAsync());
        var orderId = orderCreatedDoc.RootElement.GetProperty("id").GetGuid();
        orderCreatedDoc.RootElement.GetProperty("status").GetString().ShouldBe("Pending");

        // 5. Verify Idempotency replay with identical key
        using var replayMsg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(orderPayload)
        };
        replayMsg.Headers.Add("Idempotency-Key", idempotencyKey);

        var replayRes = await gatewayClient.SendAsync(replayMsg);
        replayRes.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        replayRes.Headers.Contains("Idempotent-Replayed").ShouldBeTrue();

        // 6. Poll for choreographed saga completion (Pending -> Confirmed)
        var confirmed = false;
        for (var i = 0; i < 20; i++)
        {
            var statusRes = await gatewayClient.GetAsync($"/api/v1/orders/{orderId}");
            if (statusRes.StatusCode == HttpStatusCode.OK)
            {
                using var statusDoc = JsonDocument.Parse(await statusRes.Content.ReadAsStringAsync());
                var currentStatus = statusDoc.RootElement.GetProperty("status").GetString();
                if (currentStatus == "Confirmed")
                {
                    confirmed = true;
                    break;
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        confirmed.ShouldBeTrue("Expected order to transition to Confirmed within 20s saga execution.");
    }
}
