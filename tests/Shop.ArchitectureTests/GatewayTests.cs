using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

namespace Shop.ArchitectureTests;

public sealed class GatewayFactory : WebApplicationFactory<Shop.Gateway.Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("HealthChecks:Expose", "true");
    }
}

public sealed class GatewayTests : IClassFixture<GatewayFactory>
{
    private readonly HttpClient _client;

    public GatewayTests(GatewayFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Gateway_Index_Returns_200_OK_And_Exposed_Routes()
    {
        var response = await _client.GetAsync("/");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("Shop.Gateway");
        content.ShouldContain("/api/v1/products");
        content.ShouldContain("/api/v1/orders");
    }

    [Fact]
    public async Task X_Trace_Id_Header_Is_Present_On_Responses()
    {
        var response = await _client.GetAsync("/");
        response.Headers.Contains("X-Trace-Id").ShouldBeTrue("Gateway must include X-Trace-Id header on all responses.");
    }

    [Fact]
    public async Task Internal_Route_Is_Not_Reachable_Through_Gateway()
    {
        // YARP reverse proxy only matches /api/v1/products/... and /api/v1/orders/...
        // An attempt to access /internal/v1/... must not match any route, resulting in 404
        var response = await _client.GetAsync("/internal/v1/products/lookup");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Down_Destination_Yields_Clean_Error()
    {
        // When downstream catalog-api is not reachable, YARP returns 502 Bad Gateway or 503 Service Unavailable
        var response = await _client.GetAsync("/api/v1/products/test-downstream");
        var statusCode = (int)response.StatusCode;
        (statusCode == 502 || statusCode == 503 || statusCode == 504).ShouldBeTrue(
            $"Downstream failure should yield 502, 503, or 504, but was {statusCode}");
    }

    [Fact]
    public async Task Rate_Limiter_Enforces_Limit_On_Orders_Route()
    {
        // orders-policy has a limit of 10 requests per 10-second window.
        // Send 15 concurrent requests so they all arrive in the same rate limiter window.
        var tasks = Enumerable.Range(0, 15)
            .Select(_ => _client.GetAsync("/api/v1/orders/rapid-test"))
            .ToList();

        var responses = await Task.WhenAll(tasks);
        var tooManyRequests = responses.FirstOrDefault(r => r.StatusCode == HttpStatusCode.TooManyRequests);

        tooManyRequests.ShouldNotBeNull("Expected at least one request to be rejected with 429 Too Many Requests.");
        var content = await tooManyRequests.Content.ReadAsStringAsync();
        content.ShouldContain("Gateway.RateLimitExceeded");
    }
}
