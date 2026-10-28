using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Scalar.AspNetCore;
using Shop.BuildingBlocks.Messaging.Data;
using Shop.BuildingBlocks.Messaging.Extensions;
using Shop.Contracts;
using Shop.Ordering.Api.Clients;
using Shop.Ordering.Api.Consumers;
using Shop.Ordering.Api.Data;
using Shop.Ordering.Api.Orders;
using Shop.ServiceDefaults;

// 1. Standalone healthcheck CLI switch for container environments
if (args.Contains(HealthCheckCli.Flag))
{
    return await HealthCheckCli.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(args);

// 2. Aspire Service Defaults: OpenTelemetry, resilience, service discovery, health checks
builder.AddServiceDefaults();

// 3. Database configuration (Npgsql PostgreSQL)
var connectionString = builder.Configuration.GetConnectionString("orderingdb")
    ?? builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Database=orderingdb;Username=postgres;Password=postgres_dev_password";

builder.Services.AddDbContext<OrderingDbContext>(options =>
{
    options.UseNpgsql(connectionString);
});

builder.Services.AddHealthChecks()
    .AddDbContextCheck<OrderingDbContext>("orderingdb", tags: ["ready"]);

// 4. Resilient HttpClient for internal Catalog service communication
builder.Services.AddHttpClient<ICatalogClient, CatalogClient>(client =>
{
    var catalogUrl = builder.Configuration["Services:Catalog:Url"] ?? "http://catalog-api";
    client.BaseAddress = new Uri(catalogUrl);
})
.AddStandardResilienceHandler(options =>
{
    options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(10);
    options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(2);
    options.Retry.MaxRetryAttempts = 3;
    options.Retry.BackoffType = DelayBackoffType.Exponential;
    options.Retry.UseJitter = true;
    options.Retry.Delay = TimeSpan.FromMilliseconds(200);
    options.CircuitBreaker.FailureRatio = 0.5;
    options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(10);
    options.CircuitBreaker.MinimumThroughput = 5;
    options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(15);
});

// 5. Messaging infrastructure, outbox, inbox, and consumers
builder.Services.AddMessagingInfrastructure(builder.Configuration);
builder.Services.AddTransactionalOutbox<OrderingDbContext>();
builder.Services.AddTransactionalInbox<OrderingDbContext>();
builder.Services.AddConsumer<StockReservedIntegrationEvent, StockReservedHandler, OrderingDbContext>("ordering.stock-reserved");
builder.Services.AddConsumer<StockRejectedIntegrationEvent, StockRejectedHandler, OrderingDbContext>("ordering.stock-rejected");

// 6. OpenAPI & Documentation
builder.Services.AddOpenApi();

var app = builder.Build();

// 7. Automatic migrations on startup (using advisory lock to support safe multi-replica startup)
if (app.Configuration.GetValue<bool>("Database:Migrate"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    logger.LogInformation("Applying Ordering database migrations...");
    await DatabaseMigrator.MigrateWithLockAsync(db, logger, lockKey: 7429103L);
    logger.LogInformation("Ordering database migrations applied successfully.");
}

// 8. OpenAPI & Scalar UI (Development only)
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// 9. Map Endpoints
app.MapOrderEndpoints();
app.MapDefaultEndpoints();

await app.RunAsync();
return 0;

namespace Shop.Ordering.Api
{
    public partial class Program { }
}
