namespace Wallet.Core.Errors;

/// <summary>
/// Raised when a wallet's available bags cannot cover a requested debit. The debit is rolled back,
/// so no bag is partially spent.
/// </summary>
public sealed class InsufficientFundsException : Exception
{
    public InsufficientFundsException(Guid walletPublicId, Money requested)
        : base($"Wallet '{walletPublicId}' has insufficient funds for a debit of {requested}.")
    {
        PublicId = walletPublicId;
        Requested = requested;
    }

    public Guid PublicId { get; }

    public Money Requested { get; }
}
