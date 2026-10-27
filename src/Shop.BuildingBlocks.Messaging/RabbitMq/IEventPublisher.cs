using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Shop.BuildingBlocks.Messaging.RabbitMq;

public interface IEventPublisher
{
    Task PublishAsync(
        string routingKey,
        string messageType,
        string payload,
        Guid? messageId = null,
        IDictionary<string, object?>? headers = null,
        CancellationToken cancellationToken = default);
}
