using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Shop.BuildingBlocks.Messaging.Inbox;
using Shop.BuildingBlocks.Messaging.RabbitMq;
using Shop.BuildingBlocks.Messaging.Telemetry;
using Shop.Contracts;

namespace Shop.BuildingBlocks.Messaging.Consumers;

public sealed class EventConsumer<TEvent, THandler, TDbContext> : BackgroundService
    where TEvent : class
    where THandler : class, IIntegrationEventHandler<TEvent>
    where TDbContext : DbContext
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IRabbitMqConnection _connection;
    private readonly ILogger<EventConsumer<TEvent, THandler, TDbContext>> _logger;
    private readonly string _queueName;
    private readonly string _routingKey;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public EventConsumer(
        IServiceProvider serviceProvider,
        IRabbitMqConnection connection,
        ILogger<EventConsumer<TEvent, THandler, TDbContext>> logger,
        string queueName,
        string? routingKey = null)
    {
        _serviceProvider = serviceProvider;
        _connection = connection;
        _logger = logger;
        _queueName = queueName;

        var attr = typeof(TEvent).GetCustomAttribute<IntegrationEventAttribute>();
        _routingKey = !string.IsNullOrWhiteSpace(routingKey)
            ? routingKey
            : attr?.ContractName ?? typeof(TEvent).Name.ToLowerInvariant();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Starting consumer for {Event} on queue {Queue} (RoutingKey: {RoutingKey})",
            typeof(TEvent).Name, _queueName, _routingKey);

        IChannel channel;
        try
        {
            channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);
            await RabbitMqTopology.DeclareAndBindQueueAsync(channel, _queueName, _routingKey, cancellationToken: stoppingToken);
            await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 10, global: false, cancellationToken: stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize RabbitMQ channel/queue for {Queue}", _queueName);
            return;
        }

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (sender, ea) =>
        {
            var sw = Stopwatch.StartNew();

            // 1. Extract W3C distributed trace context
            ActivityContext parentContext = default;
            if (ea.BasicProperties.Headers is not null &&
                ea.BasicProperties.Headers.TryGetValue("traceparent", out var tpObj) &&
                tpObj is not null)
            {
                var tpStr = tpObj switch
                {
                    byte[] bytes => Encoding.UTF8.GetString(bytes),
                    string str => str,
                    _ => tpObj.ToString()
                };

                if (!string.IsNullOrEmpty(tpStr) && ActivityContext.TryParse(tpStr, null, out var parsed))
                {
                    parentContext = parsed;
                }
            }

            using var activity = MessagingTelemetry.ActivitySource.StartActivity(
                $"{typeof(THandler).Name}.Consume",
                ActivityKind.Consumer,
                parentContext);

            if (activity is not null)
            {
                activity.SetTag("messaging.system", "rabbitmq");
                activity.SetTag("messaging.destination", "shop.events");
                activity.SetTag("messaging.source_queue", _queueName);
                activity.SetTag("messaging.routing_key", ea.RoutingKey);
            }

            // 2. Deserialize payload
            TEvent? @event;
            try
            {
                var body = Encoding.UTF8.GetString(ea.Body.ToArray());
                @event = JsonSerializer.Deserialize<TEvent>(body, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deserialize JSON message for {Event}. Rejecting to DLQ.", typeof(TEvent).Name);
                MessagingTelemetry.ConsumerFailedTotal.Add(1);
                await channel.BasicRejectAsync(ea.DeliveryTag, requeue: false, cancellationToken: stoppingToken);
                return;
            }

            if (@event is null)
            {
                _logger.LogWarning("Deserialized null event for {Event}. Acknowledging.", typeof(TEvent).Name);
                await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                return;
            }

            // Extract MessageId
            var idProp = typeof(TEvent).GetProperty("MessageId");
            var messageId = idProp?.GetValue(@event) as Guid?
                ?? (Guid.TryParse(ea.BasicProperties.MessageId, out var parsedId) ? parsedId : Guid.NewGuid());

            if (activity is not null)
            {
                activity.SetTag("messaging.message_id", messageId.ToString());
            }

            // 3. Inbox Deduplication
            using var scope = _serviceProvider.CreateScope();
            var inbox = scope.ServiceProvider.GetRequiredService<IInbox>();
            var handler = scope.ServiceProvider.GetRequiredService<THandler>();

            var canProcess = await inbox.TryStartProcessingAsync(messageId, typeof(THandler).Name, stoppingToken);
            if (!canProcess)
            {
                _logger.LogInformation(
                    "Message {MessageId} duplicate detected for handler {Handler}. Skipping processing and acknowledging.",
                    messageId, typeof(THandler).Name);
                await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                return;
            }

            // 4. In-process retry execution with exponential backoff
            const int maxRetries = 3;
            for (var attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    await handler.HandleAsync(@event, stoppingToken);
                    await inbox.MarkProcessedAsync(messageId, typeof(THandler).Name, stoppingToken);
                    await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken: stoppingToken);

                    MessagingTelemetry.ConsumerProcessedTotal.Add(1);
                    sw.Stop();
                    MessagingTelemetry.ConsumerDurationSeconds.Record(sw.Elapsed.TotalSeconds);
                    return;
                }
                catch (Exception ex) when (attempt < maxRetries && !stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        ex,
                        "Consumer handler {Handler} failed on attempt {Attempt}/{Max} for message {MessageId}. Retrying...",
                        typeof(THandler).Name, attempt, maxRetries, messageId);

                    await Task.Delay(TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt)), stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Fatal failure processing message {MessageId} in {Handler} after {Max} attempts. Rejecting to DLQ.",
                        messageId, typeof(THandler).Name, maxRetries);

                    MessagingTelemetry.ConsumerFailedTotal.Add(1);
                    sw.Stop();
                    MessagingTelemetry.ConsumerDurationSeconds.Record(sw.Elapsed.TotalSeconds);

                    // Requeue = false instructs RabbitMQ to drop or forward to Dead-Letter Exchange
                    await channel.BasicRejectAsync(ea.DeliveryTag, requeue: false, cancellationToken: stoppingToken);
                    return;
                }
            }
        };

        await channel.BasicConsumeAsync(
            queue: _queueName,
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken);

        _logger.LogInformation("Consumer active on queue {Queue}", _queueName);

        // Keep running until cancellation
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Stopping consumer for {Queue}", _queueName);
        }

        try
        {
            await channel.CloseAsync(CancellationToken.None);
            await channel.DisposeAsync();
        }
        catch
        {
            // Ignore channel cleanup errors on shutdown
        }
    }
}
