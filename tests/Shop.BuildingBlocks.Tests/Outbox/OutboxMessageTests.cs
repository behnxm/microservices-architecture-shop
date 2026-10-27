using System;
using System.Text.Json;
using Shop.BuildingBlocks.Messaging.Outbox;
using Shop.Contracts;
using Shouldly;
using Xunit;

namespace Shop.BuildingBlocks.Tests.Outbox;

public sealed class OutboxMessageTests
{
    [Fact]
    public void Create_From_Integration_Event_Extracts_Metadata_And_Serializes_Payload()
    {
        // Arrange
        var messageId = Guid.NewGuid();
        var occurredOn = DateTimeOffset.UtcNow;
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var @event = new OrderPlacedIntegrationEvent(
            MessageId: messageId,
            OccurredOn: occurredOn,
            OrderId: orderId,
            CustomerId: customerId,
            CustomerEmail: "charlie@example.com",
            Items: [new OrderLineItemContract(Guid.NewGuid(), 2)]);

        // Act
        var outboxMessage = OutboxMessage.Create(@event);

        // Assert
        outboxMessage.Id.ShouldBe(messageId);
        outboxMessage.OccurredOn.ShouldBe(occurredOn);
        outboxMessage.RoutingKey.ShouldBe("ordering.order-placed.v1");
        outboxMessage.MessageType.ShouldBe(typeof(OrderPlacedIntegrationEvent).FullName);
        outboxMessage.ProcessedOn.ShouldBeNull();
        outboxMessage.RetryCount.ShouldBe(0);
        outboxMessage.LastError.ShouldBeNull();

        // Verify JSON payload
        using var doc = JsonDocument.Parse(outboxMessage.Payload);
        doc.RootElement.GetProperty("messageId").GetGuid().ShouldBe(messageId);
        doc.RootElement.GetProperty("orderId").GetGuid().ShouldBe(orderId);
        doc.RootElement.GetProperty("customerEmail").GetString().ShouldBe("charlie@example.com");
    }

    [Fact]
    public void MarkProcessed_Sets_ProcessedOn_And_Clears_Error()
    {
        var @event = new StockReservedIntegrationEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid());
        var message = OutboxMessage.Create(@event);
        message.RecordFailure("Network timeout");

        var processedTime = DateTimeOffset.UtcNow;
        message.MarkProcessed(processedTime);

        message.ProcessedOn.ShouldBe(processedTime);
        message.LastError.ShouldBeNull();
    }

    [Fact]
    public void RecordFailure_Increments_RetryCount_And_Records_Error()
    {
        var @event = new StockReservedIntegrationEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid());
        var message = OutboxMessage.Create(@event);

        message.RecordFailure("Broker unreachable");

        message.RetryCount.ShouldBe(1);
        message.LastError.ShouldBe("Broker unreachable");

        message.RecordFailure("Connection timed out");
        message.RetryCount.ShouldBe(2);
        message.LastError.ShouldBe("Connection timed out");
    }
}
