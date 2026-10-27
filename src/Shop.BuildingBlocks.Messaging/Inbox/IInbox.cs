using System;
using System.Threading;
using System.Threading.Tasks;

namespace Shop.BuildingBlocks.Messaging.Inbox;

public interface IInbox
{
    Task<bool> HasBeenProcessedAsync(Guid messageId, string handlerName, CancellationToken cancellationToken = default);
    Task<bool> TryStartProcessingAsync(Guid messageId, string handlerName, CancellationToken cancellationToken = default);
    Task MarkProcessedAsync(Guid messageId, string handlerName, CancellationToken cancellationToken = default);
}
