namespace Wallet.Core.Models;

/// <summary>
/// The identity and metadata of a wallet, as exposed outside the money tables.
/// Balances are read separately (see <see cref="CurrencyBalance"/>) because they are
/// derived from the ledger rather than stored on the wallet.
/// </summary>
public sealed record WalletAccount(
    Guid PublicId,
    string UserId,
    string? Metadata,
    DateTime CreatedAtUtc,
    DateTime? DeletedAtUtc)
{
    public bool IsActive => DeletedAtUtc is null;
}
