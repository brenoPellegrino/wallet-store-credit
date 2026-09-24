namespace Wallet.Core.Models;

/// <summary>
/// A request to move money from one wallet to another. The <see cref="EventId"/> is the idempotency
/// key: it is used for both the source debit and the destination credit, so replaying the same
/// transfer moves nothing new.
/// </summary>
public sealed record TransferRequest(Guid EventId, Money Amount);

/// <summary>
/// The result of a transfer: the debit taken from the source and the credit added to the
/// destination, which happen in one transaction. <see cref="Replayed"/> is true when the transfer
/// had already been applied and nothing moved this time.
/// </summary>
public sealed record TransferReceipt(
    Guid EventId,
    Money Amount,
    Guid SourcePublicId,
    Guid DestinationPublicId,
    DebitReceipt Debit,
    CreditReceipt Credit,
    bool Replayed);
