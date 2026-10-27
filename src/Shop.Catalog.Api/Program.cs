using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scalar.AspNetCore;
using Shop.Catalog.Api.Data;
using Shop.Catalog.Api.Products;
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
var connectionString = builder.Configuration.GetConnectionString("catalogdb")
    ?? builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Database=catalogdb;Username=postgres;Password=postgres_dev_password";

builder.Services.AddDbContext<CatalogDbContext>(options =>
{
    options.UseNpgsql(connectionString);
});

builder.Services.AddHealthChecks()
    .AddDbContextCheck<CatalogDbContext>("catalogdb", tags: ["ready"]);

// 4. OpenAPI & Documentation
builder.Services.AddOpenApi();

var app = builder.Build();

// 5. Automatic migrations and seed on startup (configured for development/compose/apphost)
if (app.Configuration.GetValue<bool>("Database:Migrate"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    logger.LogInformation("Applying Catalog database migrations...");
    await db.Database.MigrateAsync();
    logger.LogInformation("Catalog database migrations applied successfully.");

    if (app.Configuration.GetValue<bool>("Database:Seed"))
    {
        await CatalogSeed.SeedAsync(db, logger);
    }
}

// 6. OpenAPI & Scalar UI (Development only)
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// 7. Map Endpoints
app.MapProductEndpoints();
app.MapDefaultEndpoints();

await app.RunAsync();
return 0;

namespace Shop.Catalog.Api
{
    public partial class Program { }
}
