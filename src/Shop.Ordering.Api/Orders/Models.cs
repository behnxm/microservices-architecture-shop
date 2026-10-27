using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Shop.Ordering.Api.Orders;

public sealed record CreateOrderLineRequest
{
    [Required]
    public Guid ProductId { get; init; }

    [Range(1, 1000)]
    public int Quantity { get; init; }

    public CreateOrderLineRequest() { }

    public CreateOrderLineRequest(Guid productId, int quantity)
    {
        ProductId = productId;
        Quantity = quantity;
    }
}

public sealed record CreateOrderRequest
{
    [Required]
    public Guid CustomerId { get; init; }

    [Required, EmailAddress, StringLength(256)]
    public string CustomerEmail { get; init; } = string.Empty;

    [Required]
    public IReadOnlyList<CreateOrderLineRequest> Lines { get; init; } = [];

    public CreateOrderRequest() { }

    public CreateOrderRequest(Guid customerId, string customerEmail, IReadOnlyList<CreateOrderLineRequest> lines)
    {
        CustomerId = customerId;
        CustomerEmail = customerEmail;
        Lines = lines;
    }
}

public sealed record OrderCreatedResponse(
    Guid Id,
    string Status
);

public sealed record OrderLineResponse(
    Guid ProductId,
    string ProductName,
    decimal UnitPrice,
    int Quantity,
    decimal TotalPrice
);

public sealed record OrderResponse(
    Guid Id,
    Guid CustomerId,
    string CustomerEmail,
    string Status,
    decimal Total,
    string Currency,
    string? RejectionReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<OrderLineResponse> Lines
);

public sealed record OrderSummaryResponse(
    Guid Id,
    Guid CustomerId,
    string Status,
    decimal Total,
    string Currency,
    DateTimeOffset CreatedAt
);

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages
);
