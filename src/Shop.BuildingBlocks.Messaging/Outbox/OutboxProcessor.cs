using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shop.BuildingBlocks.Messaging.RabbitMq;
using Shop.BuildingBlocks.Messaging.Telemetry;

namespace Shop.BuildingBlocks.Messaging.Outbox;

public sealed class OutboxProcessor<TDbContext> : BackgroundService where TDbContext : DbContext
{
    private readonly IServiceProvider _serviceProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OutboxProcessor<TDbContext>> _logger;
    private readonly TimeSpan _pollingInterval = TimeSpan.FromMilliseconds(500);
    private const int BatchSize = 20;

    public OutboxProcessor(
        IServiceProvider serviceProvider,
        TimeProvider timeProvider,
        ILogger<OutboxProcessor<TDbContext>> logger)
    {
        _serviceProvider = serviceProvider;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Outbox processor started for DbContext {DbContext}", typeof(TDbContext).Name);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processedCount = await ProcessOutboxBatchAsync(stoppingToken);
                if (processedCount == 0)
                {
                    await Task.Delay(_pollingInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in outbox processor for DbContext {DbContext}", typeof(TDbContext).Name);
                await Task.Delay(_pollingInterval, stoppingToken);
            }
        }

        _logger.LogInformation("Outbox processor stopped for DbContext {DbContext}", typeof(TDbContext).Name);
    }

    public async Task<int> ProcessOutboxBatchAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        List<OutboxMessage> messages;

        // Use PostgreSQL FOR UPDATE SKIP LOCKED when supported, else standard LINQ fallback for testing
        if (db.Database.IsNpgsql())
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

            var messageIds = await db.Database.SqlQueryRaw<Guid>(
                """
                SELECT "Id" AS "Value"
                FROM outbox_messages
                WHERE "ProcessedOn" IS NULL
                ORDER BY "OccurredOn"
                LIMIT {0}
                FOR UPDATE SKIP LOCKED
                """, BatchSize).ToListAsync(cancellationToken);

            if (messageIds.Count == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return 0;
            }

            messages = await db.Set<OutboxMessage>()
                .Where(m => messageIds.Contains(m.Id))
                .OrderBy(m => m.OccurredOn)
                .ToListAsync(cancellationToken);

            await PublishBatchAsync(messages, publisher, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return messages.Count;
        }
        else
        {
            messages = await db.Set<OutboxMessage>()
                .Where(m => m.ProcessedOn == null)
                .OrderBy(m => m.OccurredOn)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            if (messages.Count == 0)
            {
                return 0;
            }

            await PublishBatchAsync(messages, publisher, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return messages.Count;
        }
    }

    private async Task PublishBatchAsync(
        List<OutboxMessage> messages,
        IEventPublisher publisher,
        CancellationToken cancellationToken)
    {
        foreach (var message in messages)
        {
            using var activity = MessagingTelemetry.ActivitySource.StartActivity(
                "Outbox.Publish",
                ActivityKind.Producer);

            if (activity is not null)
            {
                activity.SetTag("messaging.system", "rabbitmq");
                activity.SetTag("messaging.destination", "shop.events");
                activity.SetTag("messaging.routing_key", message.RoutingKey);
                activity.SetTag("messaging.message_id", message.Id.ToString());
            }

            try
            {
                await publisher.PublishAsync(
                    routingKey: message.RoutingKey,
                    messageType: message.MessageType,
                    payload: message.Payload,
                    messageId: message.Id,
                    cancellationToken: cancellationToken);

                message.MarkProcessed(_timeProvider.GetUtcNow());
                MessagingTelemetry.OutboxPublishedTotal.Add(1);

                _logger.LogDebug(
                    "Published outbox message {MessageId} ({RoutingKey}) successfully.",
                    message.Id, message.RoutingKey);
            }
            catch (Exception ex)
            {
                message.RecordFailure(ex.Message);
                MessagingTelemetry.OutboxFailedTotal.Add(1);

                _logger.LogError(
                    ex,
                    "Failed to publish outbox message {MessageId} ({RoutingKey}). Retry count: {RetryCount}",
                    message.Id, message.RoutingKey, message.RetryCount);
            }
        }
    }
}
