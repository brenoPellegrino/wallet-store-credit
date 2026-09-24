namespace Wallet.Core.Models;

/// <summary>
/// A request to spend from a wallet. The <see cref="EventId"/> is the client-supplied idempotency
/// key: replaying the same request never spends twice.
/// </summary>
public sealed record DebitRequest(
    Guid EventId,
    Money Amount,
    DebitKind Kind = DebitKind.Spend);

/// <summary>How much a debit drew from one credit bag.</summary>
public sealed record DebitAllocation(long CreditId, decimal Amount);

/// <summary>
/// The result of a debit: the debit that now exists for the request's <see cref="EventId"/> and how
/// it drew from each bag. When <see cref="Replayed"/> is true the debit already existed and nothing
/// new was spent.
/// </summary>
public sealed record DebitReceipt(
    long DebitId,
    Guid EventId,
    Money Amount,
    DebitKind Kind,
    DateTime CreatedAtUtc,
    bool Replayed,
    IReadOnlyList<DebitAllocation> Allocations);
