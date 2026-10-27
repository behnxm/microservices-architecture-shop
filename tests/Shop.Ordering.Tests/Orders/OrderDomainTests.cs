using System;
using System.Collections.Generic;
using Shop.Ordering.Api.Orders;
using Shouldly;
using Xunit;

namespace Shop.Ordering.Tests.Orders;

public sealed class OrderDomainTests
{
    [Fact]
    public void Create_Valid_Order_Calculates_Total_And_Sets_Pending_Status()
    {
        // Arrange
        var customerId = Guid.NewGuid();
        var email = "alice@example.com";
        var lines = new List<OrderLine>
        {
            OrderLine.Create(Guid.NewGuid(), "Mechanical Keyboard", 120.00m, 2),
            OrderLine.Create(Guid.NewGuid(), "USB-C Cable", 15.50m, 3)
        };

        // Act
        var order = Order.Create(customerId, email, lines);

        // Assert
        order.Id.ShouldNotBe(Guid.Empty);
        order.CustomerId.ShouldBe(customerId);
        order.CustomerEmail.ShouldBe("alice@example.com");
        order.Status.ShouldBe(OrderStatus.Pending);
        order.Currency.ShouldBe("USD");
        order.Total.ShouldBe((120.00m * 2) + (15.50m * 3)); // 240 + 46.50 = 286.50
        order.Lines.Count.ShouldBe(2);
        order.Version.ShouldBe(1);
        order.CreatedAt.ShouldBeInRange(DateTimeOffset.UtcNow.AddSeconds(-2), DateTimeOffset.UtcNow.AddSeconds(2));
    }

    [Fact]
    public void Create_Order_With_Empty_CustomerId_Throws_ArgumentException()
    {
        var lines = new List<OrderLine>
        {
            OrderLine.Create(Guid.NewGuid(), "Keyboard", 50m, 1)
        };

        Should.Throw<ArgumentException>(() => Order.Create(Guid.Empty, "alice@example.com", lines));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Create_Order_With_Invalid_Email_Throws_ArgumentException(string? email)
    {
        var lines = new List<OrderLine>
        {
            OrderLine.Create(Guid.NewGuid(), "Keyboard", 50m, 1)
        };

        Should.Throw<ArgumentException>(() => Order.Create(Guid.NewGuid(), email!, lines));
    }

    [Fact]
    public void Create_Order_With_Empty_Lines_Throws_ArgumentException()
    {
        Should.Throw<ArgumentException>(() => Order.Create(Guid.NewGuid(), "alice@example.com", []));
    }

    [Fact]
    public void Confirm_Transitions_From_Pending_To_Confirmed()
    {
        var order = CreateTestOrder();
        var initialVersion = order.Version;

        order.Confirm();

        order.Status.ShouldBe(OrderStatus.Confirmed);
        order.Version.ShouldBe(initialVersion + 1);
    }

    [Fact]
    public void Confirm_On_Already_Confirmed_Order_Is_Idempotent_NoOp()
    {
        var order = CreateTestOrder();
        order.Confirm();
        var version = order.Version;

        order.Confirm();

        order.Status.ShouldBe(OrderStatus.Confirmed);
        order.Version.ShouldBe(version);
    }

    [Fact]
    public void Confirm_On_Rejected_Order_Throws_InvalidOperationException()
    {
        var order = CreateTestOrder();
        order.Reject("Out of stock");

        Should.Throw<InvalidOperationException>(() => order.Confirm());
    }

    [Fact]
    public void Reject_Transitions_From_Pending_To_Rejected_With_Reason()
    {
        var order = CreateTestOrder();
        var initialVersion = order.Version;

        order.Reject("Item discontinued");

        order.Status.ShouldBe(OrderStatus.Rejected);
        order.RejectionReason.ShouldBe("Item discontinued");
        order.Version.ShouldBe(initialVersion + 1);
    }

    [Fact]
    public void Reject_On_Already_Rejected_Order_Is_Idempotent_NoOp()
    {
        var order = CreateTestOrder();
        order.Reject("Out of stock");
        var version = order.Version;

        order.Reject("Different reason");

        order.Status.ShouldBe(OrderStatus.Rejected);
        order.RejectionReason.ShouldBe("Out of stock");
        order.Version.ShouldBe(version);
    }

    [Fact]
    public void Reject_On_Confirmed_Order_Throws_InvalidOperationException()
    {
        var order = CreateTestOrder();
        order.Confirm();

        Should.Throw<InvalidOperationException>(() => order.Reject("Payment declined"));
    }

    [Fact]
    public void OrderLine_Validation_Rejects_Invalid_Arguments()
    {
        Should.Throw<ArgumentException>(() => OrderLine.Create(Guid.Empty, "Widget", 10m, 1));
        Should.Throw<ArgumentException>(() => OrderLine.Create(Guid.NewGuid(), "", 10m, 1));
        Should.Throw<ArgumentOutOfRangeException>(() => OrderLine.Create(Guid.NewGuid(), "Widget", -5m, 1));
        Should.Throw<ArgumentOutOfRangeException>(() => OrderLine.Create(Guid.NewGuid(), "Widget", 10m, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => OrderLine.Create(Guid.NewGuid(), "Widget", 10m, -2));
    }

    private static Order CreateTestOrder()
    {
        return Order.Create(
            Guid.NewGuid(),
            "test@example.com",
            [OrderLine.Create(Guid.NewGuid(), "Gadget", 49.99m, 1)]);
    }
}
