using System;

namespace Shop.Notifications.Worker.Data;

public sealed class Notification
{
    public Guid OrderId { get; private set; }
    public string Type { get; private set; } = default!;
    public string RecipientEmail { get; private set; } = default!;
    public string Status { get; private set; } = default!;
    public DateTimeOffset SentAt { get; private set; }

    private Notification() { }

    public static Notification Create(Guid orderId, string type, string recipientEmail, string status = "Sent")
    {
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("OrderId cannot be empty.", nameof(orderId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        return new Notification
        {
            OrderId = orderId,
            Type = type.Trim(),
            RecipientEmail = recipientEmail.Trim().ToLowerInvariant(),
            Status = status.Trim(),
            SentAt = DateTimeOffset.UtcNow
        };
    }
}
