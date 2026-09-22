using Wallet.Core.Models;

namespace Wallet.Core.Abstractions;

/// <summary>
/// The data access contract for wallets. Reads (wallet lookup, balances) are hand-written SQL;
/// the credit money path goes through the <c>usp_CreditWallet</c> stored procedure. The
/// implementation lives in the infrastructure layer over raw ADO.NET.
/// </summary>
public interface IWalletRepository
{
    /// <summary>Creates a new, active wallet and returns it.</summary>
    Task<WalletAccount> CreateWalletAsync(
        string userId,
        string? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the wallet with the given public id, or <c>null</c> if none exists.</summary>
    Task<WalletAccount?> GetWalletAsync(
        Guid publicId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a credit bag to a wallet through <c>usp_CreditWallet</c>. Replaying a request with an
    /// <see cref="CreditRequest.EventId"/> that was already applied returns the existing bag with
    /// <see cref="CreditReceipt.Replayed"/> set, and adds nothing.
    /// </summary>
    /// <exception cref="Errors.WalletNotFoundException">The wallet does not exist or is deleted.</exception>
    Task<CreditReceipt> CreditAsync(
        Guid walletPublicId,
        CreditRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the wallet's available balance per currency at <paramref name="asOfUtc"/>,
    /// counting only bags that have not expired.
    /// </summary>
    Task<IReadOnlyList<CurrencyBalance>> GetBalancesAsync(
        Guid walletPublicId,
        DateTime asOfUtc,
        CancellationToken cancellationToken = default);
}
