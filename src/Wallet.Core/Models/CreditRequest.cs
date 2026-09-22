namespace Wallet.Core.Models;

/// <summary>
/// A request to add a credit bag to a wallet. The <see cref="EventId"/> is the client-supplied
/// idempotency key: replaying the same request never adds a second bag.
/// </summary>
public sealed record CreditRequest(
    Guid EventId,
    Money Amount,
    bool IsRefundable = false,
    DateTime? ExpirationUtc = null);
