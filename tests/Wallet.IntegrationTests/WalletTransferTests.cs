using Microsoft.Extensions.DependencyInjection;
using Wallet.Core;
using Wallet.Core.Abstractions;
using Wallet.Core.Errors;
using Wallet.Core.Models;

namespace Wallet.IntegrationTests;

/// <summary>
/// Covers the client-side SqlTransaction that spans two wallets. A transfer is a debit from the
/// source and a credit to the destination that must commit together or not at all.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class WalletTransferTests
{
    private readonly DatabaseFixture _fixture;

    public WalletTransferTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Transfer_moves_money_and_preserves_the_total()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var scope = _fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWalletRepository>();
        var now = DateTime.UtcNow;

        var source = (await repo.CreateWalletAsync("transfer-source")).PublicId;
        var destination = (await repo.CreateWalletAsync("transfer-destination")).PublicId;
        await repo.CreditAsync(source, new CreditRequest(Guid.NewGuid(), Money.Create(100m, "USD")));

        var receipt = await repo.TransferAsync(source, destination, new TransferRequest(Guid.NewGuid(), Money.Create(40m, "USD")), now);

        Assert.False(receipt.Replayed);
        Assert.Equal(60m, await BalanceAsync(repo, source, now));
        Assert.Equal(40m, await BalanceAsync(repo, destination, now));
    }

    [SkippableFact]
    public async Task Transfer_over_balance_moves_nothing()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var scope = _fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWalletRepository>();
        var now = DateTime.UtcNow;

        var source = (await repo.CreateWalletAsync("transfer-short-source")).PublicId;
        var destination = (await repo.CreateWalletAsync("transfer-short-destination")).PublicId;
        await repo.CreditAsync(source, new CreditRequest(Guid.NewGuid(), Money.Create(100m, "USD")));

        await Assert.ThrowsAsync<InsufficientFundsException>(
            () => repo.TransferAsync(source, destination, new TransferRequest(Guid.NewGuid(), Money.Create(150m, "USD")), now));

        // Neither side changed: the debit never happened and the credit was rolled back with it.
        Assert.Equal(100m, await BalanceAsync(repo, source, now));
        Assert.Empty(await repo.GetBalancesAsync(destination, now));
    }

    [SkippableFact]
    public async Task Transfer_to_missing_destination_rolls_back_the_debit()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var scope = _fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWalletRepository>();
        var now = DateTime.UtcNow;

        var source = (await repo.CreateWalletAsync("transfer-orphan-source")).PublicId;
        await repo.CreditAsync(source, new CreditRequest(Guid.NewGuid(), Money.Create(100m, "USD")));

        // The debit runs first, then the credit fails because the destination does not exist.
        await Assert.ThrowsAsync<WalletNotFoundException>(
            () => repo.TransferAsync(source, Guid.NewGuid(), new TransferRequest(Guid.NewGuid(), Money.Create(40m, "USD")), now));

        // The whole transaction rolled back, so the source still has its full balance.
        Assert.Equal(100m, await BalanceAsync(repo, source, now));
    }

    [SkippableFact]
    public async Task Transfer_is_idempotent_on_event_id()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var scope = _fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWalletRepository>();
        var now = DateTime.UtcNow;

        var source = (await repo.CreateWalletAsync("transfer-replay-source")).PublicId;
        var destination = (await repo.CreateWalletAsync("transfer-replay-destination")).PublicId;
        await repo.CreditAsync(source, new CreditRequest(Guid.NewGuid(), Money.Create(100m, "USD")));
        var request = new TransferRequest(Guid.NewGuid(), Money.Create(40m, "USD"));

        var first = await repo.TransferAsync(source, destination, request, now);
        var second = await repo.TransferAsync(source, destination, request, now);

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);

        // The replay must not move money a second time.
        Assert.Equal(60m, await BalanceAsync(repo, source, now));
        Assert.Equal(40m, await BalanceAsync(repo, destination, now));
    }

    private static async Task<decimal> BalanceAsync(IWalletRepository repo, Guid wallet, DateTime now)
    {
        var balances = await repo.GetBalancesAsync(wallet, now);
        return balances.Count == 0 ? 0m : balances.Single().Available;
    }
}
