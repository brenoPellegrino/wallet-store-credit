using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Wallet.Core;
using Wallet.Core.Abstractions;
using Wallet.Core.Errors;
using Wallet.Core.Models;

namespace Wallet.IntegrationTests;

/// <summary>
/// Stress test for the classic transfer deadlock: A -> B and B -> A running at the same time. Fires
/// many transfers in both directions released at the same instant, and asserts none is killed as a
/// deadlock victim (SQL error 1205) and that the total is conserved.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class WalletTransferConcurrencyTests
{
    private const int DeadlockVictim = 1205;
    private const int PerDirection = 40; // 80 transfers fired together, under the default pool of 100

    private readonly DatabaseFixture _fixture;

    public WalletTransferConcurrencyTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Bidirectional_transfers_do_not_deadlock()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        var now = DateTime.UtcNow;

        Guid a, b;
        using (var scope = _fixture.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IWalletRepository>();
            a = (await repo.CreateWalletAsync("deadlock-a")).PublicId;
            b = (await repo.CreateWalletAsync("deadlock-b")).PublicId;
            await repo.CreditAsync(a, new CreditRequest(Guid.NewGuid(), Money.Create(1000m, "USD")));
            await repo.CreditAsync(b, new CreditRequest(Guid.NewGuid(), Money.Create(1000m, "USD")));
        }

        var gate = new TaskCompletionSource();
        var deadlocks = 0;
        var sqlNumbers = new ConcurrentBag<int>();
        var unexpected = new ConcurrentBag<Exception>();

        async Task Transfer(Guid from, Guid to)
        {
            using var scope = _fixture.Services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IWalletRepository>();
            await gate.Task;
            try
            {
                await repo.TransferAsync(from, to, new TransferRequest(Guid.NewGuid(), Money.Create(1m, "USD")), now);
            }
            catch (InsufficientFundsException)
            {
                // Not expected with these balances, but harmless if it happens.
            }
            catch (SqlException ex)
            {
                sqlNumbers.Add(ex.Number);
                if (ex.Number == DeadlockVictim)
                {
                    Interlocked.Increment(ref deadlocks);
                }
            }
            catch (Exception ex)
            {
                unexpected.Add(ex);
            }
        }

        var tasks = new List<Task>();
        for (var i = 0; i < PerDirection; i++)
        {
            tasks.Add(Transfer(a, b));
            tasks.Add(Transfer(b, a));
        }

        gate.SetResult();
        await Task.WhenAll(tasks);

        var sqlSummary = string.Join(",", sqlNumbers.GroupBy(n => n).Select(g => $"{g.Key}x{g.Count()}"));
        var detail = string.Join(" || ", unexpected.Select(e =>
            $"{e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("Wallet."))?.Trim()}").Take(3));
        Assert.True(unexpected.IsEmpty && deadlocks == 0,
            $"deadlocks={deadlocks}; sqlNumbers=[{sqlSummary}]; unexpected({unexpected.Count})=[{detail}]");

        // Transfers between A and B only move money between them, so the total never changes.
        using (var scope = _fixture.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IWalletRepository>();
            var total = await BalanceAsync(repo, a, now) + await BalanceAsync(repo, b, now);
            Assert.Equal(2000m, total);
        }
    }

    private static async Task<decimal> BalanceAsync(IWalletRepository repo, Guid wallet, DateTime now)
    {
        var balances = await repo.GetBalancesAsync(wallet, now);
        return balances.Count == 0 ? 0m : balances.Single().Available;
    }
}
