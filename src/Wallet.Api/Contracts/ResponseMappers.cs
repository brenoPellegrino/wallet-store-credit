using Wallet.Core.Models;

namespace Wallet.Api.Contracts;

/// <summary>Maps domain receipts to API responses. Shared so credit, debit and transfer agree.</summary>
internal static class ResponseMappers
{
    public static CreditResponse ToResponse(this CreditReceipt receipt) => new(
        receipt.CreditId,
        receipt.EventId,
        receipt.Amount.Amount,
        receipt.Amount.Currency,
        receipt.IsRefundable,
        receipt.ExpirationUtc,
        receipt.CreatedAtUtc,
        receipt.Replayed);

    public static DebitResponse ToResponse(this DebitReceipt receipt) => new(
        receipt.DebitId,
        receipt.EventId,
        receipt.Amount.Amount,
        receipt.Amount.Currency,
        receipt.Kind,
        receipt.CreatedAtUtc,
        receipt.Replayed,
        receipt.Allocations.Select(a => new DebitAllocationResponse(a.CreditId, a.Amount)).ToList());
}
