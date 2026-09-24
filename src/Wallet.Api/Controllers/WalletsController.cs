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
        if (wallet is null || !wallet.IsActive)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Wallet not found");
        }

        return Ok(ToResponse(wallet));
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
            return Ok(receipt.ToResponse());
        }
        catch (WalletNotFoundException)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Wallet not found");
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
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Wallet not found");
        }

        var balances = await _wallets.GetBalancesAsync(publicId, DateTime.UtcNow, cancellationToken);
        return Ok(balances.Select(b => new BalanceResponse(b.Currency, b.Available)));
    }

    /// <summary>Debits a wallet, drawing from bags oldest-expiring first. Idempotent on <c>EventId</c>.</summary>
    [HttpPost("{publicId:guid}/debits")]
    public async Task<ActionResult<DebitResponse>> Debit(
        Guid publicId,
        DebitWalletRequest request,
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

        var debitRequest = new DebitRequest(request.EventId, amount, request.Kind);

        try
        {
            var receipt = await _wallets.DebitAsync(publicId, debitRequest, DateTime.UtcNow, cancellationToken);
            return Ok(receipt.ToResponse());
        }
        catch (WalletNotFoundException)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Wallet not found");
        }
        catch (InsufficientFundsException ex)
        {
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>Gets the wallet's movements (credits and debits) in time order.</summary>
    [HttpGet("{publicId:guid}/statement")]
    public async Task<ActionResult<IEnumerable<StatementEntryResponse>>> Statement(
        Guid publicId,
        CancellationToken cancellationToken)
    {
        try
        {
            var entries = await _wallets.GetStatementAsync(publicId, cancellationToken);
            return Ok(entries.Select(e => new StatementEntryResponse(
                e.Type.ToString(), e.EntryId, e.EventId, e.Amount.Amount, e.Amount.Currency, e.Kind, e.CreatedAtUtc)));
        }
        catch (WalletNotFoundException)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Wallet not found");
        }
    }

    private static WalletResponse ToResponse(WalletAccount wallet) => new(
        wallet.PublicId, wallet.UserId, wallet.Metadata, wallet.CreatedAtUtc, wallet.DeletedAtUtc);
}
