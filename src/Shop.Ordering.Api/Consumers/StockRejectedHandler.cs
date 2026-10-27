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

public sealed class StockRejectedHandler : IIntegrationEventHandler<StockRejectedIntegrationEvent>
{
    private readonly OrderingDbContext _db;
    private readonly IOutbox _outbox;
    private readonly ILogger<StockRejectedHandler> _logger;

    public StockRejectedHandler(
        OrderingDbContext db,
        IOutbox outbox,
        ILogger<StockRejectedHandler> logger)
    {
        _db = db;
        _outbox = outbox;
        _logger = logger;
    }

    public async Task HandleAsync(StockRejectedIntegrationEvent eventMessage, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("Rejecting Order {OrderId} following stock reservation failure: {Reason}",
            eventMessage.OrderId, eventMessage.Reason);

        var order = await _db.Orders
            .FirstOrDefaultAsync(o => o.Id == eventMessage.OrderId, cancellationToken);

        if (order is null)
        {
            _logger.LogError("Order {OrderId} not found during stock rejection processing.", eventMessage.OrderId);
            return;
        }

        if (order.Status == OrderStatus.Rejected)
        {
            _logger.LogInformation("Order {OrderId} is already rejected. Skipping.", order.Id);
            return;
        }

        order.Reject(eventMessage.Reason);

        var rejectedEvent = new OrderRejectedIntegrationEvent(
            MessageId: Guid.CreateVersion7(),
            OccurredOn: DateTimeOffset.UtcNow,
            OrderId: order.Id,
            CustomerId: order.CustomerId,
            CustomerEmail: order.CustomerEmail,
            Reason: eventMessage.Reason);

        _outbox.Enqueue(rejectedEvent);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Order {OrderId} successfully rejected and OrderRejected event queued to outbox", order.Id);
    }
}
