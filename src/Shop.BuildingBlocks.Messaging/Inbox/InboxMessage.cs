using System;

namespace Shop.BuildingBlocks.Messaging.Inbox;

public sealed class InboxMessage
{
    public Guid MessageId { get; private set; }
    public string HandlerName { get; private set; } = default!;
    public DateTimeOffset ReceivedOn { get; private set; }
    public DateTimeOffset? ProcessedOn { get; private set; }

    private InboxMessage() { }

    public static InboxMessage Create(Guid messageId, string handlerName)
    {
        if (messageId == Guid.Empty)
        {
            throw new ArgumentException("MessageId cannot be empty.", nameof(messageId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(handlerName);

        return new InboxMessage
        {
            MessageId = messageId,
            HandlerName = handlerName.Trim(),
            ReceivedOn = DateTimeOffset.UtcNow,
            ProcessedOn = null
        };
    }

    public void MarkProcessed(DateTimeOffset processedOn)
    {
        ProcessedOn = processedOn;
    }
}
