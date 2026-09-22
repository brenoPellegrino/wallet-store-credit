using Wallet.Core.Errors;

namespace Wallet.Core;

/// <summary>
/// An exact monetary amount in a single currency.
/// </summary>
/// <remarks>
/// Money is stored as <c>DECIMAL(19,4)</c> in the database, so a value carries at most four
/// decimal places and never uses binary floating point. This type enforces that rule at the
/// domain boundary: it rejects over-precise amounts and refuses to combine different currencies.
/// It is a value type with structural equality, so two amounts are equal when both the amount
/// and the currency match.
/// </remarks>
public readonly struct Money : IEquatable<Money>
{
    /// <summary>The number of decimal places money is stored with, matching <c>DECIMAL(19,4)</c>.</summary>
    public const int Scale = 4;

    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    /// <summary>The amount, always rounded to <see cref="Scale"/> decimal places.</summary>
    public decimal Amount { get; }

    /// <summary>The ISO 4217 currency code, three upper-case letters.</summary>
    public string Currency { get; }

    public bool IsZero => Amount == 0m;

    public bool IsPositive => Amount > 0m;

    /// <summary>
    /// Creates a money value, validating the currency and rejecting amounts that carry more
    /// than <see cref="Scale"/> decimal places (which could not be stored without silent loss).
    /// </summary>
    public static Money Create(decimal amount, string currency)
    {
        var normalizedCurrency = NormalizeCurrency(currency);

        if (decimal.Round(amount, Scale) != amount)
        {
            throw new MoneyException(
                $"Amount {amount} has more than {Scale} decimal places and cannot be stored as money.");
        }

        // Round trailing precision away so equality and formatting are stable (12.30 == 12.3000).
        return new Money(decimal.Round(amount, Scale), normalizedCurrency);
    }

    /// <summary>A zero amount in the given currency.</summary>
    public static Money Zero(string currency) => new(0m, NormalizeCurrency(currency));

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount - right.Amount, left.Currency);
    }

    public static bool operator ==(Money left, Money right) => left.Equals(right);

    public static bool operator !=(Money left, Money right) => !left.Equals(right);

    public bool Equals(Money other) =>
        Amount == other.Amount && Currency == other.Currency;

    public override bool Equals(object? obj) => obj is Money other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Amount, Currency);

    public override string ToString() =>
        $"{Amount.ToString("F" + Scale, System.Globalization.CultureInfo.InvariantCulture)} {Currency}";

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (left.Currency != right.Currency)
        {
            throw new CurrencyMismatchException(left.Currency, right.Currency);
        }
    }

    private static string NormalizeCurrency(string currency)
    {
        if (currency is null)
        {
            throw new MoneyException("Currency is required.");
        }

        var normalized = currency.Trim().ToUpperInvariant();
        if (normalized.Length != 3 || !normalized.All(char.IsLetter))
        {
            throw new MoneyException($"Currency '{currency}' must be a three-letter ISO 4217 code.");
        }

        return normalized;
    }
}
