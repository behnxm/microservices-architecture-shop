using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shop.BuildingBlocks.Messaging.Outbox;
using Shop.BuildingBlocks.Messaging.RabbitMq;
using Shop.Contracts;
using Shouldly;
using Xunit;

namespace Shop.BuildingBlocks.Tests.Outbox;

public sealed class OutboxProcessorTests
{
    [Fact]
    public async Task ProcessOutboxBatchAsync_Publishes_Unprocessed_Messages_And_Marks_Them_Processed()
    {
        // Arrange
        var dbName = $"outbox_proc_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<TestMessagingDbContext>(o => o.UseInMemoryDatabase(dbName));

        var publisher = Substitute.For<IEventPublisher>();
        services.AddSingleton(publisher);

        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(new DateTimeOffset(2026, 10, 27, 21, 0, 0, TimeSpan.Zero));
        services.AddSingleton<TimeProvider>(timeProvider);

        var sp = services.BuildServiceProvider();

        // Seed 2 outbox messages
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestMessagingDbContext>();
            var event1 = new StockReservedIntegrationEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid());
            var event2 = new StockRejectedIntegrationEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), "Out of stock", []);

            db.OutboxMessages.AddRange(OutboxMessage.Create(event1), OutboxMessage.Create(event2));
            await db.SaveChangesAsync();
        }

        var processor = new OutboxProcessor<TestMessagingDbContext>(
            sp,
            timeProvider,
            NullLogger<OutboxProcessor<TestMessagingDbContext>>.Instance);

        // Act
        var processedCount = await processor.ProcessOutboxBatchAsync();

        // Assert
        processedCount.ShouldBe(2);

        // Verify publisher was called for both
        await publisher.Received(2).PublishAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<Guid?>(),
            Arg.Any<IDictionary<string, object?>>(),
            Arg.Any<CancellationToken>());

        // Verify messages in DB marked processed
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestMessagingDbContext>();
            var unread = await db.OutboxMessages.CountAsync(m => m.ProcessedOn == null);
            unread.ShouldBe(0);

            var processed = await db.OutboxMessages.CountAsync(m => m.ProcessedOn == timeProvider.GetUtcNow());
            processed.ShouldBe(2);
        }
    }

    [Fact]
    public async Task ProcessOutboxBatchAsync_Records_Failure_When_Publisher_Throws()
    {
        // Arrange
        var dbName = $"outbox_fail_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<TestMessagingDbContext>(o => o.UseInMemoryDatabase(dbName));

        var publisher = Substitute.For<IEventPublisher>();
        publisher.PublishAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<Guid?>(),
            Arg.Any<IDictionary<string, object?>>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("RabbitMQ connection refused"));

        services.AddSingleton(publisher);

        var timeProvider = new FakeTimeProvider();
        services.AddSingleton<TimeProvider>(timeProvider);

        var sp = services.BuildServiceProvider();

        Guid messageId;
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestMessagingDbContext>();
            var @event = new StockReservedIntegrationEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid());
            var outboxMsg = OutboxMessage.Create(@event);
            messageId = outboxMsg.Id;
            db.OutboxMessages.Add(outboxMsg);
            await db.SaveChangesAsync();
        }

        var processor = new OutboxProcessor<TestMessagingDbContext>(
            sp,
            timeProvider,
            NullLogger<OutboxProcessor<TestMessagingDbContext>>.Instance);

        // Act
        var count = await processor.ProcessOutboxBatchAsync();

        // Assert
        count.ShouldBe(1);

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestMessagingDbContext>();
            var msg = await db.OutboxMessages.FirstAsync(m => m.Id == messageId);
            msg.ProcessedOn.ShouldBeNull();
            msg.RetryCount.ShouldBe(1);
            msg.LastError.ShouldNotBeNull();
            msg.LastError.ShouldContain("RabbitMQ connection refused");
        }
    }
}
