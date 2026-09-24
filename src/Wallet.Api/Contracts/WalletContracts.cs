using System.ComponentModel.DataAnnotations;
using Wallet.Core;

namespace Wallet.Api.Contracts;

/// <summary>Request body for creating a wallet.</summary>
public sealed record CreateWalletRequest(
    [Required] string UserId,
    string? Metadata);

/// <summary>Request body for crediting a wallet. <c>EventId</c> is the idempotency key.</summary>
public sealed record CreditWalletRequest(
    [Required] Guid EventId,
    [Range(typeof(decimal), "0.0001", "999999999999999.9999", ParseLimitsInInvariantCulture = true)] decimal Amount,
    [Required][StringLength(3, MinimumLength = 3)] string Currency,
    bool IsRefundable = false,
    DateTime? ExpirationUtc = null);

/// <summary>A wallet as returned by the API.</summary>
public sealed record WalletResponse(
    Guid PublicId,
    string UserId,
    string? Metadata,
    DateTime CreatedAtUtc,
    DateTime? DeletedAtUtc);

/// <summary>The result of a credit, including whether it was an idempotent replay.</summary>
public sealed record CreditResponse(
    long CreditId,
    Guid EventId,
    decimal Amount,
    string Currency,
    bool IsRefundable,
    DateTime? ExpirationUtc,
    DateTime CreatedAtUtc,
    bool Replayed);

/// <summary>One currency's available balance.</summary>
public sealed record BalanceResponse(string Currency, decimal Available);

/// <summary>Request body for debiting a wallet. <c>EventId</c> is the idempotency key.</summary>
public sealed record DebitWalletRequest(
    [Required] Guid EventId,
    [Range(typeof(decimal), "0.0001", "999999999999999.9999", ParseLimitsInInvariantCulture = true)] decimal Amount,
    [Required][StringLength(3, MinimumLength = 3)] string Currency,
    DebitKind Kind = DebitKind.Spend);

/// <summary>How a debit drew from one credit bag.</summary>
public sealed record DebitAllocationResponse(long CreditId, decimal Amount);

/// <summary>The result of a debit, including whether it was an idempotent replay.</summary>
public sealed record DebitResponse(
    long DebitId,
    Guid EventId,
    decimal Amount,
    string Currency,
    DebitKind Kind,
    DateTime CreatedAtUtc,
    bool Replayed,
    IReadOnlyList<DebitAllocationResponse> Allocations);

/// <summary>One movement on a wallet statement. <c>Amount</c> is positive; the sign is in <c>Type</c>.</summary>
public sealed record StatementEntryResponse(
    string Type,
    long EntryId,
    Guid EventId,
    decimal Amount,
    string Currency,
    DebitKind? Kind,
    DateTime CreatedAtUtc);

/// <summary>Request body for a transfer. <c>EventId</c> is the idempotency key for the whole move.</summary>
public sealed record TransferWalletRequest(
    [Required] Guid EventId,
    [Required] Guid SourceWalletId,
    [Required] Guid DestinationWalletId,
    [Range(typeof(decimal), "0.0001", "999999999999999.9999", ParseLimitsInInvariantCulture = true)] decimal Amount,
    [Required][StringLength(3, MinimumLength = 3)] string Currency);

/// <summary>The result of a transfer: the source debit and destination credit that committed together.</summary>
public sealed record TransferResponse(
    Guid EventId,
    Guid SourceWalletId,
    Guid DestinationWalletId,
    decimal Amount,
    string Currency,
    bool Replayed,
    DebitResponse Debit,
    CreditResponse Credit);
