namespace Wallet.Core.Models;

/// <summary>
/// The available balance of a wallet in a single currency, derived from its unexpired
/// credit bags minus what has been spent from them.
/// </summary>
public sealed record CurrencyBalance(string Currency, decimal Available)
{
    public Money AsMoney() => Money.Create(Available, Currency);
}
