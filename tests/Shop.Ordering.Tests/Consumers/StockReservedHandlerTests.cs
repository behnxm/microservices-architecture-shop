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

public sealed class StockReservedHandlerTests
{
    private static OrderingDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<OrderingDbContext>()
            .UseInMemoryDatabase($"ordering_reserved_db_{Guid.NewGuid()}")
            .Options;

        return new OrderingDbContext(options);
    }

    [Fact]
    public async Task HandleAsync_Confirms_Pending_Order_And_Enqueues_OrderConfirmedIntegrationEvent()
    {
        // Arrange
        await using var db = CreateDbContext();
        var outbox = new EfOutbox<OrderingDbContext>(db);
        var handler = new StockReservedHandler(db, outbox, NullLogger<StockReservedHandler>.Instance);

        var order = Order.Create(
            Guid.NewGuid(),
            "alice@example.com",
            [OrderLine.Create(Guid.NewGuid(), "Desk Mat", 29.99m, 1)]);

        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var @event = new StockReservedIntegrationEvent(
            MessageId: Guid.NewGuid(),
            OccurredOn: DateTimeOffset.UtcNow,
            OrderId: order.Id);

        // Act
        await handler.HandleAsync(@event);

        // Assert: Order status Confirmed
        var updatedOrder = await db.Orders.FirstAsync(o => o.Id == order.Id);
        updatedOrder.Status.ShouldBe(OrderStatus.Confirmed);

        // Assert: Outbox has OrderConfirmed event
        var outboxMsg = await db.OutboxMessages.FirstOrDefaultAsync(m => m.RoutingKey == "ordering.order-confirmed.v1");
        outboxMsg.ShouldNotBeNull();
        outboxMsg.Payload.ShouldContain(order.Id.ToString());
        outboxMsg.Payload.ShouldContain("alice@example.com");
    }

    [Fact]
    public async Task HandleAsync_On_Already_Confirmed_Order_Is_Safe_NoOp()
    {
        // Arrange
        await using var db = CreateDbContext();
        var outbox = new EfOutbox<OrderingDbContext>(db);
        var handler = new StockReservedHandler(db, outbox, NullLogger<StockReservedHandler>.Instance);

        var order = Order.Create(
            Guid.NewGuid(),
            "alice@example.com",
            [OrderLine.Create(Guid.NewGuid(), "Desk Mat", 29.99m, 1)]);
        order.Confirm();

        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var @event = new StockReservedIntegrationEvent(
            MessageId: Guid.NewGuid(),
            OccurredOn: DateTimeOffset.UtcNow,
            OrderId: order.Id);

        // Act
        await handler.HandleAsync(@event);

        // Assert: Outbox should NOT contain a new event
        var outboxMsg = await db.OutboxMessages.FirstOrDefaultAsync(m => m.RoutingKey == "ordering.order-confirmed.v1");
        outboxMsg.ShouldBeNull();
    }
}
