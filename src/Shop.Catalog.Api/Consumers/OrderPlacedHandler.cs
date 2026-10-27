using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shop.BuildingBlocks.Messaging.Consumers;
using Shop.BuildingBlocks.Messaging.Outbox;
using Shop.Catalog.Api.Data;
using Shop.Contracts;

namespace Shop.Catalog.Api.Consumers;

public sealed class OrderPlacedHandler : IIntegrationEventHandler<OrderPlacedIntegrationEvent>
{
    private readonly CatalogDbContext _db;
    private readonly IOutbox _outbox;
    private readonly ILogger<OrderPlacedHandler> _logger;

    public OrderPlacedHandler(
        CatalogDbContext db,
        IOutbox outbox,
        ILogger<OrderPlacedHandler> logger)
    {
        _db = db;
        _outbox = outbox;
        _logger = logger;
    }

    public async Task HandleAsync(OrderPlacedIntegrationEvent eventMessage, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Processing stock reservation for Order {OrderId} with {ItemCount} items",
            eventMessage.OrderId, eventMessage.Items.Count);

        var requestedItems = eventMessage.Items;
        var productIds = requestedItems.Select(i => i.ProductId).Distinct().ToList();

        var products = await _db.Products
            .Where(p => productIds.Contains(p.Id))
            .ToListAsync(cancellationToken);

        var productMap = products.ToDictionary(p => p.Id);
        var unavailable = new List<Guid>();

        foreach (var item in requestedItems)
        {
            if (!productMap.TryGetValue(item.ProductId, out var product) || product.AvailableStock < item.Quantity)
            {
                unavailable.Add(item.ProductId);
            }
        }

        if (unavailable.Count == 0)
        {
            // All-or-nothing stock reservation
            foreach (var item in requestedItems)
            {
                var product = productMap[item.ProductId];
                product.Reserve(item.Quantity);
            }

            var reservedEvent = new StockReservedIntegrationEvent(
                MessageId: Guid.CreateVersion7(),
                OccurredOn: DateTimeOffset.UtcNow,
                OrderId: eventMessage.OrderId);

            _outbox.Enqueue(reservedEvent);
            _logger.LogInformation("Stock reserved successfully for Order {OrderId}", eventMessage.OrderId);
        }
        else
        {
            // Rejection event for choreography compensation
            var rejectedEvent = new StockRejectedIntegrationEvent(
                MessageId: Guid.CreateVersion7(),
                OccurredOn: DateTimeOffset.UtcNow,
                OrderId: eventMessage.OrderId,
                Reason: $"Insufficient stock for product IDs: {string.Join(", ", unavailable)}",
                UnavailableProductIds: unavailable);

            _outbox.Enqueue(rejectedEvent);
            _logger.LogWarning("Stock rejected for Order {OrderId}. Unavailable products: {Products}",
                eventMessage.OrderId, string.Join(", ", unavailable));
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}
