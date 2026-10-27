using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Shop.Catalog.Api.Data;

namespace Shop.Catalog.Api.Products;

public static class ProductEndpoints
{
    public static IEndpointRouteBuilder MapProductEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var publicGroup = endpoints.MapGroup("/api/v1/products")
            .WithTags("Products");

        publicGroup.MapGet("/", GetProductsAsync)
            .WithName("GetProducts")
            .WithSummary("List products with optional search, sorting, and pagination")
            .Produces<PagedResult<ProductResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        publicGroup.MapGet("/{id:guid}", GetProductByIdAsync)
            .WithName("GetProductById")
            .WithSummary("Get product details by ID")
            .Produces<ProductResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        publicGroup.MapPost("/", CreateProductAsync)
            .WithName("CreateProduct")
            .WithSummary("Create a new product")
            .AddEndpointFilter<ValidationFilter<CreateProductRequest>>()
            .Produces<ProductResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        publicGroup.MapPut("/{id:guid}", UpdateProductAsync)
            .WithName("UpdateProduct")
            .WithSummary("Update product name and price")
            .AddEndpointFilter<ValidationFilter<UpdateProductRequest>>()
            .Produces<ProductResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        publicGroup.MapPost("/{id:guid}/restock", RestockProductAsync)
            .WithName("RestockProduct")
            .WithSummary("Increase available stock for a product")
            .AddEndpointFilter<ValidationFilter<RestockProductRequest>>()
            .Produces<ProductResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Internal lookup endpoint (never routed through Gateway)
        var internalGroup = endpoints.MapGroup("/internal/v1/products")
            .WithTags("InternalProductLookup");

        internalGroup.MapGet("/lookup", LookupProductsAsync)
            .WithName("LookupProducts")
            .WithSummary("Internal bulk lookup of product prices and availability for order validation")
            .Produces<ProductLookupResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return endpoints;
    }

    private static async Task<IResult> GetProductsAsync(
        CatalogDbContext db,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? sort = null,
        CancellationToken cancellationToken = default)
    {
        if (page < 1)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid page parameter",
                detail: "Page number must be 1 or greater.",
                extensions: new Dictionary<string, object?> { ["code"] = "Catalog.InvalidPage" });
        }

        if (pageSize < 1 || pageSize > 100)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid pageSize parameter",
                detail: "Page size must be between 1 and 100.",
                extensions: new Dictionary<string, object?> { ["code"] = "Catalog.InvalidPageSize" });
        }

        var query = db.Products.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var trimmed = search.Trim();
            query = query.Where(p => p.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                                  || p.Sku.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
        }

        query = sort?.ToLowerInvariant() switch
        {
            "name_desc" => query.OrderByDescending(p => p.Name),
            "price" => query.OrderBy(p => p.Price),
            "price_desc" => query.OrderByDescending(p => p.Price),
            "stock" => query.OrderBy(p => p.AvailableStock),
            "stock_desc" => query.OrderByDescending(p => p.AvailableStock),
            _ => query.OrderBy(p => p.Name)
        };

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new ProductResponse(
                p.Id,
                p.Sku,
                p.Name,
                p.Price,
                p.Currency,
                p.AvailableStock,
                p.CreatedAt,
                p.UpdatedAt))
            .ToListAsync(cancellationToken);

        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        return Results.Ok(new PagedResult<ProductResponse>(items, page, pageSize, totalCount, totalPages));
    }

    private static async Task<IResult> GetProductByIdAsync(
        CatalogDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (product is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Product not found",
                detail: $"Product with ID '{id}' was not found.",
                extensions: new Dictionary<string, object?> { ["code"] = "Catalog.ProductNotFound" });
        }

        return Results.Ok(new ProductResponse(
            product.Id,
            product.Sku,
            product.Name,
            product.Price,
            product.Currency,
            product.AvailableStock,
            product.CreatedAt,
            product.UpdatedAt));
    }

    private static async Task<IResult> CreateProductAsync(
        CatalogDbContext db,
        CreateProductRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedSku = request.Sku.Trim().ToUpperInvariant();
        var exists = await db.Products.AnyAsync(p => p.Sku == normalizedSku, cancellationToken);
        if (exists)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Duplicate SKU",
                detail: $"A product with SKU '{normalizedSku}' already exists.",
                extensions: new Dictionary<string, object?> { ["code"] = "Catalog.DuplicateSku" });
        }

        var product = Product.Create(request.Sku, request.Name, request.Price, request.Stock, request.Currency);
        db.Products.Add(product);
        await db.SaveChangesAsync(cancellationToken);

        var response = new ProductResponse(
            product.Id,
            product.Sku,
            product.Name,
            product.Price,
            product.Currency,
            product.AvailableStock,
            product.CreatedAt,
            product.UpdatedAt);

        return Results.Created($"/api/v1/products/{product.Id}", response);
    }

    private static async Task<IResult> UpdateProductAsync(
        CatalogDbContext db,
        Guid id,
        UpdateProductRequest request,
        CancellationToken cancellationToken)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (product is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Product not found",
                detail: $"Product with ID '{id}' was not found.",
                extensions: new Dictionary<string, object?> { ["code"] = "Catalog.ProductNotFound" });
        }

        product.Update(request.Name, request.Price);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new ProductResponse(
            product.Id,
            product.Sku,
            product.Name,
            product.Price,
            product.Currency,
            product.AvailableStock,
            product.CreatedAt,
            product.UpdatedAt));
    }

    private static async Task<IResult> RestockProductAsync(
        CatalogDbContext db,
        Guid id,
        RestockProductRequest request,
        CancellationToken cancellationToken)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (product is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Product not found",
                detail: $"Product with ID '{id}' was not found.",
                extensions: new Dictionary<string, object?> { ["code"] = "Catalog.ProductNotFound" });
        }

        product.Restock(request.Quantity);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new ProductResponse(
            product.Id,
            product.Sku,
            product.Name,
            product.Price,
            product.Currency,
            product.AvailableStock,
            product.CreatedAt,
            product.UpdatedAt));
    }

    private static async Task<IResult> LookupProductsAsync(
        CatalogDbContext db,
        [FromQuery(Name = "ids")] string[] ids,
        CancellationToken cancellationToken)
    {
        // Parse comma-separated or multi-value query strings
        var parsedIds = ids
            .SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct()
            .ToList();

        if (parsedIds.Count == 0)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Missing product IDs",
                detail: "At least one valid GUID must be provided in the 'ids' query parameter.",
                extensions: new Dictionary<string, object?> { ["code"] = "Catalog.MissingIds" });
        }

        if (parsedIds.Count > 50)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Too many product IDs",
                detail: "A maximum of 50 product IDs can be looked up in a single request.",
                extensions: new Dictionary<string, object?> { ["code"] = "Catalog.TooManyIds" });
        }

        var found = await db.Products.AsNoTracking()
            .Where(p => parsedIds.Contains(p.Id))
            .Select(p => new ProductLookupItem(
                p.Id,
                p.Sku,
                p.Name,
                p.Price,
                p.Currency,
                p.AvailableStock))
            .ToListAsync(cancellationToken);

        return Results.Ok(new ProductLookupResponse(found));
    }
}

public sealed class ValidationFilter<T> : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var argument = context.Arguments.OfType<T>().FirstOrDefault();
        if (argument is not null)
        {
            var results = new List<ValidationResult>();
            var validationContext = new ValidationContext(argument);
            if (!Validator.TryValidateObject(argument, validationContext, results, validateAllProperties: true))
            {
                var errors = new Dictionary<string, string[]>();
                foreach (var r in results)
                {
                    var members = r.MemberNames.Any() ? r.MemberNames : ["_general"];
                    foreach (var m in members)
                    {
                        var key = char.ToLowerInvariant(m[0]) + m[1..];
                        if (!errors.ContainsKey(key))
                        {
                            errors[key] = [r.ErrorMessage ?? "Invalid value."];
                        }
                    }
                }

                return Results.ValidationProblem(
                    errors,
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Validation failed",
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = "Catalog.ValidationFailed"
                    });
            }
        }

        return await next(context);
    }
}
