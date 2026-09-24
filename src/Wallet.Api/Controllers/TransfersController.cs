using Microsoft.AspNetCore.Mvc;
using Wallet.Api.Contracts;
using Wallet.Core;
using Wallet.Core.Abstractions;
using Wallet.Core.Errors;
using Wallet.Core.Models;

namespace Wallet.Api.Controllers;

[ApiController]
[Route("transfers")]
public sealed class TransfersController : ControllerBase
{
    private readonly IWalletRepository _wallets;

    public TransfersController(IWalletRepository wallets)
    {
        _wallets = wallets;
    }

    /// <summary>
    /// Moves money from one wallet to another in a single transaction. Idempotent on <c>EventId</c>.
    /// Returns 409 if the source cannot cover the amount, 404 if either wallet is missing.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<TransferResponse>> Transfer(
        TransferWalletRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SourceWalletId == request.DestinationWalletId)
        {
            return ValidationProblem("Source and destination must be different wallets.");
        }

        Money amount;
        try
        {
            amount = Money.Create(request.Amount, request.Currency);
        }
        catch (MoneyException ex)
        {
            return ValidationProblem(ex.Message);
        }

        try
        {
            var receipt = await _wallets.TransferAsync(
                request.SourceWalletId,
                request.DestinationWalletId,
                new TransferRequest(request.EventId, amount),
                DateTime.UtcNow,
                cancellationToken);

            return Ok(new TransferResponse(
                receipt.EventId,
                receipt.SourcePublicId,
                receipt.DestinationPublicId,
                receipt.Amount.Amount,
                receipt.Amount.Currency,
                receipt.Replayed,
                receipt.Debit.ToResponse(),
                receipt.Credit.ToResponse()));
        }
        catch (WalletNotFoundException)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Source or destination wallet not found");
        }
        catch (InsufficientFundsException ex)
        {
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
