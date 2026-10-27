using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Shop.Ordering.Api.Clients;
using Shop.Ordering.Api.Data;
using Shop.Ordering.Api.Idempotency;

namespace Shop.Ordering.Api.Orders;

public static class OrderEndpoints
{
    private static readonly JsonSerializerOptions CamelCaseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/orders")
            .WithTags("Orders");

        group.MapPost("/", CreateOrderAsync)
            .WithName("CreateOrder")
            .WithSummary("Place a new order with synchronous product snapshot validation and asynchronous fulfillment")
            .AddEndpointFilter<ValidationFilter<CreateOrderRequest>>()
            .Produces<OrderCreatedResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/{id:guid}", GetOrderByIdAsync)
            .WithName("GetOrderById")
            .WithSummary("Get order details and current fulfillment status")
            .Produces<OrderResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/", GetOrdersAsync)
            .WithName("GetOrders")
            .WithSummary("Query orders with optional customer, status, and pagination filters")
            .Produces<PagedResult<OrderSummaryResponse>>(StatusCodes.Status200OK);

        return endpoints;
    }

    private static async Task<IResult> CreateOrderAsync(
        HttpContext httpContext,
        OrderingDbContext db,
        ICatalogClient catalogClient,
        CreateOrderRequest request,
        CancellationToken cancellationToken)
    {
        // 1. Enforce Idempotency-Key header presence and format
        if (!httpContext.Request.Headers.TryGetValue("Idempotency-Key", out var rawKey) || string.IsNullOrWhiteSpace(rawKey))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Missing Idempotency-Key header",
                detail: "The 'Idempotency-Key' HTTP request header is required for checkout mutations.",
                extensions: new Dictionary<string, object?> { ["code"] = "Ordering.MissingIdempotencyKey" });
        }

        var idempotencyKey = rawKey.ToString().Trim();
        if (idempotencyKey.Length > 64)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid Idempotency-Key",
                detail: "The 'Idempotency-Key' value cannot exceed 64 characters.",
                extensions: new Dictionary<string, object?> { ["code"] = "Ordering.InvalidIdempotencyKey" });
        }

        // 2. Check for previously processed idempotent request
        var canonicalRequestJson = JsonSerializer.Serialize(request, CamelCaseJson);
        var requestHash = IdempotencyRecord.ComputeSha256(canonicalRequestJson);

        var existingRecord = await db.IdempotencyRecords.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Key == idempotencyKey, cancellationToken);

        if (existingRecord is not null)
        {
            if (!string.Equals(existingRecord.RequestHash, requestHash, StringComparison.Ordinal))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "Idempotency key payload mismatch",
                    detail: "The idempotency key has already been used with a different request payload.",
                    extensions: new Dictionary<string, object?> { ["code"] = "Ordering.IdempotencyPayloadMismatch" });
            }

            httpContext.Response.Headers.Append("Idempotent-Replayed", "true");
            return Results.Content(existingRecord.ResponseBody, contentType: "application/json", statusCode: existingRecord.StatusCode);
        }

        if (request.Lines.Count == 0)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Empty order lines",
                detail: "An order must contain at least one line item.",
                extensions: new Dictionary<string, object?> { ["code"] = "Ordering.EmptyLines" });
        }

        // 3. Synchronous catalog lookup with resilient client
        var productIds = request.Lines.Select(l => l.ProductId).Distinct().ToList();
        CatalogLookupResult catalogResult;
        try
        {
            catalogResult = await catalogClient.GetProductsAsync(productIds, cancellationToken);
        }
        catch (CatalogUnavailableException)
        {
            httpContext.Response.Headers.Append("Retry-After", "15");
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Catalog unavailable",
                detail: "The catalog service is temporarily unreachable. The order was NOT created. Please retry later.",
                extensions: new Dictionary<string, object?> { ["code"] = "Ordering.CatalogUnavailable" });
        }

        var catalogMap = catalogResult.Products.ToDictionary(p => p.Id);

        // 4. Validate that all requested products exist in the catalog
        var missingProductIds = productIds.Where(id => !catalogMap.ContainsKey(id)).ToList();
        if (missingProductIds.Count > 0)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Unknown product",
                detail: $"The following product IDs do not exist in the catalog: {string.Join(", ", missingProductIds)}",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "Ordering.UnknownProduct",
                    ["unknownProductIds"] = missingProductIds
                });
        }

        // 5. Validate currency consistency (must be USD)
        var currencyMismatch = catalogResult.Products.FirstOrDefault(p => !string.Equals(p.Currency, "USD", StringComparison.OrdinalIgnoreCase));
        if (currencyMismatch is not null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Currency mismatch",
                detail: $"Product '{currencyMismatch.Sku}' has currency '{currencyMismatch.Currency}', which does not match expected 'USD'.",
                extensions: new Dictionary<string, object?> { ["code"] = "Ordering.CurrencyMismatch" });
        }

        // 6. Build immutable order lines with captured unit prices
        var orderLines = request.Lines.Select(l =>
        {
            var item = catalogMap[l.ProductId];
            return OrderLine.Create(l.ProductId, item.Name, item.Price, l.Quantity);
        });

        var order = Order.Create(request.CustomerId, request.CustomerEmail, orderLines, "USD");
        var responsePayload = new OrderCreatedResponse(order.Id, order.Status.ToString());
        var responseJson = JsonSerializer.Serialize(responsePayload, CamelCaseJson);

        // 7. Atomic transaction: insert order + idempotency record
        var idempotencyRecord = IdempotencyRecord.Create(idempotencyKey, requestHash, order.Id, StatusCodes.Status202Accepted, responseJson);

        try
        {
            db.Orders.Add(order);
            db.IdempotencyRecords.Add(idempotencyRecord);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Concurrent race on the unique idempotency key: fetch the existing winner
            var concurrentWinner = await db.IdempotencyRecords.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Key == idempotencyKey, cancellationToken);

            if (concurrentWinner is not null)
            {
                httpContext.Response.Headers.Append("Idempotent-Replayed", "true");
                return Results.Content(concurrentWinner.ResponseBody, contentType: "application/json", statusCode: concurrentWinner.StatusCode);
            }

            throw;
        }

        return Results.Accepted($"/api/v1/orders/{order.Id}", responsePayload);
    }

    private static async Task<IResult> GetOrderByIdAsync(
        OrderingDbContext db,
        Guid id,
        CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (order is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Order not found",
                detail: $"Order with ID '{id}' was not found.",
                extensions: new Dictionary<string, object?> { ["code"] = "Ordering.OrderNotFound" });
        }

        var lines = order.Lines.Select(l => new OrderLineResponse(
            l.ProductId,
            l.ProductName,
            l.UnitPrice,
            l.Quantity,
            l.UnitPrice * l.Quantity
        )).ToList();

        var response = new OrderResponse(
            order.Id,
            order.CustomerId,
            order.CustomerEmail,
            order.Status.ToString(),
            order.Total,
            order.Currency,
            order.RejectionReason,
            order.CreatedAt,
            order.UpdatedAt,
            lines);

        return Results.Ok(response);
    }

    private static async Task<IResult> GetOrdersAsync(
        OrderingDbContext db,
        [FromQuery] Guid? customerId,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (page < 1)
        {
            page = 1;
        }

        if (pageSize < 1 || pageSize > 100)
        {
            pageSize = 20;
        }

        var query = db.Orders.AsNoTracking().AsQueryable();

        if (customerId.HasValue && customerId.Value != Guid.Empty)
        {
            query = query.Where(o => o.CustomerId == customerId.Value);
        }

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<OrderStatus>(status, ignoreCase: true, out var parsedStatus))
        {
            query = query.Where(o => o.Status == parsedStatus);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new OrderSummaryResponse(
                o.Id,
                o.CustomerId,
                o.Status.ToString(),
                o.Total,
                o.Currency,
                o.CreatedAt))
            .ToListAsync(cancellationToken);

        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        return Results.Ok(new PagedResult<OrderSummaryResponse>(items, page, pageSize, totalCount, totalPages));
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
                        ["code"] = "Ordering.ValidationFailed"
                    });
            }
        }

        return await next(context);
    }
}
