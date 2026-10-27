using Microsoft.EntityFrameworkCore;
using Shop.BuildingBlocks.Messaging.Data;
using Shop.BuildingBlocks.Messaging.Inbox;
using Shop.BuildingBlocks.Messaging.Outbox;
using Shop.Ordering.Api.Idempotency;
using Shop.Ordering.Api.Orders;

namespace Shop.Ordering.Api.Data;

public sealed class OrderingDbContext : DbContext
{
    public OrderingDbContext(DbContextOptions<OrderingDbContext> options) : base(options) { }

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyMessagingConfigurations();

        modelBuilder.Entity<Order>(b =>
        {
            b.ToTable("orders");
            b.HasKey(o => o.Id);
            b.Property(o => o.CustomerId).IsRequired();
            b.Property(o => o.CustomerEmail).HasMaxLength(256).IsRequired();
            b.Property(o => o.Status).HasConversion<int>().IsRequired();
            b.Property(o => o.Total).HasPrecision(18, 2).IsRequired();
            b.Property(o => o.Currency).HasMaxLength(3).IsRequired();
            b.Property(o => o.RejectionReason).HasMaxLength(512);
            b.Property(o => o.CreatedAt).IsRequired();
            b.Property(o => o.UpdatedAt).IsRequired();
            b.Property(o => o.Version).IsConcurrencyToken().IsRequired();

            b.HasMany(o => o.Lines)
                .WithOne()
                .HasForeignKey(l => l.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            b.Navigation(o => o.Lines).AutoInclude();
        });

        modelBuilder.Entity<OrderLine>(b =>
        {
            b.ToTable("order_lines");
            b.HasKey(l => l.Id);
            b.Property(l => l.ProductId).IsRequired();
            b.Property(l => l.ProductName).HasMaxLength(256).IsRequired();
            b.Property(l => l.UnitPrice).HasPrecision(18, 2).IsRequired();
            b.Property(l => l.Quantity).IsRequired();
        });

        modelBuilder.Entity<IdempotencyRecord>(b =>
        {
            b.ToTable("idempotency_records");
            b.HasKey(r => r.Key);
            b.Property(r => r.Key).HasMaxLength(64);
            b.Property(r => r.RequestHash).HasMaxLength(64).IsRequired();
            b.Property(r => r.OrderId).IsRequired();
            b.Property(r => r.StatusCode).IsRequired();
            b.Property(r => r.ResponseBody).IsRequired();
            b.Property(r => r.CreatedAt).IsRequired();
        });
    }
}
