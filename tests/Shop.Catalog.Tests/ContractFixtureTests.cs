using System;
using System.IO;
using System.Text.Json;
using Shop.Contracts;
using Shouldly;
using Xunit;

namespace Shop.Catalog.Tests;

public sealed class ContractFixtureTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string FindSolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "Shop.slnx")) || File.Exists(Path.Combine(dir, "Shop.sln")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Directory.GetCurrentDirectory();
    }

    [Fact]
    public void OrderPlaced_Fixture_Matches_Contract()
    {
        var fixturePath = Path.Combine(FindSolutionRoot(), "docs", "contracts", "events", "ordering.order-placed.v1.json");
        File.Exists(fixturePath).ShouldBeTrue();

        var json = File.ReadAllText(fixturePath);
        var evt = JsonSerializer.Deserialize<OrderPlacedIntegrationEvent>(json, JsonOptions);

        evt.ShouldNotBeNull();
        evt.OrderId.ShouldBe(Guid.Parse("018f6c40-1000-7000-8000-000000000010"));
        evt.CustomerEmail.ShouldBe("customer@example.com");
        evt.Items.Count.ShouldBe(1);
        evt.Items[0].Quantity.ShouldBe(2);
    }

    [Fact]
    public void StockReserved_Fixture_Matches_Contract()
    {
        var fixturePath = Path.Combine(FindSolutionRoot(), "docs", "contracts", "events", "catalog.stock-reserved.v1.json");
        File.Exists(fixturePath).ShouldBeTrue();

        var json = File.ReadAllText(fixturePath);
        var evt = JsonSerializer.Deserialize<StockReservedIntegrationEvent>(json, JsonOptions);

        evt.ShouldNotBeNull();
        evt.OrderId.ShouldBe(Guid.Parse("018f6c40-1000-7000-8000-000000000010"));
    }

    [Fact]
    public void StockRejected_Fixture_Matches_Contract()
    {
        var fixturePath = Path.Combine(FindSolutionRoot(), "docs", "contracts", "events", "catalog.stock-rejected.v1.json");
        File.Exists(fixturePath).ShouldBeTrue();

        var json = File.ReadAllText(fixturePath);
        var evt = JsonSerializer.Deserialize<StockRejectedIntegrationEvent>(json, JsonOptions);

        evt.ShouldNotBeNull();
        evt.OrderId.ShouldBe(Guid.Parse("018f6c40-1000-7000-8000-000000000010"));
        evt.Reason.ShouldContain("Insufficient stock");
        evt.UnavailableProductIds.Count.ShouldBe(1);
    }

    [Fact]
    public void OrderConfirmed_Fixture_Matches_Contract()
    {
        var fixturePath = Path.Combine(FindSolutionRoot(), "docs", "contracts", "events", "ordering.order-confirmed.v1.json");
        File.Exists(fixturePath).ShouldBeTrue();

        var json = File.ReadAllText(fixturePath);
        var evt = JsonSerializer.Deserialize<OrderConfirmedIntegrationEvent>(json, JsonOptions);

        evt.ShouldNotBeNull();
        evt.OrderId.ShouldBe(Guid.Parse("018f6c40-1000-7000-8000-000000000010"));
        evt.Total.ShouldBe(59.98m);
        evt.Currency.ShouldBe("USD");
    }

    [Fact]
    public void OrderRejected_Fixture_Matches_Contract()
    {
        var fixturePath = Path.Combine(FindSolutionRoot(), "docs", "contracts", "events", "ordering.order-rejected.v1.json");
        File.Exists(fixturePath).ShouldBeTrue();

        var json = File.ReadAllText(fixturePath);
        var evt = JsonSerializer.Deserialize<OrderRejectedIntegrationEvent>(json, JsonOptions);

        evt.ShouldNotBeNull();
        evt.OrderId.ShouldBe(Guid.Parse("018f6c40-1000-7000-8000-000000000010"));
        evt.Reason.ShouldContain("Stock reservation rejected");
    }
}
