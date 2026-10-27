using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Shop.Ordering.Api.Clients;

public sealed record CatalogProductItem(
    Guid Id,
    string Sku,
    string Name,
    decimal Price,
    string Currency,
    int AvailableStock
);

public sealed record CatalogLookupResult(
    IReadOnlyList<CatalogProductItem> Products
);

public class CatalogUnavailableException : Exception
{
    public CatalogUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

public interface ICatalogClient
{
    Task<CatalogLookupResult> GetProductsAsync(IEnumerable<Guid> productIds, CancellationToken cancellationToken = default);
}

public sealed class CatalogClient : ICatalogClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CatalogClient> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public CatalogClient(HttpClient httpClient, ILogger<CatalogClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<CatalogLookupResult> GetProductsAsync(IEnumerable<Guid> productIds, CancellationToken cancellationToken = default)
    {
        var idList = productIds.Distinct().ToList();
        if (idList.Count == 0)
        {
            return new CatalogLookupResult([]);
        }

        var query = string.Join(",", idList);
        var uri = $"/internal/v1/products/lookup?ids={query}";

        try
        {
            var response = await _httpClient.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Catalog service returned non-success code {StatusCode}: {ErrorBody}", response.StatusCode, errorBody);
                throw new CatalogUnavailableException($"Catalog lookup failed with HTTP {(int)response.StatusCode}.");
            }

            var result = await response.Content.ReadFromJsonAsync<CatalogLookupResult>(JsonOptions, cancellationToken);
            return result ?? new CatalogLookupResult([]);
        }
        catch (Exception ex) when (ex is not CatalogUnavailableException and not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to connect to Catalog service at {Uri}", uri);
            throw new CatalogUnavailableException("Catalog service is currently unavailable or unreachable.", ex);
        }
    }
}
