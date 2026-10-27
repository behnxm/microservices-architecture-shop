using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Shop.BuildingBlocks.Messaging.Inbox;

public sealed class EfInbox<TDbContext> : IInbox where TDbContext : DbContext
{
    private readonly TDbContext _db;
    private readonly ILogger<EfInbox<TDbContext>> _logger;

    public EfInbox(TDbContext db, ILogger<EfInbox<TDbContext>> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<bool> HasBeenProcessedAsync(Guid messageId, string handlerName, CancellationToken cancellationToken = default)
    {
        return await _db.Set<InboxMessage>()
            .AsNoTracking()
            .AnyAsync(m => m.MessageId == messageId && m.HandlerName == handlerName && m.ProcessedOn != null, cancellationToken);
    }

    public async Task<bool> TryStartProcessingAsync(Guid messageId, string handlerName, CancellationToken cancellationToken = default)
    {
        var existing = await _db.Set<InboxMessage>()
            .FirstOrDefaultAsync(m => m.MessageId == messageId && m.HandlerName == handlerName, cancellationToken);

        if (existing is not null)
        {
            _logger.LogInformation("Inbox duplicate detected for message {MessageId} in handler {HandlerName}", messageId, handlerName);
            return false;
        }

        var inboxMessage = InboxMessage.Create(messageId, handlerName);
        _db.Set<InboxMessage>().Add(inboxMessage);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex)
        {
            // Race condition: another concurrent consumer already claimed this message
            _logger.LogWarning(ex, "Concurrent duplicate inbox claim caught for message {MessageId} in handler {HandlerName}", messageId, handlerName);
            return false;
        }
    }

    public async Task MarkProcessedAsync(Guid messageId, string handlerName, CancellationToken cancellationToken = default)
    {
        var inboxMessage = await _db.Set<InboxMessage>()
            .FirstOrDefaultAsync(m => m.MessageId == messageId && m.HandlerName == handlerName, cancellationToken);

        if (inboxMessage is not null)
        {
            inboxMessage.MarkProcessed(DateTimeOffset.UtcNow);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }
}
