using Wallet.Core;
using Wallet.Core.Errors;

namespace Wallet.UnitTests;

public sealed class MoneyTests
{
    [Fact]
    public void Create_keeps_amount_and_uppercases_currency()
    {
        var money = Money.Create(12.34m, "usd");

        Assert.Equal(12.34m, money.Amount);
        Assert.Equal("USD", money.Currency);
    }

    [Theory]
    [InlineData(" usd ", "USD")]
    [InlineData("EuR", "EUR")]
    public void Create_trims_and_normalizes_currency(string input, string expected)
    {
        Assert.Equal(expected, Money.Create(1m, input).Currency);
    }

    [Fact]
    public void Create_allows_up_to_four_decimal_places()
    {
        var money = Money.Create(0.0001m, "USD");
        Assert.Equal(0.0001m, money.Amount);
    }

    [Fact]
    public void Create_rejects_more_than_four_decimal_places()
    {
        var ex = Assert.Throws<MoneyException>(() => Money.Create(1.23456m, "USD"));
        Assert.Contains("decimal places", ex.Message);
    }

    [Theory]
    [InlineData("US")]
    [InlineData("USDD")]
    [InlineData("US1")]
    [InlineData("")]
    public void Create_rejects_invalid_currency(string currency)
    {
        Assert.Throws<MoneyException>(() => Money.Create(1m, currency));
    }

    [Fact]
    public void Trailing_zeros_do_not_affect_equality()
    {
        Assert.Equal(Money.Create(10m, "USD"), Money.Create(10.0000m, "USD"));
    }

    [Fact]
    public void Different_currency_is_not_equal()
    {
        Assert.NotEqual(Money.Create(10m, "USD"), Money.Create(10m, "EUR"));
    }

    [Fact]
    public void Addition_and_subtraction_work_within_a_currency()
    {
        Assert.Equal(Money.Create(30m, "USD"), Money.Create(10m, "USD") + Money.Create(20m, "USD"));
        Assert.Equal(Money.Create(7.5m, "USD"), Money.Create(10m, "USD") - Money.Create(2.5m, "USD"));
    }

    [Fact]
    public void Combining_different_currencies_throws()
    {
        var ex = Assert.Throws<CurrencyMismatchException>(() => Money.Create(1m, "USD") + Money.Create(1m, "EUR"));
        Assert.Equal("USD", ex.Left);
        Assert.Equal("EUR", ex.Right);
    }

    [Fact]
    public void Zero_is_zero_in_its_currency()
    {
        var zero = Money.Zero("USD");
        Assert.True(zero.IsZero);
        Assert.False(zero.IsPositive);
        Assert.Equal("USD", zero.Currency);
    }

    [Fact]
    public void ToString_shows_four_decimals_and_currency()
    {
        Assert.Equal("12.3000 USD", Money.Create(12.3m, "USD").ToString());
    }
}
