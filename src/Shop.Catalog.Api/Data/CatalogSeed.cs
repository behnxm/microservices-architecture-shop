using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shop.Catalog.Api.Products;

namespace Shop.Catalog.Api.Data;

public static class CatalogSeed
{
    public static readonly Guid LaptopId = Guid.Parse("018f6c40-0000-7000-8000-000000000001");
    public static readonly Guid MouseId = Guid.Parse("018f6c40-0000-7000-8000-000000000002");
    public static readonly Guid KeyboardId = Guid.Parse("018f6c40-0000-7000-8000-000000000003");
    public static readonly Guid MonitorId = Guid.Parse("018f6c40-0000-7000-8000-000000000004");
    public static readonly Guid HeadsetId = Guid.Parse("018f6c40-0000-7000-8000-000000000005");

    public static async Task SeedAsync(CatalogDbContext db, ILogger logger, CancellationToken cancellationToken = default)
    {
        if (await db.Products.AnyAsync(cancellationToken))
        {
            logger.LogInformation("Catalog database already contains products. Skipping seed.");
            return;
        }

        var products = new[]
        {
            Product.Create("PROD-LAPTOP-01", "Enterprise Laptop 15-inch", 1299.99m, 50, "USD", LaptopId),
            Product.Create("PROD-MOUSE-02", "Wireless Ergonomic Mouse", 49.99m, 200, "USD", MouseId),
            Product.Create("PROD-KEYBOARD-03", "Mechanical Keyboard", 89.50m, 150, "USD", KeyboardId),
            Product.Create("PROD-MONITOR-04", "4K Ultra-HD Monitor 27-inch", 399.00m, 75, "USD", MonitorId),
            Product.Create("PROD-HEADSET-05", "Noise Cancelling Headset", 149.00m, 100, "USD", HeadsetId)
        };

        await db.Products.AddRangeAsync(products, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Catalog database successfully seeded with 5 fixed test products.");
    }
}
