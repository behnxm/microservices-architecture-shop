using System;

namespace Shop.Catalog.Api.Products;

public sealed class Product
{
    public Guid Id { get; private set; }
    public string Sku { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public decimal Price { get; private set; }
    public string Currency { get; private set; } = "USD";
    public int AvailableStock { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public int Version { get; private set; } = 1;

    private Product() { }

    public static Product Create(string sku, string name, decimal price, int stock, string currency = "USD", Guid? fixedId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (price < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(price), "Price cannot be negative.");
        }

        if (stock < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stock), "Stock cannot be negative.");
        }

        var now = DateTimeOffset.UtcNow;
        return new Product
        {
            Id = fixedId ?? Guid.CreateVersion7(),
            Sku = sku.Trim().ToUpperInvariant(),
            Name = name.Trim(),
            Price = price,
            Currency = currency.Trim().ToUpperInvariant(),
            AvailableStock = stock,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };
    }

    public void Update(string name, decimal price)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (price < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(price), "Price cannot be negative.");
        }

        Name = name.Trim();
        Price = price;
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }

    public void Restock(int quantity)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Restock quantity must be positive.");
        }

        AvailableStock += quantity;
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }

    public void Reserve(int quantity)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Reservation quantity must be positive.");
        }

        if (AvailableStock < quantity)
        {
            throw new InvalidOperationException($"Insufficient stock for product '{Sku}'. Requested: {quantity}, available: {AvailableStock}.");
        }

        AvailableStock -= quantity;
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }
}
