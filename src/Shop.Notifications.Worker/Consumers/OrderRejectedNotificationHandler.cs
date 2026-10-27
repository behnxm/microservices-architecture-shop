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

public sealed class OrderRejectedNotificationHandler : IIntegrationEventHandler<OrderRejectedIntegrationEvent>
{
    private readonly NotificationsDbContext _db;
    private readonly IEmailSender _emailSender;
    private readonly ILogger<OrderRejectedNotificationHandler> _logger;

    public OrderRejectedNotificationHandler(
        NotificationsDbContext db,
        IEmailSender emailSender,
        ILogger<OrderRejectedNotificationHandler> logger)
    {
        _db = db;
        _emailSender = emailSender;
        _logger = logger;
    }

    public async Task HandleAsync(OrderRejectedIntegrationEvent eventMessage, CancellationToken cancellationToken = default)
    {
        // Natural key idempotency check: (OrderId, "OrderRejected")
        var alreadySent = await _db.Notifications
            .AsNoTracking()
            .AnyAsync(n => n.OrderId == eventMessage.OrderId && n.Type == "OrderRejected", cancellationToken);

        if (alreadySent)
        {
            _logger.LogInformation(
                "OrderRejected notification for Order {OrderId} was already sent. Skipping due to natural key deduplication.",
                eventMessage.OrderId);
            return;
        }

        var subject = $"Order {eventMessage.OrderId} Cancelled";
        var body = $"Hello! We regret to inform you that your order {eventMessage.OrderId} could not be completed. Reason: {eventMessage.Reason}";

        await _emailSender.SendEmailAsync(eventMessage.CustomerEmail, subject, body, cancellationToken);

        var notification = Notification.Create(
            eventMessage.OrderId,
            "OrderRejected",
            eventMessage.CustomerEmail,
            "Sent");

        _db.Notifications.Add(notification);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("OrderRejected notification recorded for Order {OrderId}", eventMessage.OrderId);
    }
}
