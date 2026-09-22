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
    }

    private sealed record WalletDto(Guid PublicId, string UserId);

    private sealed record CreditDto(long CreditId, bool Replayed);

    private sealed record BalanceDto(string Currency, decimal Available);
}
