using Microsoft.EntityFrameworkCore;
using Shop.BuildingBlocks.Messaging.Data;
using Shop.BuildingBlocks.Messaging.Inbox;
using Shop.BuildingBlocks.Messaging.Outbox;
using Shop.Catalog.Api.Products;

namespace Shop.Catalog.Api.Data;

public sealed class CatalogDbContext : DbContext
{
    public CatalogDbContext(DbContextOptions<CatalogDbContext> options) : base(options) { }

    public DbSet<Product> Products => Set<Product>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyMessagingConfigurations();

        modelBuilder.Entity<Product>(b =>
        {
            b.ToTable("products");
            b.HasKey(p => p.Id);
            b.Property(p => p.Sku).HasMaxLength(64).IsRequired();
            b.HasIndex(p => p.Sku).IsUnique();
            b.Property(p => p.Name).HasMaxLength(256).IsRequired();
            b.Property(p => p.Price).HasPrecision(18, 2).IsRequired();
            b.Property(p => p.Currency).HasMaxLength(3).IsRequired();
            b.Property(p => p.AvailableStock).IsRequired();
            b.Property(p => p.CreatedAt).IsRequired();
            b.Property(p => p.UpdatedAt).IsRequired();
            b.Property(p => p.Version).IsConcurrencyToken().IsRequired();
        });
    }
}
