using System.Threading;
using System.Threading.Tasks;

namespace Shop.BuildingBlocks.Messaging.Outbox;

public interface IOutbox
{
    void Enqueue<T>(T message, string? routingKey = null) where T : class;
    Task EnqueueAsync<T>(T message, string? routingKey = null, CancellationToken cancellationToken = default) where T : class;
}
