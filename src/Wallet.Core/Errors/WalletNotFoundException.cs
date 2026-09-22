namespace Wallet.Core.Errors;

/// <summary>
/// Raised when an operation targets a wallet that does not exist or has been soft-deleted.
/// </summary>
public sealed class WalletNotFoundException : Exception
{
    public WalletNotFoundException(Guid publicId)
        : base($"Wallet '{publicId}' was not found or has been deleted.")
    {
        PublicId = publicId;
    }

    public Guid PublicId { get; }
}
