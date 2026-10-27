using Microsoft.EntityFrameworkCore;
using Shop.BuildingBlocks.Messaging.Data;
using Shop.BuildingBlocks.Messaging.Inbox;
using Shop.BuildingBlocks.Messaging.Outbox;

namespace Shop.Notifications.Worker.Data;

public sealed class NotificationsDbContext : DbContext
{
    public NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : base(options) { }

    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyMessagingConfigurations();

        modelBuilder.Entity<Notification>(b =>
        {
            b.ToTable("notifications");
            b.HasKey(n => new { n.OrderId, n.Type });
            b.Property(n => n.OrderId).IsRequired();
            b.Property(n => n.Type).HasMaxLength(64).IsRequired();
            b.Property(n => n.RecipientEmail).HasMaxLength(256).IsRequired();
            b.Property(n => n.Status).HasMaxLength(32).IsRequired();
            b.Property(n => n.SentAt).IsRequired();
        });
    }
}
