namespace Wallet.Core.Models;

/// <summary>Whether a statement movement is money in (a credit) or money out (a debit).</summary>
public enum MovementType
{
    Credit,
    Debit,
}

/// <summary>
/// One movement on a wallet statement. The amount is always positive; the sign is read from
/// <see cref="Type"/> (credit is money in, debit is money out). <see cref="Kind"/> is set only for
/// debits.
/// </summary>
public sealed record StatementEntry(
    MovementType Type,
    long EntryId,
    Guid EventId,
    Money Amount,
    DebitKind? Kind,
    DateTime CreatedAtUtc);
