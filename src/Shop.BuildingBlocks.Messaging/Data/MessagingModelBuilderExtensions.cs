using Microsoft.EntityFrameworkCore;
using Shop.BuildingBlocks.Messaging.Inbox;
using Shop.BuildingBlocks.Messaging.Outbox;

namespace Shop.BuildingBlocks.Messaging.Data;

public static class MessagingModelBuilderExtensions
{
    public static void ApplyMessagingConfigurations(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_messages");
            b.HasKey(m => m.Id);
            b.Property(m => m.MessageType).HasMaxLength(256).IsRequired();
            b.Property(m => m.RoutingKey).HasMaxLength(256).IsRequired();
            b.Property(m => m.Payload).IsRequired();
            b.Property(m => m.OccurredOn).IsRequired();
            b.Property(m => m.ProcessedOn);
            b.Property(m => m.RetryCount).IsRequired();
            b.Property(m => m.LastError).HasMaxLength(2048);

            b.HasIndex(m => m.ProcessedOn);
            b.HasIndex(m => m.OccurredOn);
        });

        modelBuilder.Entity<InboxMessage>(b =>
        {
            b.ToTable("inbox_messages");
            b.HasKey(m => new { m.MessageId, m.HandlerName });
            b.Property(m => m.MessageId).IsRequired();
            b.Property(m => m.HandlerName).HasMaxLength(256).IsRequired();
            b.Property(m => m.ReceivedOn).IsRequired();
            b.Property(m => m.ProcessedOn);
        });
    }
}
