using System;
using System.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shop.Ordering.Api.Clients;
using Shop.Ordering.Api.Data;

namespace Shop.Ordering.Tests.Api;

public sealed class OrderingApiFactory : WebApplicationFactory<Shop.Ordering.Api.Program>
{
    private readonly string _dbName = $"ordering_test_db_{Guid.NewGuid()}";
    public ICatalogClient CatalogClientMock { get; } = Substitute.For<ICatalogClient>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.UseSetting("Database:Migrate", "false");
        builder.UseSetting("HealthChecks:Expose", "true");

        builder.ConfigureServices(services =>
        {
            var descriptors = services.Where(d =>
                d.ServiceType == typeof(DbContextOptions<OrderingDbContext>)
                || d.ServiceType == typeof(DbContextOptions)
                || d.ServiceType == typeof(OrderingDbContext)).ToList();

            foreach (var d in descriptors)
            {
                services.Remove(d);
            }

            var internalProvider = new ServiceCollection()
                .AddEntityFrameworkInMemoryDatabase()
                .BuildServiceProvider();

            services.AddDbContext<OrderingDbContext>(options =>
            {
                options.UseInMemoryDatabase(_dbName);
                options.UseInternalServiceProvider(internalProvider);
            });

            // Replace ICatalogClient with mock
            var catalogDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ICatalogClient));
            if (catalogDescriptor is not null)
            {
                services.Remove(catalogDescriptor);
            }
            services.AddSingleton(CatalogClientMock);
        });
    }
}
