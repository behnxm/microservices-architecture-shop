using System;
using System.Collections.Generic;
using System.Linq;

namespace Shop.Ordering.Api.Orders;

public enum OrderStatus
{
    Pending = 1,
    Confirmed = 2,
    Rejected = 3
}

public sealed class Order
{
    private readonly List<OrderLine> _lines = [];

    public Guid Id { get; private set; }
    public Guid CustomerId { get; private set; }
    public string CustomerEmail { get; private set; } = default!;
    public OrderStatus Status { get; private set; }
    public IReadOnlyList<OrderLine> Lines => _lines.AsReadOnly();
    public decimal Total { get; private set; }
    public string Currency { get; private set; } = "USD";
    public string? RejectionReason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public int Version { get; private set; } = 1;

    private Order() { }

    public static Order Create(Guid customerId, string customerEmail, IEnumerable<OrderLine> lines, string currency = "USD")
    {
        if (customerId == Guid.Empty)
        {
            throw new ArgumentException("Customer ID cannot be empty.", nameof(customerId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(customerEmail);
        var lineList = lines.ToList();
        if (lineList.Count == 0)
        {
            throw new ArgumentException("Order must contain at least one order line.", nameof(lines));
        }

        var now = DateTimeOffset.UtcNow;
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            CustomerId = customerId,
            CustomerEmail = customerEmail.Trim().ToLowerInvariant(),
            Status = OrderStatus.Pending,
            Currency = currency.Trim().ToUpperInvariant(),
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };

        foreach (var line in lineList)
        {
            order._lines.Add(line);
        }

        order.Total = order._lines.Sum(l => l.UnitPrice * l.Quantity);
        return order;
    }

    public void Confirm()
    {
        if (Status == OrderStatus.Confirmed)
        {
            // Idempotent: repeated confirm is a safe no-op
            return;
        }

        if (Status == OrderStatus.Rejected)
        {
            throw new InvalidOperationException("Cannot confirm an order that has already been rejected.");
        }

        Status = OrderStatus.Confirmed;
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }

    public void Reject(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (Status == OrderStatus.Rejected)
        {
            // Idempotent: repeated reject is a safe no-op
            return;
        }

        if (Status == OrderStatus.Confirmed)
        {
            throw new InvalidOperationException("Cannot reject an order that has already been confirmed.");
        }

        Status = OrderStatus.Rejected;
        RejectionReason = reason.Trim();
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }
}

public sealed class OrderLine
{
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid ProductId { get; private set; }
    public string ProductName { get; private set; } = default!;
    public decimal UnitPrice { get; private set; }
    public int Quantity { get; private set; }

    private OrderLine() { }

    public static OrderLine Create(Guid productId, string productName, decimal unitPrice, int quantity)
    {
        if (productId == Guid.Empty)
        {
            throw new ArgumentException("Product ID cannot be empty.", nameof(productId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(productName);
        if (unitPrice < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(unitPrice), "Unit price cannot be negative.");
        }

        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");
        }

        return new OrderLine
        {
            Id = Guid.CreateVersion7(),
            ProductId = productId,
            ProductName = productName.Trim(),
            UnitPrice = unitPrice,
            Quantity = quantity
        };
    }
}
