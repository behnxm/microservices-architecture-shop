using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shop.Contracts;
using Shop.Notifications.Worker.Consumers;
using Shop.Notifications.Worker.Data;
using Shop.Notifications.Worker.Email;
using Shouldly;
using Xunit;

namespace Shop.Notifications.Tests.Consumers;

public sealed class OrderConfirmedNotificationHandlerTests
{
    private static NotificationsDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseInMemoryDatabase($"notifications_confirmed_{Guid.NewGuid()}")
            .Options;

        return new NotificationsDbContext(options);
    }

    [Fact]
    public async Task HandleAsync_Sends_Email_And_Records_Notification_In_Database()
    {
        // Arrange
        await using var db = CreateDbContext();
        var emailSender = Substitute.For<IEmailSender>();
        var handler = new OrderConfirmedNotificationHandler(
            db,
            emailSender,
            NullLogger<OrderConfirmedNotificationHandler>.Instance);

        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var email = "alice@example.com";
        var @event = new OrderConfirmedIntegrationEvent(
            MessageId: Guid.NewGuid(),
            OccurredOn: DateTimeOffset.UtcNow,
            OrderId: orderId,
            CustomerId: customerId,
            CustomerEmail: email,
            Total: 189.50m,
            Currency: "USD");

        // Act
        await handler.HandleAsync(@event);

        // Assert: Email sender invoked with expected details
        await emailSender.Received(1).SendEmailAsync(
            email,
            Arg.Is<string>(s => s.Contains(orderId.ToString()) && s.Contains("Confirmed")),
            Arg.Is<string>(b => b.Contains("189.50") && b.Contains("USD")),
            Arg.Any<CancellationToken>());

        // Assert: Notification recorded in database
        var record = await db.Notifications.FirstOrDefaultAsync(n => n.OrderId == orderId && n.Type == "OrderConfirmed");
        record.ShouldNotBeNull();
        record.RecipientEmail.ShouldBe(email);
        record.Status.ShouldBe("Sent");
    }

    [Fact]
    public async Task HandleAsync_Duplicate_Event_Replay_Does_Not_Send_Second_Email()
    {
        // Arrange
        await using var db = CreateDbContext();
        var emailSender = Substitute.For<IEmailSender>();
        var handler = new OrderConfirmedNotificationHandler(
            db,
            emailSender,
            NullLogger<OrderConfirmedNotificationHandler>.Instance);

        var orderId = Guid.NewGuid();
        var @event = new OrderConfirmedIntegrationEvent(
            MessageId: Guid.NewGuid(),
            OccurredOn: DateTimeOffset.UtcNow,
            OrderId: orderId,
            CustomerId: Guid.NewGuid(),
            CustomerEmail: "bob@example.com",
            Total: 50.00m,
            Currency: "USD");

        // Act 1: Initial delivery
        await handler.HandleAsync(@event);

        // Act 2: Replay delivery with same orderId
        await handler.HandleAsync(@event);

        // Assert: Email sender was only called ONCE
        await emailSender.Received(1).SendEmailAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());

        var count = await db.Notifications.CountAsync(n => n.OrderId == orderId && n.Type == "OrderConfirmed");
        count.ShouldBe(1);
    }
}
