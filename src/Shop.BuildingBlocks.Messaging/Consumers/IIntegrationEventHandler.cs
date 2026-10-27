using System.Threading;
using System.Threading.Tasks;

namespace Shop.BuildingBlocks.Messaging.Consumers;

public interface IIntegrationEventHandler<in TEvent> where TEvent : class
{
    Task HandleAsync(TEvent eventMessage, CancellationToken cancellationToken = default);
}
