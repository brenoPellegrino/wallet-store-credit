using Microsoft.Extensions.DependencyInjection;
using Wallet.Core;
using Wallet.Core.Abstractions;
using Wallet.Core.Errors;
using Wallet.Core.Models;

namespace Wallet.IntegrationTests;

/// <summary>
/// Proves that concurrent debits on the same wallet never overspend. Without the wallet-row
/// UPDLOCK in usp_DebitWallet, racing debits could each read the same "available" and all succeed,
/// spending a bag more than once. These tests fire many debits at the same instant and assert the
/// money adds up.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class WalletConcurrencyTests
{
    private readonly DatabaseFixture _fixture;

    public WalletConcurrencyTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Concurrent_full_balance_debits_let_exactly_one_win()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        var now = DateTime.UtcNow;

        Guid wallet;
        using (var scope = _fixture.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IWalletRepository>();
            wallet = (await repo.CreateWalletAsync("race-winner-takes-all")).PublicId;
            await repo.CreditAsync(wallet, new CreditRequest(Guid.NewGuid(), Money.Create(100m, "USD")));
        }

        // 12 debits, each for the whole balance, released at the same instant.
        var outcomes = await RunConcurrentDebitsAsync(wallet, amount: 100m, count: 12, now);

        Assert.Equal(1, outcomes.Count(success => success));
        Assert.Equal(0m, await BalanceAsync(wallet, now));
    }

    [SkippableFact]
    public async Task Concurrent_partial_debits_never_overspend()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        var now = DateTime.UtcNow;

        Guid wallet;
        using (var scope = _fixture.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IWalletRepository>();
            wallet = (await repo.CreateWalletAsync("race-partial")).PublicId;
            await repo.CreditAsync(wallet, new CreditRequest(Guid.NewGuid(), Money.Create(100m, "USD")));
        }

        // 10 debits of 30 against a balance of 100: serialized, exactly three fit (90), the rest fail.
        var outcomes = await RunConcurrentDebitsAsync(wallet, amount: 30m, count: 10, now);

        Assert.Equal(3, outcomes.Count(success => success));
        var remaining = await BalanceAsync(wallet, now);
        Assert.Equal(10m, remaining);
        Assert.True(remaining >= 0m, "the balance must never go negative");
    }

    private async Task<IReadOnlyList<bool>> RunConcurrentDebitsAsync(Guid wallet, decimal amount, int count, DateTime now)
    {
        // A gate so every task calls DebitAsync at the same moment, maximizing contention.
        var gate = new TaskCompletionSource();

        var tasks = Enumerable.Range(0, count).Select(async _ =>
        {
            using var scope = _fixture.Services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IWalletRepository>();
            await gate.Task;
            try
            {
                await repo.DebitAsync(wallet, new DebitRequest(Guid.NewGuid(), Money.Create(amount, "USD")), now);
                return true;
            }
            catch (InsufficientFundsException)
            {
                return false;
            }
        }).ToList();

        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    private async Task<decimal> BalanceAsync(Guid wallet, DateTime now)
    {
        using var scope = _fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWalletRepository>();
        var balances = await repo.GetBalancesAsync(wallet, now);
        return balances.Count == 0 ? 0m : balances.Single().Available;
    }
}
