using Microsoft.AspNetCore.Mvc;
using Wallet.Api.Contracts;
using Wallet.Core;
using Wallet.Core.Abstractions;
using Wallet.Core.Errors;
using Wallet.Core.Models;

namespace Wallet.Api.Controllers;

[ApiController]
[Route("wallets")]
public sealed class WalletsController : ControllerBase
{
    private readonly IWalletRepository _wallets;

    public WalletsController(IWalletRepository wallets)
    {
        _wallets = wallets;
    }

    /// <summary>Creates a wallet.</summary>
    [HttpPost]
    public async Task<ActionResult<WalletResponse>> Create(
        CreateWalletRequest request,
        CancellationToken cancellationToken)
    {
        var wallet = await _wallets.CreateWalletAsync(request.UserId, request.Metadata, cancellationToken);
        return CreatedAtAction(nameof(Get), new { publicId = wallet.PublicId }, ToResponse(wallet));
    }

    /// <summary>Gets a wallet by its public id.</summary>
    [HttpGet("{publicId:guid}")]
    public async Task<ActionResult<WalletResponse>> Get(Guid publicId, CancellationToken cancellationToken)
    {
        var wallet = await _wallets.GetWalletAsync(publicId, cancellationToken);
        return wallet is null || !wallet.IsActive ? NotFound() : Ok(ToResponse(wallet));
    }

    /// <summary>Credits a wallet. Replaying the same <c>EventId</c> returns the original credit.</summary>
    [HttpPost("{publicId:guid}/credits")]
    public async Task<ActionResult<CreditResponse>> Credit(
        Guid publicId,
        CreditWalletRequest request,
        CancellationToken cancellationToken)
    {
        Money amount;
        try
        {
            amount = Money.Create(request.Amount, request.Currency);
        }
        catch (MoneyException ex)
        {
            return ValidationProblem(ex.Message);
        }

        var creditRequest = new CreditRequest(request.EventId, amount, request.IsRefundable, request.ExpirationUtc);

        try
        {
            var receipt = await _wallets.CreditAsync(publicId, creditRequest, cancellationToken);
            return Ok(ToResponse(receipt));
        }
        catch (WalletNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>Gets the wallet's available balance per currency, as of now.</summary>
    [HttpGet("{publicId:guid}/balances")]
    public async Task<ActionResult<IEnumerable<BalanceResponse>>> Balances(
        Guid publicId,
        CancellationToken cancellationToken)
    {
        var wallet = await _wallets.GetWalletAsync(publicId, cancellationToken);
        if (wallet is null || !wallet.IsActive)
        {
            return NotFound();
        }

        var balances = await _wallets.GetBalancesAsync(publicId, DateTime.UtcNow, cancellationToken);
        return Ok(balances.Select(b => new BalanceResponse(b.Currency, b.Available)));
    }

    private static WalletResponse ToResponse(WalletAccount wallet) => new(
        wallet.PublicId, wallet.UserId, wallet.Metadata, wallet.CreatedAtUtc, wallet.DeletedAtUtc);

    private static CreditResponse ToResponse(CreditReceipt receipt) => new(
        receipt.CreditId,
        receipt.EventId,
        receipt.Amount.Amount,
        receipt.Amount.Currency,
        receipt.IsRefundable,
        receipt.ExpirationUtc,
        receipt.CreatedAtUtc,
        receipt.Replayed);
}
