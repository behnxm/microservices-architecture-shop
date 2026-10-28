using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shop.BuildingBlocks.Messaging.Data;
using Shop.BuildingBlocks.Messaging.Extensions;
using Shop.Contracts;
using Shop.Notifications.Worker.Consumers;
using Shop.Notifications.Worker.Data;
using Shop.Notifications.Worker.Email;
using Shop.ServiceDefaults;

// 1. Standalone healthcheck CLI switch for container environments
if (args.Contains(HealthCheckCli.Flag))
{
    return await HealthCheckCli.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(args);

// 2. Aspire Service Defaults: OpenTelemetry, resilience, service discovery, health checks
builder.AddServiceDefaults();

// 3. Database configuration (Npgsql PostgreSQL)
var connectionString = builder.Configuration.GetConnectionString("notificationsdb")
    ?? builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Database=notificationsdb;Username=postgres;Password=postgres_dev_password";

builder.Services.AddDbContext<NotificationsDbContext>(options =>
{
    options.UseNpgsql(connectionString);
});

builder.Services.AddHealthChecks()
    .AddDbContextCheck<NotificationsDbContext>("notificationsdb", tags: ["ready"]);

// 4. SMTP Email Configuration
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.SectionName));

// Override SMTP host if Aspire service discovery or environment variable is present
var mailpitEndpoint = builder.Configuration["services:mailpit:smtp:0"]
    ?? builder.Configuration["services:mailpit:default:0"]
    ?? builder.Configuration["MAILPIT_SMTP"];

if (!string.IsNullOrEmpty(mailpitEndpoint))
{
    builder.Services.PostConfigure<SmtpOptions>(options =>
    {
        var cleaned = mailpitEndpoint;
        var schemeIdx = cleaned.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0)
        {
            cleaned = cleaned.Substring(schemeIdx + 3);
        }

        if (Uri.TryCreate($"smtp://{cleaned}", UriKind.Absolute, out var uri))
        {
            options.Host = uri.Host;
            if (uri.Port > 0) options.Port = uri.Port;
        }
    });
}

builder.Services.AddSingleton<IEmailSender, MailpitEmailSender>();

// 5. Messaging Infrastructure & Event Consumers
builder.Services.AddMessagingInfrastructure(builder.Configuration);
builder.Services.AddTransactionalInbox<NotificationsDbContext>();
builder.Services.AddConsumer<OrderConfirmedIntegrationEvent, OrderConfirmedNotificationHandler, NotificationsDbContext>("notifications.order-confirmed");
builder.Services.AddConsumer<OrderRejectedIntegrationEvent, OrderRejectedNotificationHandler, NotificationsDbContext>("notifications.order-rejected");

var app = builder.Build();

// 6. Automatic database migrations
if (app.Configuration.GetValue<bool>("Database:Migrate"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    logger.LogInformation("Applying Notifications database migrations...");
    await DatabaseMigrator.MigrateWithLockAsync(db, logger, lockKey: 7429102L);
    logger.LogInformation("Notifications database migrations applied successfully.");
}

// 7. Map Endpoints
app.MapDefaultEndpoints();

await app.RunAsync();
return 0;

namespace Shop.Notifications.Worker
{
    public partial class Program { }
}
