using System;
using System.Collections.Generic;

namespace Shop.Contracts;

public sealed record OrderLineItemContract(
    Guid ProductId,
    int Quantity
);

[IntegrationEvent("ordering.order-placed.v1")]
public sealed record OrderPlacedIntegrationEvent(
    Guid MessageId,
    DateTimeOffset OccurredOn,
    Guid OrderId,
    Guid CustomerId,
    string CustomerEmail,
    IReadOnlyList<OrderLineItemContract> Items
);

[IntegrationEvent("catalog.stock-reserved.v1")]
public sealed record StockReservedIntegrationEvent(
    Guid MessageId,
    DateTimeOffset OccurredOn,
    Guid OrderId
);

[IntegrationEvent("catalog.stock-rejected.v1")]
public sealed record StockRejectedIntegrationEvent(
    Guid MessageId,
    DateTimeOffset OccurredOn,
    Guid OrderId,
    string Reason,
    IReadOnlyList<Guid> UnavailableProductIds
);

[IntegrationEvent("ordering.order-confirmed.v1")]
public sealed record OrderConfirmedIntegrationEvent(
    Guid MessageId,
    DateTimeOffset OccurredOn,
    Guid OrderId,
    Guid CustomerId,
    string CustomerEmail,
    decimal Total,
    string Currency
);

[IntegrationEvent("ordering.order-rejected.v1")]
public sealed record OrderRejectedIntegrationEvent(
    Guid MessageId,
    DateTimeOffset OccurredOn,
    Guid OrderId,
    Guid CustomerId,
    string CustomerEmail,
    string Reason
);
