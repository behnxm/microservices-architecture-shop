using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RabbitMQ.Client;

namespace Shop.BuildingBlocks.Messaging.RabbitMq;

public static class RabbitMqTopology
{
    public const string DefaultExchange = "shop.events";
    public const string DeadLetterExchange = "shop.events.dlx";

    public static async Task DeclareTopologyAsync(
        IChannel channel,
        string exchange = DefaultExchange,
        string dlxExchange = DeadLetterExchange,
        CancellationToken cancellationToken = default)
    {
        // 1. Declare primary topic exchange
        await channel.ExchangeDeclareAsync(
            exchange: exchange,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);

        // 2. Declare dead-letter topic exchange
        await channel.ExchangeDeclareAsync(
            exchange: dlxExchange,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);
    }

    public static async Task DeclareAndBindQueueAsync(
        IChannel channel,
        string queueName,
        string routingKey,
        string exchange = DefaultExchange,
        string dlxExchange = DeadLetterExchange,
        CancellationToken cancellationToken = default)
    {
        await DeclareTopologyAsync(channel, exchange, dlxExchange, cancellationToken);

        var dlqName = $"{queueName}.dlq";
        var dlqRoutingKey = $"{queueName}.dlq";

        // 1. Declare DLQ
        await channel.QueueDeclareAsync(
            queue: dlqName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: cancellationToken);

        // 2. Bind DLQ to DLX
        await channel.QueueBindAsync(
            queue: dlqName,
            exchange: dlxExchange,
            routingKey: dlqRoutingKey,
            cancellationToken: cancellationToken);

        // 3. Declare primary queue configured with dead-letter exchange
        var queueArgs = new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = dlxExchange,
            ["x-dead-letter-routing-key"] = dlqRoutingKey
        };

        await channel.QueueDeclareAsync(
            queue: queueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: queueArgs,
            cancellationToken: cancellationToken);

        // 4. Bind primary queue to primary exchange
        await channel.QueueBindAsync(
            queue: queueName,
            exchange: exchange,
            routingKey: routingKey,
            cancellationToken: cancellationToken);
    }
}
