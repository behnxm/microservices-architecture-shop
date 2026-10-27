using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shop.Gateway;
using Shop.ServiceDefaults;
using Yarp.ReverseProxy.Configuration;

// 1. Standalone healthcheck CLI switch for container environments
if (args.Contains(HealthCheckCli.Flag))
{
    return await HealthCheckCli.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(args);

// 2. Aspire Service Defaults: OpenTelemetry, metrics, logging, service discovery
builder.AddServiceDefaults();
builder.Services.AddRequestTimeouts();

// 3. Security & Forwarded Headers
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// 4. CORS configuration
var allowedOrigins = builder.Configuration["Cors:AllowedOrigins"]?.Split(';', StringSplitOptions.RemoveEmptyEntries)
    ?? ["http://localhost:3000"];

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyMethod()
            .WithHeaders("Content-Type", "Idempotency-Key", "X-Trace-Id", "Authorization")
            .WithExposedHeaders("X-Trace-Id", "Location", "Idempotent-Replayed", "Retry-After");
    });
});

// 5. Rate Limiting policies per client IP
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.ContentType = "application/problem+json";
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            type = "https://httpstatuses.com/429",
            title = "Too Many Requests",
            status = StatusCodes.Status429TooManyRequests,
            detail = "Rate limit exceeded. Please retry after some time.",
            code = "Gateway.RateLimitExceeded"
        }, cancellationToken: token);
    };

    // Standard policy for read-heavy catalog routes
    options.AddPolicy("standard-policy", httpContext =>
    {
        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            clientIp,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromSeconds(10),
                QueueLimit = 0
            });
    });

    // Stricter policy for sensitive state-mutating checkout routes
    options.AddPolicy("orders-policy", httpContext =>
    {
        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            clientIp,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromSeconds(10),
                QueueLimit = 0
            });
    });
});

// 6. YARP Reverse Proxy with Service Discovery resolution
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddServiceDiscoveryDestinationResolver();

var app = builder.Build();

// 7. Security Headers Middleware
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    context.Response.Headers.Append("Content-Security-Policy", "default-src 'none'; frame-ancestors 'none'");
    await next(context);
});

app.UseForwardedHeaders();
app.UseCors();
app.UseRateLimiter();
app.UseRequestTimeouts();

// 8. Public Gateway Index & Health Endpoints
app.MapGet("/", () => Results.Ok(new
{
    name = "Shop.Gateway",
    version = "1.0.0",
    status = "online",
    routes = GatewayConstants.ExposedRoutes
}));

app.MapDefaultEndpoints();

// 9. Map YARP Reverse Proxy routes
app.MapReverseProxy();

await app.RunAsync();
return 0;

namespace Shop.Gateway
{
    public partial class Program { }
}
