using Microsoft.Extensions.DependencyInjection;
using Wallet.Core;
using Wallet.Core.Abstractions;
using Wallet.Core.Errors;
using Wallet.Core.Models;

namespace Wallet.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public sealed class WalletDebitTests
{
    private readonly DatabaseFixture _fixture;

    public WalletDebitTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private IWalletRepository Repository(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IWalletRepository>();

    [SkippableFact]
    public async Task Debit_reduces_the_balance()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var scope = _fixture.Services.CreateScope();
        var repo = Repository(scope);
        var now = DateTime.UtcNow;

        var wallet = await repo.CreateWalletAsync("debit-basic");
        await repo.CreditAsync(wallet.PublicId, new CreditRequest(Guid.NewGuid(), Money.Create(100m, "USD")));

        var receipt = await repo.DebitAsync(wallet.PublicId, new DebitRequest(Guid.NewGuid(), Money.Create(30m, "USD")), now);

        Assert.False(receipt.Replayed);
        Assert.Equal(30m, Assert.Single(receipt.Allocations).Amount);
        var balances = await repo.GetBalancesAsync(wallet.PublicId, now);
        Assert.Equal(70m, Assert.Single(balances).Available);
    }

    [SkippableFact]
    public async Task Debit_draws_from_the_oldest_expiring_bag_first()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var scope = _fixture.Services.CreateScope();
        var repo = Repository(scope);
        var now = DateTime.UtcNow;

        var wallet = await repo.CreateWalletAsync("debit-fifo");
        var later = await repo.CreditAsync(wallet.PublicId,
            new CreditRequest(Guid.NewGuid(), Money.Create(30m, "USD"), ExpirationUtc: now.AddDays(10)));
        var sooner = await repo.CreditAsync(wallet.PublicId,
            new CreditRequest(Guid.NewGuid(), Money.Create(30m, "USD"), ExpirationUtc: now.AddDays(5)));

        var receipt = await repo.DebitAsync(wallet.PublicId, new DebitRequest(Guid.NewGuid(), Money.Create(40m, "USD")), now);

        // The sooner-expiring bag is fully drained first, then the remainder comes from the later one.
        Assert.Equal(2, receipt.Allocations.Count);
        Assert.Equal(sooner.CreditId, receipt.Allocations[0].CreditId);
        Assert.Equal(30m, receipt.Allocations[0].Amount);
        Assert.Equal(later.CreditId, receipt.Allocations[1].CreditId);
        Assert.Equal(10m, receipt.Allocations[1].Amount);

        var balances = await repo.GetBalancesAsync(wallet.PublicId, now);
        Assert.Equal(20m, Assert.Single(balances).Available);
    }

    [SkippableFact]
    public async Task Debit_over_balance_throws_and_changes_nothing()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var scope = _fixture.Services.CreateScope();
        var repo = Repository(scope);
        var now = DateTime.UtcNow;

        var wallet = await repo.CreateWalletAsync("debit-overdraft");
        await repo.CreditAsync(wallet.PublicId, new CreditRequest(Guid.NewGuid(), Money.Create(10m, "USD")));

        await Assert.ThrowsAsync<InsufficientFundsException>(
            () => repo.DebitAsync(wallet.PublicId, new DebitRequest(Guid.NewGuid(), Money.Create(20m, "USD")), now));

        // The rollback leaves the balance untouched and creates no allocations.
        var balances = await repo.GetBalancesAsync(wallet.PublicId, now);
        Assert.Equal(10m, Assert.Single(balances).Available);
    }

    [SkippableFact]
    public async Task Debit_is_idempotent_on_event_id()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var scope = _fixture.Services.CreateScope();
        var repo = Repository(scope);
        var now = DateTime.UtcNow;

        var wallet = await repo.CreateWalletAsync("debit-replay");
        await repo.CreditAsync(wallet.PublicId, new CreditRequest(Guid.NewGuid(), Money.Create(100m, "USD")));
        var request = new DebitRequest(Guid.NewGuid(), Money.Create(30m, "USD"));

        var first = await repo.DebitAsync(wallet.PublicId, request, now);
        var second = await repo.DebitAsync(wallet.PublicId, request, now);

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.DebitId, second.DebitId);

        // The replay must not spend twice: the balance dropped by 30 only once.
        var balances = await repo.GetBalancesAsync(wallet.PublicId, now);
        Assert.Equal(70m, Assert.Single(balances).Available);
    }

    [SkippableFact]
    public async Task Withdrawal_only_draws_from_refundable_bags()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var scope = _fixture.Services.CreateScope();
        var repo = Repository(scope);
        var now = DateTime.UtcNow;

        var wallet = await repo.CreateWalletAsync("debit-withdrawal");
        var refundable = await repo.CreditAsync(wallet.PublicId,
            new CreditRequest(Guid.NewGuid(), Money.Create(20m, "USD"), IsRefundable: true));
        await repo.CreditAsync(wallet.PublicId,
            new CreditRequest(Guid.NewGuid(), Money.Create(20m, "USD"), IsRefundable: false));

        var receipt = await repo.DebitAsync(wallet.PublicId,
            new DebitRequest(Guid.NewGuid(), Money.Create(20m, "USD"), DebitKind.Withdrawal), now);

        // The withdrawal drew only from the refundable bag.
        Assert.Equal(refundable.CreditId, Assert.Single(receipt.Allocations).CreditId);

        // With the refundable bag empty, another withdrawal cannot be covered, even though 20 non-refundable remains.
        await Assert.ThrowsAsync<InsufficientFundsException>(
            () => repo.DebitAsync(wallet.PublicId,
                new DebitRequest(Guid.NewGuid(), Money.Create(1m, "USD"), DebitKind.Withdrawal), now));
    }

    [SkippableFact]
    public async Task Statement_lists_credits_and_debits_in_time_order()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var scope = _fixture.Services.CreateScope();
        var repo = Repository(scope);
        var now = DateTime.UtcNow;

        var wallet = await repo.CreateWalletAsync("statement");
        await repo.CreditAsync(wallet.PublicId, new CreditRequest(Guid.NewGuid(), Money.Create(50m, "USD")));
        await repo.DebitAsync(wallet.PublicId, new DebitRequest(Guid.NewGuid(), Money.Create(20m, "USD")), now);

        var statement = await repo.GetStatementAsync(wallet.PublicId);

        Assert.Equal(2, statement.Count);
        Assert.Equal(MovementType.Credit, statement[0].Type);
        Assert.Equal(50m, statement[0].Amount.Amount);
        Assert.Equal(MovementType.Debit, statement[1].Type);
        Assert.Equal(20m, statement[1].Amount.Amount);
        Assert.Equal(DebitKind.Spend, statement[1].Kind);
    }
}
