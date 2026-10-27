using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;
using Shop.BuildingBlocks.Messaging.RabbitMq;
using Shouldly;
using Xunit;

namespace Shop.BuildingBlocks.Tests.RabbitMq;

public sealed class TopologyAndTraceTests
{
    [Fact]
    public async Task RabbitMqTopology_Declares_Exchange_And_DLQ_Topology()
    {
        // Arrange
        var channel = Substitute.For<IChannel>();

        // Act
        await RabbitMqTopology.DeclareAndBindQueueAsync(
            channel,
            queueName: "catalog.order-placed",
            routingKey: "ordering.order-placed.v1");

        // Assert: Exchange declared
        await channel.Received(1).ExchangeDeclareAsync(
            exchange: "shop.events",
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: Arg.Any<CancellationToken>());

        // Assert: DLX declared
        await channel.Received(1).ExchangeDeclareAsync(
            exchange: "shop.events.dlx",
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: Arg.Any<CancellationToken>());

        // Assert: DLQ declared and bound
        await channel.Received(1).QueueDeclareAsync(
            queue: "catalog.order-placed.dlq",
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: Arg.Any<CancellationToken>());

        await channel.Received(1).QueueBindAsync(
            queue: "catalog.order-placed.dlq",
            exchange: "shop.events.dlx",
            routingKey: "catalog.order-placed.dlq",
            cancellationToken: Arg.Any<CancellationToken>());

        // Assert: Primary queue declared with DLX configuration
        await channel.Received(1).QueueDeclareAsync(
            queue: "catalog.order-placed",
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: Arg.Is<IDictionary<string, object?>>(args =>
                args["x-dead-letter-exchange"]!.ToString() == "shop.events.dlx" &&
                args["x-dead-letter-routing-key"]!.ToString() == "catalog.order-placed.dlq"),
            cancellationToken: Arg.Any<CancellationToken>());

        await channel.Received(1).QueueBindAsync(
            queue: "catalog.order-placed",
            exchange: "shop.events",
            routingKey: "ordering.order-placed.v1",
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Publisher_Injects_Ambient_Activity_Traceparent_Header()
    {
        // Arrange
        var channel = Substitute.For<IChannel>();
        var connection = Substitute.For<IRabbitMqConnection>();
        connection.CreateChannelAsync(Arg.Any<CreateChannelOptions>(), Arg.Any<CancellationToken>())
            .Returns(channel);

        var options = Options.Create(new RabbitMqOptions());
        var publisher = new RabbitMqEventPublisher(connection, options, NullLogger<RabbitMqEventPublisher>.Instance);

        var testActivitySource = new ActivitySource("TestPublisherSource");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = testActivitySource.StartActivity("TestOperation");
        activity.ShouldNotBeNull();

        BasicProperties? capturedProps = null;
        await channel.BasicPublishAsync(
            exchange: Arg.Any<string>(),
            routingKey: Arg.Any<string>(),
            mandatory: Arg.Any<bool>(),
            basicProperties: Arg.Do<BasicProperties>(p => capturedProps = p),
            body: Arg.Any<ReadOnlyMemory<byte>>(),
            cancellationToken: Arg.Any<CancellationToken>());

        // Act
        await publisher.PublishAsync(
            routingKey: "ordering.order-placed.v1",
            messageType: "OrderPlaced",
            payload: "{}");

        // Assert
        capturedProps.ShouldNotBeNull();
        capturedProps.Headers.ShouldNotBeNull();
        capturedProps.Headers.ContainsKey("traceparent").ShouldBeTrue();
        capturedProps.Headers["traceparent"]!.ToString().ShouldBe(activity.Id);
    }
}
