using System;
using System.Threading;
using System.Threading.Tasks;
using RabbitMQ.Client;

namespace Shop.BuildingBlocks.Messaging.RabbitMq;

public interface IRabbitMqConnection : IAsyncDisposable
{
    Task<IChannel> CreateChannelAsync(CreateChannelOptions? options = null, CancellationToken cancellationToken = default);
    Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken = default);
}
