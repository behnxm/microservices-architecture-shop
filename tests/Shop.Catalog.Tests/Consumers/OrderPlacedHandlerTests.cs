using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shop.BuildingBlocks.Messaging.Outbox;
using Shop.Catalog.Api.Consumers;
using Shop.Catalog.Api.Data;
using Shop.Catalog.Api.Products;
using Shop.Contracts;
using Shouldly;
using Xunit;

namespace Shop.Catalog.Tests.Consumers;

public sealed class OrderPlacedHandlerTests
{
    private static CatalogDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseInMemoryDatabase($"catalog_handler_db_{Guid.NewGuid()}")
            .Options;

        return new CatalogDbContext(options);
    }

    [Fact]
    public async Task HandleAsync_With_Sufficient_Stock_Reserves_Stock_And_Enqueues_StockReservedIntegrationEvent()
    {
        // Arrange
        await using var db = CreateDbContext();
        var outbox = new EfOutbox<CatalogDbContext>(db);
        var handler = new OrderPlacedHandler(db, outbox, NullLogger<OrderPlacedHandler>.Instance);

        var product = Product.Create("KB-001", "Mechanical Keyboard", 120m, 10);
        db.Products.Add(product);
        await db.SaveChangesAsync();

        var orderId = Guid.NewGuid();
        var @event = new OrderPlacedIntegrationEvent(
            MessageId: Guid.NewGuid(),
            OccurredOn: DateTimeOffset.UtcNow,
            OrderId: orderId,
            CustomerId: Guid.NewGuid(),
            CustomerEmail: "alice@example.com",
            Items: [new OrderLineItemContract(product.Id, 3)]);

        // Act
        await handler.HandleAsync(@event);

        // Assert: Product stock deducted
        var updatedProduct = await db.Products.FirstAsync(p => p.Id == product.Id);
        updatedProduct.AvailableStock.ShouldBe(7);

        // Assert: Outbox has StockReserved event
        var outboxMsg = await db.OutboxMessages.FirstOrDefaultAsync(m => m.RoutingKey == "catalog.stock-reserved.v1");
        outboxMsg.ShouldNotBeNull();
        outboxMsg.Payload.ShouldContain(orderId.ToString());
    }

    [Fact]
    public async Task HandleAsync_With_Insufficient_Stock_Does_Not_Deduct_Stock_And_Enqueues_StockRejectedIntegrationEvent()
    {
        // Arrange
        await using var db = CreateDbContext();
        var outbox = new EfOutbox<CatalogDbContext>(db);
        var handler = new OrderPlacedHandler(db, outbox, NullLogger<OrderPlacedHandler>.Instance);

        var product1 = Product.Create("P1", "Item One", 50m, 2); // only 2 in stock
        var product2 = Product.Create("P2", "Item Two", 25m, 10); // 10 in stock
        db.Products.AddRange(product1, product2);
        await db.SaveChangesAsync();

        var orderId = Guid.NewGuid();
        var @event = new OrderPlacedIntegrationEvent(
            MessageId: Guid.NewGuid(),
            OccurredOn: DateTimeOffset.UtcNow,
            OrderId: orderId,
            CustomerId: Guid.NewGuid(),
            CustomerEmail: "bob@example.com",
            Items:
            [
                new OrderLineItemContract(product1.Id, 5), // Requests 5 (exceeds 2)
                new OrderLineItemContract(product2.Id, 2)  // Requests 2 (available)
            ]);

        // Act
        await handler.HandleAsync(@event);

        // Assert: All-or-nothing: stock remains untouched for both items
        var p1 = await db.Products.FirstAsync(p => p.Id == product1.Id);
        var p2 = await db.Products.FirstAsync(p => p.Id == product2.Id);
        p1.AvailableStock.ShouldBe(2);
        p2.AvailableStock.ShouldBe(10);

        // Assert: Outbox has StockRejected event with product1 listed
        var outboxMsg = await db.OutboxMessages.FirstOrDefaultAsync(m => m.RoutingKey == "catalog.stock-rejected.v1");
        outboxMsg.ShouldNotBeNull();
        outboxMsg.Payload.ShouldContain(orderId.ToString());
        outboxMsg.Payload.ShouldContain(product1.Id.ToString());
    }

    [Fact]
    public async Task HandleAsync_With_Unknown_Product_Enqueues_StockRejectedIntegrationEvent()
    {
        // Arrange
        await using var db = CreateDbContext();
        var outbox = new EfOutbox<CatalogDbContext>(db);
        var handler = new OrderPlacedHandler(db, outbox, NullLogger<OrderPlacedHandler>.Instance);

        var unknownId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var @event = new OrderPlacedIntegrationEvent(
            MessageId: Guid.NewGuid(),
            OccurredOn: DateTimeOffset.UtcNow,
            OrderId: orderId,
            CustomerId: Guid.NewGuid(),
            CustomerEmail: "charlie@example.com",
            Items: [new OrderLineItemContract(unknownId, 1)]);

        // Act
        await handler.HandleAsync(@event);

        // Assert: Outbox has StockRejected event
        var outboxMsg = await db.OutboxMessages.FirstOrDefaultAsync(m => m.RoutingKey == "catalog.stock-rejected.v1");
        outboxMsg.ShouldNotBeNull();
        outboxMsg.Payload.ShouldContain(orderId.ToString());
        outboxMsg.Payload.ShouldContain(unknownId.ToString());
    }
}
