using Microsoft.EntityFrameworkCore;
using Shop.BuildingBlocks.Messaging.Data;
using Shop.BuildingBlocks.Messaging.Inbox;
using Shop.BuildingBlocks.Messaging.Outbox;

namespace Shop.BuildingBlocks.Tests;

public sealed class TestMessagingDbContext : DbContext
{
    public TestMessagingDbContext(DbContextOptions<TestMessagingDbContext> options) : base(options) { }

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyMessagingConfigurations();
    }
}
