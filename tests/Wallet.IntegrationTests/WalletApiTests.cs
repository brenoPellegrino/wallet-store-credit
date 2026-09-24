using System.Net;
using System.Net.Http.Json;

namespace Wallet.IntegrationTests;

/// <summary>
/// Exercises the API through the HTTP pipeline (model binding, validation, routing), which the
/// repository-level tests skip. This is the layer that caught the record-validation attribute bug.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class WalletApiTests
{
    private readonly DatabaseFixture _fixture;

    public WalletApiTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Create_credit_and_read_balance_over_http()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        var client = _fixture.CreateClient();

        var createResponse = await client.PostAsJsonAsync("/wallets", new { userId = "http-user" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var wallet = await createResponse.Content.ReadFromJsonAsync<WalletDto>();
        Assert.NotNull(wallet);

        var eventId = Guid.NewGuid();
        var credit = new { eventId, amount = 42.50m, currency = "USD" };

        var first = await client.PostAsJsonAsync($"/wallets/{wallet!.PublicId}/credits", credit);
        first.EnsureSuccessStatusCode();
        var firstBody = await first.Content.ReadFromJsonAsync<CreditDto>();
        Assert.False(firstBody!.Replayed);

        var replay = await client.PostAsJsonAsync($"/wallets/{wallet.PublicId}/credits", credit);
        var replayBody = await replay.Content.ReadFromJsonAsync<CreditDto>();
        Assert.True(replayBody!.Replayed);

        var balances = await client.GetFromJsonAsync<List<BalanceDto>>($"/wallets/{wallet.PublicId}/balances");
        var usd = Assert.Single(balances!);
        Assert.Equal("USD", usd.Currency);
        Assert.Equal(42.50m, usd.Available);
    }

    [SkippableFact]
    public async Task Missing_user_id_is_rejected_with_400()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        var client = _fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/wallets", new { metadata = "no user id" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task Unknown_wallet_returns_404()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        var client = _fixture.CreateClient();

        var response = await client.GetAsync($"/wallets/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // Errors come back as ProblemDetails.
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [SkippableFact]
    public async Task Debit_over_http_spends_then_overdraft_returns_409()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        var client = _fixture.CreateClient();

        var create = await client.PostAsJsonAsync("/wallets", new { userId = "http-debit" });
        var wallet = await create.Content.ReadFromJsonAsync<WalletDto>();
        await client.PostAsJsonAsync($"/wallets/{wallet!.PublicId}/credits",
            new { eventId = Guid.NewGuid(), amount = 50m, currency = "USD" });

        var debit = await client.PostAsJsonAsync($"/wallets/{wallet.PublicId}/debits",
            new { eventId = Guid.NewGuid(), amount = 30m, currency = "USD" });
        debit.EnsureSuccessStatusCode();

        var balances = await client.GetFromJsonAsync<List<BalanceDto>>($"/wallets/{wallet.PublicId}/balances");
        Assert.Equal(20m, Assert.Single(balances!).Available);

        var overdraft = await client.PostAsJsonAsync($"/wallets/{wallet.PublicId}/debits",
            new { eventId = Guid.NewGuid(), amount = 100m, currency = "USD" });
        Assert.Equal(HttpStatusCode.Conflict, overdraft.StatusCode);
    }

    [SkippableFact]
    public async Task Statement_over_http_lists_movements()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        var client = _fixture.CreateClient();

        var create = await client.PostAsJsonAsync("/wallets", new { userId = "http-statement" });
        var wallet = await create.Content.ReadFromJsonAsync<WalletDto>();
        await client.PostAsJsonAsync($"/wallets/{wallet!.PublicId}/credits",
            new { eventId = Guid.NewGuid(), amount = 40m, currency = "USD" });
        await client.PostAsJsonAsync($"/wallets/{wallet.PublicId}/debits",
            new { eventId = Guid.NewGuid(), amount = 15m, currency = "USD" });

        var statement = await client.GetFromJsonAsync<List<StatementDto>>($"/wallets/{wallet.PublicId}/statement");

        Assert.Equal(2, statement!.Count);
        Assert.Equal("Credit", statement[0].Type);
        Assert.Equal("Debit", statement[1].Type);
    }

    [SkippableFact]
    public async Task Transfer_over_http_moves_money_between_wallets()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        var client = _fixture.CreateClient();

        var source = (await (await client.PostAsJsonAsync("/wallets", new { userId = "http-xfer-src" }))
            .Content.ReadFromJsonAsync<WalletDto>())!;
        var destination = (await (await client.PostAsJsonAsync("/wallets", new { userId = "http-xfer-dst" }))
            .Content.ReadFromJsonAsync<WalletDto>())!;
        await client.PostAsJsonAsync($"/wallets/{source.PublicId}/credits",
            new { eventId = Guid.NewGuid(), amount = 70m, currency = "USD" });

        var transfer = await client.PostAsJsonAsync("/transfers", new
        {
            eventId = Guid.NewGuid(),
            sourceWalletId = source.PublicId,
            destinationWalletId = destination.PublicId,
            amount = 30m,
            currency = "USD",
        });
        transfer.EnsureSuccessStatusCode();

        var sourceBalance = await client.GetFromJsonAsync<List<BalanceDto>>($"/wallets/{source.PublicId}/balances");
        var destBalance = await client.GetFromJsonAsync<List<BalanceDto>>($"/wallets/{destination.PublicId}/balances");
        Assert.Equal(40m, Assert.Single(sourceBalance!).Available);
        Assert.Equal(30m, Assert.Single(destBalance!).Available);
    }

    [SkippableFact]
    public async Task Transfer_to_self_returns_400()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        var client = _fixture.CreateClient();

        var wallet = (await (await client.PostAsJsonAsync("/wallets", new { userId = "http-xfer-self" }))
            .Content.ReadFromJsonAsync<WalletDto>())!;

        var response = await client.PostAsJsonAsync("/transfers", new
        {
            eventId = Guid.NewGuid(),
            sourceWalletId = wallet.PublicId,
            destinationWalletId = wallet.PublicId,
            amount = 10m,
            currency = "USD",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task Transfer_over_balance_returns_409()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        var client = _fixture.CreateClient();

        var source = (await (await client.PostAsJsonAsync("/wallets", new { userId = "http-xfer-poor" }))
            .Content.ReadFromJsonAsync<WalletDto>())!;
        var destination = (await (await client.PostAsJsonAsync("/wallets", new { userId = "http-xfer-rich" }))
            .Content.ReadFromJsonAsync<WalletDto>())!;
        await client.PostAsJsonAsync($"/wallets/{source.PublicId}/credits",
            new { eventId = Guid.NewGuid(), amount = 10m, currency = "USD" });

        var response = await client.PostAsJsonAsync("/transfers", new
        {
            eventId = Guid.NewGuid(),
            sourceWalletId = source.PublicId,
            destinationWalletId = destination.PublicId,
            amount = 50m,
            currency = "USD",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private sealed record WalletDto(Guid PublicId, string UserId);

    private sealed record CreditDto(long CreditId, bool Replayed);

    private sealed record BalanceDto(string Currency, decimal Available);

    private sealed record StatementDto(string Type, decimal Amount);
}
