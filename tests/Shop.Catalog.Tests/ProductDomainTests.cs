using System;
using Shop.Catalog.Api.Products;
using Shouldly;
using Xunit;

namespace Shop.Catalog.Tests;

public sealed class ProductDomainTests
{
    [Fact]
    public void Create_ValidProduct_SetsInitialProperties()
    {
        var fixedId = Guid.CreateVersion7();
        var product = Product.Create("SKU-100", "Mechanical Keyboard", 89.99m, 25, "USD", fixedId);

        product.Id.ShouldBe(fixedId);
        product.Sku.ShouldBe("SKU-100");
        product.Name.ShouldBe("Mechanical Keyboard");
        product.Price.ShouldBe(89.99m);
        product.Currency.ShouldBe("USD");
        product.AvailableStock.ShouldBe(25);
        product.Version.ShouldBe(1);
        product.CreatedAt.ShouldBeGreaterThan(DateTimeOffset.MinValue);
        product.UpdatedAt.ShouldBeGreaterThan(DateTimeOffset.MinValue);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_InvalidSku_ThrowsArgumentException(string invalidSku)
    {
        Should.Throw<ArgumentException>(() => Product.Create(invalidSku, "Test Product", 10.0m, 5));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_InvalidName_ThrowsArgumentException(string invalidName)
    {
        Should.Throw<ArgumentException>(() => Product.Create("SKU-1", invalidName, 10.0m, 5));
    }

    [Fact]
    public void Create_NegativePrice_ThrowsArgumentOutOfRangeException()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Product.Create("SKU-1", "Product", -1.0m, 5));
    }

    [Fact]
    public void Create_NegativeStock_ThrowsArgumentOutOfRangeException()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Product.Create("SKU-1", "Product", 10.0m, -1));
    }

    [Fact]
    public void Update_ValidParameters_UpdatesNameAndPriceAndIncrementsVersion()
    {
        var product = Product.Create("SKU-1", "Old Name", 50.0m, 10);
        var initialVersion = product.Version;

        product.Update("New Name", 65.50m);

        product.Name.ShouldBe("New Name");
        product.Price.ShouldBe(65.50m);
        product.Version.ShouldBe(initialVersion + 1);
    }

    [Fact]
    public void Restock_ValidQuantity_IncreasesStockAndIncrementsVersion()
    {
        var product = Product.Create("SKU-1", "Test Product", 10.0m, 20);
        var initialVersion = product.Version;

        product.Restock(15);

        product.AvailableStock.ShouldBe(35);
        product.Version.ShouldBe(initialVersion + 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Restock_InvalidQuantity_ThrowsArgumentOutOfRangeException(int quantity)
    {
        var product = Product.Create("SKU-1", "Test Product", 10.0m, 20);
        Should.Throw<ArgumentOutOfRangeException>(() => product.Restock(quantity));
    }

    [Fact]
    public void Reserve_AdequateStock_DecrementsStockAndIncrementsVersion()
    {
        var product = Product.Create("SKU-1", "Test Product", 10.0m, 20);
        var initialVersion = product.Version;

        product.Reserve(5);

        product.AvailableStock.ShouldBe(15);
        product.Version.ShouldBe(initialVersion + 1);
    }

    [Fact]
    public void Reserve_InsufficientStock_ThrowsInvalidOperationExceptionAndPreservesStock()
    {
        var product = Product.Create("SKU-1", "Test Product", 10.0m, 10);
        var initialVersion = product.Version;

        var ex = Should.Throw<InvalidOperationException>(() => product.Reserve(15));
        ex.Message.ShouldContain("Insufficient stock");
        product.AvailableStock.ShouldBe(10);
        product.Version.ShouldBe(initialVersion);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Reserve_ZeroOrNegativeQuantity_ThrowsArgumentOutOfRangeException(int quantity)
    {
        var product = Product.Create("SKU-1", "Test Product", 10.0m, 10);
        Should.Throw<ArgumentOutOfRangeException>(() => product.Reserve(quantity));
    }
}
