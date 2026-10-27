using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Shop.BuildingBlocks.Messaging.RabbitMq;

public sealed class RabbitMqEventPublisher : IEventPublisher, IAsyncDisposable
{
    private readonly IRabbitMqConnection _connection;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqEventPublisher> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private IChannel? _channel;
    private bool _disposed;

    public RabbitMqEventPublisher(
        IRabbitMqConnection connection,
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqEventPublisher> logger)
    {
        _connection = connection;
        _options = options.Value;
        _logger = logger;
    }

    private async Task<IChannel> GetOrCreateChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is not null && _channel.IsOpen)
        {
            return _channel;
        }

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (_channel is not null && _channel.IsOpen)
            {
                return _channel;
            }

            var channelOptions = new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true);

            _channel = await _connection.CreateChannelAsync(channelOptions, cancellationToken);

            _channel.BasicReturnAsync += (sender, ea) =>
            {
                _logger.LogError(
                    "RabbitMQ BasicReturn received! Message was unroutable. Exchange: {Exchange}, RoutingKey: {RoutingKey}, ReplyCode: {ReplyCode}, ReplyText: {ReplyText}",
                    ea.Exchange, ea.RoutingKey, ea.ReplyCode, ea.ReplyText);
                return Task.CompletedTask;
            };

            // Ensure base topology exists before publishing
            await RabbitMqTopology.DeclareTopologyAsync(_channel, _options.Exchange, _options.DeadLetterExchange, cancellationToken);

            return _channel;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task PublishAsync(
        string routingKey,
        string messageType,
        string payload,
        Guid? messageId = null,
        IDictionary<string, object?>? headers = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var channel = await GetOrCreateChannelAsync(cancellationToken);
        var body = Encoding.UTF8.GetBytes(payload);

        var msgHeaders = headers is not null
            ? new Dictionary<string, object?>(headers)
            : new Dictionary<string, object?>();

        // Inject W3C Trace Context headers (traceparent and tracestate)
        var activity = Activity.Current;
        if (activity is not null)
        {
            msgHeaders["traceparent"] = activity.Id;
            if (!string.IsNullOrEmpty(activity.TraceStateString))
            {
                msgHeaders["tracestate"] = activity.TraceStateString;
            }
        }

        var props = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = (messageId ?? Guid.CreateVersion7()).ToString(),
            Type = messageType,
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            Headers = msgHeaders
        };

        _logger.LogDebug(
            "Publishing message to exchange {Exchange} with routing key {RoutingKey} and MessageId {MessageId}",
            _options.Exchange, routingKey, props.MessageId);

        await channel.BasicPublishAsync(
            exchange: _options.Exchange,
            routingKey: routingKey,
            mandatory: true,
            basicProperties: props,
            body: body,
            cancellationToken: cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_channel is not null)
        {
            try
            {
                await _channel.CloseAsync();
                await _channel.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while closing RabbitMQ publisher channel.");
            }
        }

        _semaphore.Dispose();
    }
}
