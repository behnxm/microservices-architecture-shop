using System;
using System.Reflection;
using System.Text.Json;
using Shop.Contracts;

namespace Shop.BuildingBlocks.Messaging.Outbox;

public sealed class OutboxMessage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public Guid Id { get; private set; }
    public string MessageType { get; private set; } = default!;
    public string RoutingKey { get; private set; } = default!;
    public string Payload { get; private set; } = default!;
    public DateTimeOffset OccurredOn { get; private set; }
    public DateTimeOffset? ProcessedOn { get; private set; }
    public int RetryCount { get; private set; }
    public string? LastError { get; private set; }

    private OutboxMessage() { }

    public static OutboxMessage Create<T>(T message, string? routingKey = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);

        var messageType = typeof(T).FullName ?? typeof(T).Name;
        var attr = typeof(T).GetCustomAttribute<IntegrationEventAttribute>();
        var resolvedRoutingKey = routingKey ?? attr?.ContractName ?? typeof(T).Name.ToLowerInvariant();

        // Extract MessageId and OccurredOn if present on contract, otherwise generate
        var idProp = typeof(T).GetProperty("MessageId");
        var messageId = idProp?.GetValue(message) as Guid? ?? Guid.CreateVersion7();

        var occurredProp = typeof(T).GetProperty("OccurredOn");
        var occurredOn = occurredProp?.GetValue(message) as DateTimeOffset? ?? DateTimeOffset.UtcNow;

        var json = JsonSerializer.Serialize(message, JsonOptions);

        return new OutboxMessage
        {
            Id = messageId,
            MessageType = messageType,
            RoutingKey = resolvedRoutingKey,
            Payload = json,
            OccurredOn = occurredOn,
            ProcessedOn = null,
            RetryCount = 0,
            LastError = null
        };
    }

    public void MarkProcessed(DateTimeOffset processedOn)
    {
        ProcessedOn = processedOn;
        LastError = null;
    }

    public void RecordFailure(string error)
    {
        RetryCount++;
        LastError = error;
    }
}
