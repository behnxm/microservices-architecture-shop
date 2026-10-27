using System;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Shop.Notifications.Worker.Email;

public sealed class MailpitEmailSender : IEmailSender
{
    private readonly SmtpOptions _options;
    private readonly ILogger<MailpitEmailSender> _logger;

    public MailpitEmailSender(IOptions<SmtpOptions> options, ILogger<MailpitEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendEmailAsync(string recipient, string subject, string body, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        _logger.LogInformation("Sending email to {Recipient} via SMTP {Host}:{Port} with subject '{Subject}'",
            recipient, _options.Host, _options.Port, subject);

        using var client = new SmtpClient();
        await client.ConnectAsync(_options.Host, _options.Port, SecureSocketOptions.None, cancellationToken);

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Shop Notifications", _options.SenderEmail));
        message.To.Add(MailboxAddress.Parse(recipient));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);

        _logger.LogInformation("Email sent successfully to {Recipient}", recipient);
    }
}
