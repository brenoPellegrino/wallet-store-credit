namespace Wallet.Core.Errors;

/// <summary>
/// Raised when a monetary value or currency is invalid, for example too many decimal
/// places for the <c>DECIMAL(19,4)</c> storage type or a malformed currency code.
/// </summary>
public class MoneyException : Exception
{
    public MoneyException(string message) : base(message)
    {
    }
}

/// <summary>
/// Raised when an operation combines two <see cref="Money"/> values of different currencies.
/// A debit only ever draws from bags of its own currency, so mixing currencies is a bug.
/// </summary>
public sealed class CurrencyMismatchException : MoneyException
{
    public CurrencyMismatchException(string left, string right)
        : base($"Cannot combine money of different currencies: '{left}' and '{right}'.")
    {
        Left = left;
        Right = right;
    }

    public string Left { get; }

    public string Right { get; }
}
