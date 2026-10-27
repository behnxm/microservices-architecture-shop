using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Shop.Catalog.Api.Products;

public sealed record ProductResponse(
    Guid Id,
    string Sku,
    string Name,
    decimal Price,
    string Currency,
    int AvailableStock,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages
);

public sealed record CreateProductRequest
{
    [Required(AllowEmptyStrings = false), StringLength(64, MinimumLength = 3)]
    public string Sku { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false), StringLength(256, MinimumLength = 1)]
    public string Name { get; init; } = string.Empty;

    [Range(0.0, 1_000_000.0)]
    public decimal Price { get; init; }

    [Range(0, 1_000_000)]
    public int Stock { get; init; }

    [StringLength(3, MinimumLength = 3)]
    public string Currency { get; init; } = "USD";

    public CreateProductRequest() { }

    public CreateProductRequest(string sku, string name, decimal price, int stock, string currency = "USD")
    {
        Sku = sku;
        Name = name;
        Price = price;
        Stock = stock;
        Currency = currency;
    }
}

public sealed record UpdateProductRequest
{
    [Required(AllowEmptyStrings = false), StringLength(256, MinimumLength = 1)]
    public string Name { get; init; } = string.Empty;

    [Range(0.0, 1_000_000.0)]
    public decimal Price { get; init; }

    public UpdateProductRequest() { }

    public UpdateProductRequest(string name, decimal price)
    {
        Name = name;
        Price = price;
    }
}

public sealed record RestockProductRequest
{
    [Range(1, 1_000_000)]
    public int Quantity { get; init; }

    public RestockProductRequest() { }

    public RestockProductRequest(int quantity)
    {
        Quantity = quantity;
    }
}

public sealed record ProductLookupItem(
    Guid Id,
    string Sku,
    string Name,
    decimal Price,
    string Currency,
    int AvailableStock
);

public sealed record ProductLookupResponse(
    IReadOnlyList<ProductLookupItem> Products
);
