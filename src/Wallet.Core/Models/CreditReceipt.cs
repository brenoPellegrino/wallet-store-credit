namespace Wallet.Core.Models;

/// <summary>
/// The result of crediting a wallet: the bag that now exists for the request's
/// <see cref="EventId"/>. When <see cref="Replayed"/> is true the bag already existed and no
/// new money was added, which is how idempotency is reported to the caller.
/// </summary>
public sealed record CreditReceipt(
    long CreditId,
    Guid EventId,
    Money Amount,
    bool IsRefundable,
    DateTime? ExpirationUtc,
    DateTime CreatedAtUtc,
    bool Replayed);
