using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shop.Notifications.Worker.Email;
using Shouldly;
using Xunit;

namespace Shop.Notifications.Tests.Email;

public sealed class MailpitEmailSenderTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task SendEmailAsync_Throws_On_Invalid_Recipient(string? recipient)
    {
        var options = Options.Create(new SmtpOptions());
        var sender = new MailpitEmailSender(options, NullLogger<MailpitEmailSender>.Instance);

        await Should.ThrowAsync<ArgumentException>(() =>
            sender.SendEmailAsync(recipient!, "Subject", "Body"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task SendEmailAsync_Throws_On_Invalid_Subject(string? subject)
    {
        var options = Options.Create(new SmtpOptions());
        var sender = new MailpitEmailSender(options, NullLogger<MailpitEmailSender>.Instance);

        await Should.ThrowAsync<ArgumentException>(() =>
            sender.SendEmailAsync("test@example.com", subject!, "Body"));
    }
}
