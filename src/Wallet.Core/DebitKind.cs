namespace Wallet.Core;

/// <summary>How a debit is allowed to draw from a wallet's credit bags.</summary>
public enum DebitKind : byte
{
    /// <summary>Spend: may draw from any bag.</summary>
    Spend = 1,

    /// <summary>Withdrawal: may draw only from refundable bags.</summary>
    Withdrawal = 2,
}
