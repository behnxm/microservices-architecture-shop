using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shop.BuildingBlocks.Messaging.Consumers;
using Shop.BuildingBlocks.Messaging.Outbox;
using Shop.Contracts;
using Shop.Ordering.Api.Data;
using Shop.Ordering.Api.Orders;

namespace Shop.Ordering.Api.Consumers;

public sealed class StockReservedHandler : IIntegrationEventHandler<StockReservedIntegrationEvent>
{
    private readonly OrderingDbContext _db;
    private readonly IOutbox _outbox;
    private readonly ILogger<StockReservedHandler> _logger;

    public StockReservedHandler(
        OrderingDbContext db,
        IOutbox outbox,
        ILogger<StockReservedHandler> logger)
    {
        _db = db;
        _outbox = outbox;
        _logger = logger;
    }

    public async Task HandleAsync(StockReservedIntegrationEvent eventMessage, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Confirming Order {OrderId} following stock reservation", eventMessage.OrderId);

        var order = await _db.Orders
            .FirstOrDefaultAsync(o => o.Id == eventMessage.OrderId, cancellationToken);

        if (order is null)
        {
            _logger.LogError("Order {OrderId} not found during stock reservation confirmation.", eventMessage.OrderId);
            return;
        }

        if (order.Status == OrderStatus.Confirmed)
        {
            _logger.LogInformation("Order {OrderId} is already confirmed. Skipping.", order.Id);
            return;
        }

        order.Confirm();

        var confirmedEvent = new OrderConfirmedIntegrationEvent(
            MessageId: Guid.CreateVersion7(),
            OccurredOn: DateTimeOffset.UtcNow,
            OrderId: order.Id,
            CustomerId: order.CustomerId,
            CustomerEmail: order.CustomerEmail,
            Total: order.Total,
            Currency: order.Currency);

        _outbox.Enqueue(confirmedEvent);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Order {OrderId} successfully confirmed and OrderConfirmed event queued to outbox", order.Id);
    }
}
