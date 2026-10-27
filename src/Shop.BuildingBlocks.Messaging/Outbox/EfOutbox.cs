using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Shop.BuildingBlocks.Messaging.Outbox;

public sealed class EfOutbox<TDbContext> : IOutbox where TDbContext : DbContext
{
    private readonly TDbContext _db;

    public EfOutbox(TDbContext db)
    {
        _db = db;
    }

    public void Enqueue<T>(T message, string? routingKey = null) where T : class
    {
        var outboxMessage = OutboxMessage.Create(message, routingKey);
        _db.Set<OutboxMessage>().Add(outboxMessage);
    }

    public async Task EnqueueAsync<T>(T message, string? routingKey = null, CancellationToken cancellationToken = default) where T : class
    {
        var outboxMessage = OutboxMessage.Create(message, routingKey);
        await _db.Set<OutboxMessage>().AddAsync(outboxMessage, cancellationToken);
    }
}
