using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shop.BuildingBlocks.Messaging.Inbox;
using Shouldly;
using Xunit;

namespace Shop.BuildingBlocks.Tests.Inbox;

public sealed class EfInboxTests
{
    private static TestMessagingDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<TestMessagingDbContext>()
            .UseInMemoryDatabase($"inbox_test_{Guid.NewGuid()}")
            .Options;

        return new TestMessagingDbContext(options);
    }

    [Fact]
    public async Task TryStartProcessingAsync_Allows_First_Attempt_And_Rejects_Duplicate()
    {
        // Arrange
        await using var db = CreateDbContext();
        var inbox = new EfInbox<TestMessagingDbContext>(db, NullLogger<EfInbox<TestMessagingDbContext>>.Instance);

        var messageId = Guid.NewGuid();
        var handlerName = "StockReservationHandler";

        // Act 1: First attempt
        var firstAttempt = await inbox.TryStartProcessingAsync(messageId, handlerName);

        // Act 2: Second attempt (duplicate delivery)
        var secondAttempt = await inbox.TryStartProcessingAsync(messageId, handlerName);

        // Assert
        firstAttempt.ShouldBeTrue();
        secondAttempt.ShouldBeFalse();
    }

    [Fact]
    public async Task Different_Handlers_Can_Independently_Process_Same_Message()
    {
        // Arrange
        await using var db = CreateDbContext();
        var inbox = new EfInbox<TestMessagingDbContext>(db, NullLogger<EfInbox<TestMessagingDbContext>>.Instance);

        var messageId = Guid.NewGuid();
        var handler1 = "HandlerOne";
        var handler2 = "HandlerTwo";

        // Act
        var res1 = await inbox.TryStartProcessingAsync(messageId, handler1);
        var res2 = await inbox.TryStartProcessingAsync(messageId, handler2);

        // Assert
        res1.ShouldBeTrue();
        res2.ShouldBeTrue();
    }

    [Fact]
    public async Task MarkProcessedAsync_Updates_ProcessedOn_Timestamp()
    {
        // Arrange
        await using var db = CreateDbContext();
        var inbox = new EfInbox<TestMessagingDbContext>(db, NullLogger<EfInbox<TestMessagingDbContext>>.Instance);

        var messageId = Guid.NewGuid();
        var handlerName = "OrderFulfillmentHandler";

        await inbox.TryStartProcessingAsync(messageId, handlerName);

        // Act
        await inbox.MarkProcessedAsync(messageId, handlerName);

        // Assert
        var record = await db.InboxMessages.FirstOrDefaultAsync(m => m.MessageId == messageId && m.HandlerName == handlerName);
        record.ShouldNotBeNull();
        record.ProcessedOn.ShouldNotBeNull();

        var isProcessed = await inbox.HasBeenProcessedAsync(messageId, handlerName);
        isProcessed.ShouldBeTrue();
    }
}
