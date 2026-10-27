using System;
using System.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Catalog.Api.Data;

namespace Shop.Catalog.Tests;

public sealed class CatalogApiFactory : WebApplicationFactory<Shop.Catalog.Api.Program>
{
    private readonly string _dbName = $"catalog_test_db_{Guid.NewGuid()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.UseSetting("Database:Migrate", "false");
        builder.UseSetting("Database:Seed", "false");
        builder.UseSetting("HealthChecks:Expose", "true");

        builder.ConfigureServices(services =>
        {
            var descriptors = services.Where(d =>
                d.ServiceType == typeof(DbContextOptions<CatalogDbContext>)
                || d.ServiceType == typeof(DbContextOptions)
                || d.ServiceType == typeof(CatalogDbContext)).ToList();

            foreach (var d in descriptors)
            {
                services.Remove(d);
            }

            var internalProvider = new ServiceCollection()
                .AddEntityFrameworkInMemoryDatabase()
                .BuildServiceProvider();

            services.AddDbContext<CatalogDbContext>(options =>
            {
                options.UseInMemoryDatabase(_dbName);
                options.UseInternalServiceProvider(internalProvider);
            });
        });
    }
}
