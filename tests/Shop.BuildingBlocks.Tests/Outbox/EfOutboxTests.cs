using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Shop.BuildingBlocks.Messaging.Outbox;
using Shop.Contracts;
using Shouldly;
using Xunit;

namespace Shop.BuildingBlocks.Tests.Outbox;

public sealed class EfOutboxTests
{
    private static TestMessagingDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<TestMessagingDbContext>()
            .UseInMemoryDatabase($"outbox_test_{Guid.NewGuid()}")
            .Options;

        return new TestMessagingDbContext(options);
    }

    [Fact]
    public async Task Enqueue_Persists_Outbox_Message_In_Database_Upon_Save()
    {
        // Arrange
        await using var db = CreateDbContext();
        var outbox = new EfOutbox<TestMessagingDbContext>(db);

        var messageId = Guid.NewGuid();
        var @event = new OrderConfirmedIntegrationEvent(
            MessageId: messageId,
            OccurredOn: DateTimeOffset.UtcNow,
            OrderId: Guid.NewGuid(),
            CustomerId: Guid.NewGuid(),
            CustomerEmail: "alice@example.com",
            Total: 199.99m,
            Currency: "USD");

        // Act
        outbox.Enqueue(@event);
        await db.SaveChangesAsync();

        // Assert
        var saved = await db.OutboxMessages.FirstOrDefaultAsync(m => m.Id == messageId);
        saved.ShouldNotBeNull();
        saved.RoutingKey.ShouldBe("ordering.order-confirmed.v1");
        saved.ProcessedOn.ShouldBeNull();
    }

    [Fact]
    public async Task Unsaved_Outbox_Message_Is_Not_Persisted_If_Transaction_Aborts()
    {
        // Arrange
        var dbName = $"outbox_abort_{Guid.NewGuid()}";
        var options = new DbContextOptionsBuilder<TestMessagingDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        var messageId = Guid.NewGuid();
        var @event = new OrderRejectedIntegrationEvent(
            MessageId: messageId,
            OccurredOn: DateTimeOffset.UtcNow,
            OrderId: Guid.NewGuid(),
            CustomerId: Guid.NewGuid(),
            CustomerEmail: "bob@example.com",
            Reason: "Inventory exhausted");

        // Act: Enqueue without saving, simulate exception
        await using (var db = new TestMessagingDbContext(options))
        {
            var outbox = new EfOutbox<TestMessagingDbContext>(db);
            outbox.Enqueue(@event);
            // Simulate crash / discard without SaveChangesAsync
        }

        // Assert: Read with fresh DbContext
        await using (var db = new TestMessagingDbContext(options))
        {
            var saved = await db.OutboxMessages.FirstOrDefaultAsync(m => m.Id == messageId);
            saved.ShouldBeNull();
        }
    }
}
