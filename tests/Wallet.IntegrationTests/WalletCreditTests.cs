using Microsoft.Extensions.DependencyInjection;
using Wallet.Core;
using Wallet.Core.Abstractions;
using Wallet.Core.Errors;
using Wallet.Core.Models;

namespace Wallet.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public sealed class WalletCreditTests
{
    private readonly DatabaseFixture _fixture;

    public WalletCreditTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Create_then_get_returns_the_same_wallet()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var scope = _fixture.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWalletRepository>();

        var created = await repository.CreateWalletAsync("user-abc", metadata: null);
        var fetched = await repository.GetWalletAsync(created.PublicId);

        Assert.NotNull(fetched);
        Assert.Equal(created.PublicId, fetched.PublicId);
        Assert.Equal("user-abc", fetched.UserId);
        Assert.True(fetched.IsActive);
    }

    [SkippableFact]
    public async Task Credit_adds_to_the_balance()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var scope = _fixture.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWalletRepository>();

        var wallet = await repository.CreateWalletAsync("user-credit");
        await repository.CreditAsync(wallet.PublicId, new CreditRequest(Guid.NewGuid(), Money.Create(40m, "USD")));
        await repository.CreditAsync(wallet.PublicId, new CreditRequest(Guid.NewGuid(), Money.Create(2.50m, "USD")));

        var balances = await repository.GetBalancesAsync(wallet.PublicId, DateTime.UtcNow);

        var usd = Assert.Single(balances);
        Assert.Equal("USD", usd.Currency);
        Assert.Equal(42.50m, usd.Available);
    }

    [SkippableFact]
    public async Task Credit_is_idempotent_on_event_id()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var scope = _fixture.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWalletRepository>();

        var wallet = await repository.CreateWalletAsync("user-replay");
        var eventId = Guid.NewGuid();
        var request = new CreditRequest(eventId, Money.Create(25m, "USD"));

        var first = await repository.CreditAsync(wallet.PublicId, request);
        var second = await repository.CreditAsync(wallet.PublicId, request);

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.CreditId, second.CreditId);

        // The replay must not add a second bag: the balance is still a single 25.00 credit.
        var balances = await repository.GetBalancesAsync(wallet.PublicId, DateTime.UtcNow);
        Assert.Equal(25m, Assert.Single(balances).Available);
    }

    [SkippableFact]
    public async Task Credit_keeps_currencies_separate()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var scope = _fixture.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWalletRepository>();

        var wallet = await repository.CreateWalletAsync("user-multi");
        await repository.CreditAsync(wallet.PublicId, new CreditRequest(Guid.NewGuid(), Money.Create(10m, "USD")));
        await repository.CreditAsync(wallet.PublicId, new CreditRequest(Guid.NewGuid(), Money.Create(30m, "EUR")));

        var balances = (await repository.GetBalancesAsync(wallet.PublicId, DateTime.UtcNow))
            .ToDictionary(b => b.Currency, b => b.Available);

        Assert.Equal(10m, balances["USD"]);
        Assert.Equal(30m, balances["EUR"]);
    }

    [SkippableFact]
    public async Task Credit_on_unknown_wallet_throws()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var scope = _fixture.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWalletRepository>();

        var request = new CreditRequest(Guid.NewGuid(), Money.Create(5m, "USD"));

        await Assert.ThrowsAsync<WalletNotFoundException>(
            () => repository.CreditAsync(Guid.NewGuid(), request));
    }
}
