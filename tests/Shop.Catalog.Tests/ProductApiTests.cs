using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Shop.Catalog.Api.Products;
using Shouldly;
using Xunit;

namespace Shop.Catalog.Tests;

public sealed class ProductApiTests : IClassFixture<CatalogApiFactory>
{
    private readonly HttpClient _client;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ProductApiTests(CatalogApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Response_Headers_Contain_X_Trace_Id()
    {
        var response = await _client.GetAsync("/alive");
        response.Headers.Contains("X-Trace-Id").ShouldBeTrue();
    }

    [Fact]
    public async Task Post_Product_With_Valid_Body_Returns_201_And_Location_Header()
    {
        var request = new CreateProductRequest("SKU-API-1", "Test Monitor", 299.99m, 10, "USD");
        var response = await _client.PostAsJsonAsync("/api/v1/products", request);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location!.ToString().ShouldContain("/api/v1/products/");

        var created = await response.Content.ReadFromJsonAsync<ProductResponse>(JsonOptions);
        created.ShouldNotBeNull();
        created.Sku.ShouldBe("SKU-API-1");
        created.Name.ShouldBe("Test Monitor");
        created.Price.ShouldBe(299.99m);
        created.AvailableStock.ShouldBe(10);
    }

    [Fact]
    public async Task Post_Product_With_Duplicate_Sku_Returns_409_Conflict_ProblemDetails()
    {
        var sku = $"SKU-DUP-{Guid.NewGuid():N}"[..12];
        var request1 = new CreateProductRequest(sku, "Product 1", 50m, 5, "USD");
        var res1 = await _client.PostAsJsonAsync("/api/v1/products", request1);
        res1.StatusCode.ShouldBe(HttpStatusCode.Created);

        var request2 = new CreateProductRequest(sku, "Product 2", 60m, 10, "USD");
        var res2 = await _client.PostAsJsonAsync("/api/v1/products", request2);

        res2.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var content = await res2.Content.ReadAsStringAsync();
        content.ShouldContain("Catalog.DuplicateSku");
    }

    [Fact]
    public async Task Post_Product_With_Invalid_Data_Returns_400_Validation_ProblemDetails()
    {
        // Name is required, price cannot be negative
        var invalid = new CreateProductRequest("", "", -50m, -1, "USD");
        var response = await _client.PostAsJsonAsync("/api/v1/products", invalid);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("Catalog.ValidationFailed");
    }

    [Fact]
    public async Task Get_Product_By_Id_Returns_200_When_Exists()
    {
        var createRequest = new CreateProductRequest($"SKU-{Guid.NewGuid():N}"[..10], "Desk Lamp", 35.00m, 20, "USD");
        var createRes = await _client.PostAsJsonAsync("/api/v1/products", createRequest);
        var created = await createRes.Content.ReadFromJsonAsync<ProductResponse>(JsonOptions);
        created.ShouldNotBeNull();

        var getRes = await _client.GetAsync($"/api/v1/products/{created.Id}");
        getRes.StatusCode.ShouldBe(HttpStatusCode.OK);

        var fetched = await getRes.Content.ReadFromJsonAsync<ProductResponse>(JsonOptions);
        fetched.ShouldNotBeNull();
        fetched.Id.ShouldBe(created.Id);
        fetched.Name.ShouldBe("Desk Lamp");
    }

    [Fact]
    public async Task Get_Product_By_Id_Returns_404_When_Not_Found()
    {
        var unknownId = Guid.NewGuid();
        var response = await _client.GetAsync($"/api/v1/products/{unknownId}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("Catalog.ProductNotFound");
    }

    [Fact]
    public async Task Put_Product_Updates_Name_And_Price()
    {
        var createRequest = new CreateProductRequest($"SKU-{Guid.NewGuid():N}"[..10], "Initial Name", 20.00m, 5, "USD");
        var createRes = await _client.PostAsJsonAsync("/api/v1/products", createRequest);
        var created = await createRes.Content.ReadFromJsonAsync<ProductResponse>(JsonOptions);
        created.ShouldNotBeNull();

        var updateRequest = new UpdateProductRequest("Updated Name", 29.99m);
        var updateRes = await _client.PutAsJsonAsync($"/api/v1/products/{created.Id}", updateRequest);
        updateRes.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updated = await updateRes.Content.ReadFromJsonAsync<ProductResponse>(JsonOptions);
        updated.ShouldNotBeNull();
        updated.Name.ShouldBe("Updated Name");
        updated.Price.ShouldBe(29.99m);
    }

    [Fact]
    public async Task Post_Restock_Increases_Stock()
    {
        var createRequest = new CreateProductRequest($"SKU-{Guid.NewGuid():N}"[..10], "Stock Item", 15.00m, 10, "USD");
        var createRes = await _client.PostAsJsonAsync("/api/v1/products", createRequest);
        var created = await createRes.Content.ReadFromJsonAsync<ProductResponse>(JsonOptions);
        created.ShouldNotBeNull();

        var restockRequest = new RestockProductRequest(25);
        var restockRes = await _client.PostAsJsonAsync($"/api/v1/products/{created.Id}/restock", restockRequest);
        restockRes.StatusCode.ShouldBe(HttpStatusCode.OK);

        var restocked = await restockRes.Content.ReadFromJsonAsync<ProductResponse>(JsonOptions);
        restocked.ShouldNotBeNull();
        restocked.AvailableStock.ShouldBe(35);
    }

    [Fact]
    public async Task Get_Internal_Lookup_Returns_Matching_Products()
    {
        var p1 = new CreateProductRequest($"SKU-{Guid.NewGuid():N}"[..10], "Lookup P1", 10m, 10, "USD");
        var res1 = await _client.PostAsJsonAsync("/api/v1/products", p1);
        var c1 = await res1.Content.ReadFromJsonAsync<ProductResponse>(JsonOptions);

        var p2 = new CreateProductRequest($"SKU-{Guid.NewGuid():N}"[..10], "Lookup P2", 20m, 20, "USD");
        var res2 = await _client.PostAsJsonAsync("/api/v1/products", p2);
        var c2 = await res2.Content.ReadFromJsonAsync<ProductResponse>(JsonOptions);

        c1.ShouldNotBeNull();
        c2.ShouldNotBeNull();

        var lookupRes = await _client.GetAsync($"/internal/v1/products/lookup?ids={c1.Id},{c2.Id}");
        lookupRes.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await lookupRes.Content.ReadFromJsonAsync<ProductLookupResponse>(JsonOptions);
        result.ShouldNotBeNull();
        result.Products.Count.ShouldBe(2);
        result.Products.ShouldContain(p => p.Id == c1.Id && p.Price == 10m);
        result.Products.ShouldContain(p => p.Id == c2.Id && p.Price == 20m);
    }

    [Fact]
    public async Task Get_Internal_Lookup_With_Empty_Ids_Returns_400()
    {
        var lookupRes = await _client.GetAsync("/internal/v1/products/lookup?ids=");
        lookupRes.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var content = await lookupRes.Content.ReadAsStringAsync();
        content.ShouldContain("Catalog.MissingIds");
    }
}
