using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shop.BuildingBlocks.Messaging.Consumers;
using Shop.BuildingBlocks.Messaging.Inbox;
using Shop.BuildingBlocks.Messaging.Outbox;
using Shop.BuildingBlocks.Messaging.RabbitMq;

namespace Shop.BuildingBlocks.Messaging.Extensions;

public static class MessagingServiceCollectionExtensions
{
    public static IServiceCollection AddMessagingInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.SectionName));

        // Fallback or override from Aspire/Compose connection string "rabbitmq"
        var connStr = configuration.GetConnectionString("rabbitmq");
        if (!string.IsNullOrWhiteSpace(connStr))
        {
            services.PostConfigure<RabbitMqOptions>(options =>
            {
                if (Uri.TryCreate(connStr, UriKind.Absolute, out var uri))
                {
                    options.HostName = uri.Host;
                    if (uri.Port > 0) options.Port = uri.Port;
                    if (!string.IsNullOrEmpty(uri.UserInfo))
                    {
                        var parts = uri.UserInfo.Split(':');
                        if (parts.Length > 0 && !string.IsNullOrEmpty(parts[0])) options.UserName = parts[0];
                        if (parts.Length > 1 && !string.IsNullOrEmpty(parts[1])) options.Password = parts[1];
                    }
                    if (uri.AbsolutePath.Length > 1)
                    {
                        options.VirtualHost = uri.AbsolutePath.TrimStart('/');
                    }
                }
            });
        }

        services.AddSingleton<IRabbitMqConnection, RabbitMqConnection>();
        services.AddSingleton<IEventPublisher, RabbitMqEventPublisher>();

        return services;
    }

    public static IServiceCollection AddTransactionalOutbox<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext
    {
        services.AddScoped<IOutbox, EfOutbox<TDbContext>>();
        services.AddHostedService<OutboxProcessor<TDbContext>>();
        return services;
    }

    public static IServiceCollection AddTransactionalInbox<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext
    {
        services.AddScoped<IInbox, EfInbox<TDbContext>>();
        return services;
    }

    public static IServiceCollection AddConsumer<TEvent, THandler, TDbContext>(
        this IServiceCollection services,
        string queueName,
        string? routingKey = null)
        where TEvent : class
        where THandler : class, IIntegrationEventHandler<TEvent>
        where TDbContext : DbContext
    {
        services.AddScoped<THandler>();
        services.AddHostedService(sp => ActivatorUtilities.CreateInstance<EventConsumer<TEvent, THandler, TDbContext>>(
            sp,
            queueName,
            routingKey ?? string.Empty));

        return services;
    }
}
