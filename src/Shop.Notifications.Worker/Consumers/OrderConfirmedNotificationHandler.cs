using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shop.BuildingBlocks.Messaging.Consumers;
using Shop.Contracts;
using Shop.Notifications.Worker.Data;
using Shop.Notifications.Worker.Email;

namespace Shop.Notifications.Worker.Consumers;

public sealed class OrderConfirmedNotificationHandler : IIntegrationEventHandler<OrderConfirmedIntegrationEvent>
{
    private readonly NotificationsDbContext _db;
    private readonly IEmailSender _emailSender;
    private readonly ILogger<OrderConfirmedNotificationHandler> _logger;

    public OrderConfirmedNotificationHandler(
        NotificationsDbContext db,
        IEmailSender emailSender,
        ILogger<OrderConfirmedNotificationHandler> logger)
    {
        _db = db;
        _emailSender = emailSender;
        _logger = logger;
    }

    public async Task HandleAsync(OrderConfirmedIntegrationEvent eventMessage, CancellationToken cancellationToken = default)
    {
        // Natural key idempotency check: (OrderId, "OrderConfirmed")
        var alreadySent = await _db.Notifications
            .AsNoTracking()
            .AnyAsync(n => n.OrderId == eventMessage.OrderId && n.Type == "OrderConfirmed", cancellationToken);

        if (alreadySent)
        {
            _logger.LogInformation(
                "OrderConfirmed notification for Order {OrderId} was already sent. Skipping due to natural key deduplication.",
                eventMessage.OrderId);
            return;
        }

        var subject = $"Order {eventMessage.OrderId} Confirmed";
        var body = $"Hello! Your order {eventMessage.OrderId} for {eventMessage.Total:F2} {eventMessage.Currency} has been confirmed and is being prepared.";

        await _emailSender.SendEmailAsync(eventMessage.CustomerEmail, subject, body, cancellationToken);

        var notification = Notification.Create(
            eventMessage.OrderId,
            "OrderConfirmed",
            eventMessage.CustomerEmail,
            "Sent");

        _db.Notifications.Add(notification);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("OrderConfirmed notification recorded for Order {OrderId}", eventMessage.OrderId);
    }
}
