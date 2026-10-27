using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shop.BuildingBlocks.Messaging.Outbox;
using Shop.Contracts;
using Shop.Ordering.Api.Consumers;
using Shop.Ordering.Api.Data;
using Shop.Ordering.Api.Orders;
using Shouldly;
using Xunit;

namespace Shop.Ordering.Tests.Consumers;

public sealed class StockRejectedHandlerTests
{
    private static OrderingDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<OrderingDbContext>()
            .UseInMemoryDatabase($"ordering_rejected_db_{Guid.NewGuid()}")
            .Options;

        return new OrderingDbContext(options);
    }

    [Fact]
    public async Task HandleAsync_Rejects_Pending_Order_And_Enqueues_OrderRejectedIntegrationEvent()
    {
        // Arrange
        await using var db = CreateDbContext();
        var outbox = new EfOutbox<OrderingDbContext>(db);
        var handler = new StockRejectedHandler(db, outbox, NullLogger<StockRejectedHandler>.Instance);

        var order = Order.Create(
            Guid.NewGuid(),
            "bob@example.com",
            [OrderLine.Create(Guid.NewGuid(), "Gaming Mouse", 75.00m, 1)]);

        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var @event = new StockRejectedIntegrationEvent(
            MessageId: Guid.NewGuid(),
            OccurredOn: DateTimeOffset.UtcNow,
            OrderId: order.Id,
            Reason: "Warehouse stock depleted",
            UnavailableProductIds: []);

        // Act
        await handler.HandleAsync(@event);

        // Assert: Order status Rejected
        var updatedOrder = await db.Orders.FirstAsync(o => o.Id == order.Id);
        updatedOrder.Status.ShouldBe(OrderStatus.Rejected);
        updatedOrder.RejectionReason.ShouldBe("Warehouse stock depleted");

        // Assert: Outbox has OrderRejected event
        var outboxMsg = await db.OutboxMessages.FirstOrDefaultAsync(m => m.RoutingKey == "ordering.order-rejected.v1");
        outboxMsg.ShouldNotBeNull();
        outboxMsg.Payload.ShouldContain(order.Id.ToString());
        outboxMsg.Payload.ShouldContain("Warehouse stock depleted");
    }

    [Fact]
    public async Task HandleAsync_On_Already_Rejected_Order_Is_Safe_NoOp()
    {
        // Arrange
        await using var db = CreateDbContext();
        var outbox = new EfOutbox<OrderingDbContext>(db);
        var handler = new StockRejectedHandler(db, outbox, NullLogger<StockRejectedHandler>.Instance);

        var order = Order.Create(
            Guid.NewGuid(),
            "bob@example.com",
            [OrderLine.Create(Guid.NewGuid(), "Gaming Mouse", 75.00m, 1)]);
        order.Reject("Original reason");

        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var @event = new StockRejectedIntegrationEvent(
            MessageId: Guid.NewGuid(),
            OccurredOn: DateTimeOffset.UtcNow,
            OrderId: order.Id,
            Reason: "Different reason",
            UnavailableProductIds: []);

        // Act
        await handler.HandleAsync(@event);

        // Assert: Outbox should NOT contain a new event
        var outboxMsg = await db.OutboxMessages.FirstOrDefaultAsync(m => m.RoutingKey == "ordering.order-rejected.v1");
        outboxMsg.ShouldBeNull();
    }
}
