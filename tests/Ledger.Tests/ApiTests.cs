using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ledger.Api;
using Ledger.Core;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ledger.Tests;

/// <summary>End-to-end over HTTP: the same rules, seen as status codes.</summary>
[Collection("postgres")]
public sealed class ApiTests(PostgresFixture pg) : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new WebApplicationFactory<Program>()
        .WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Ledger", pg.ConnectionString));

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Accounts_entries_and_holds_round_trip()
    {
        var http = _factory.CreateClient();

        var funding = await Create(http, new CreateAccountRequest("funding", "EUR", AllowNegative: true));
        var alice = await Create(http, new CreateAccountRequest("alice", "EUR"));
        var shop = await Create(http, new CreateAccountRequest("shop", "EUR"));

        var fund = await http.PostAsJsonAsync("/entries", new PostEntryRequest("fund-1", "top up",
            [new(funding.Id, -5_000), new(alice.Id, 5_000)]));
        Assert.Equal(HttpStatusCode.Created, fund.StatusCode);

        var replay = await http.PostAsJsonAsync("/entries", new PostEntryRequest("fund-1", "top up",
            [new(funding.Id, -5_000), new(alice.Id, 5_000)]));
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal((await fund.Content.ReadFromJsonAsync<Entry>())!.Id, (await replay.Content.ReadFromJsonAsync<Entry>())!.Id);

        var conflict = await http.PostAsJsonAsync("/entries", new PostEntryRequest("fund-1", "top up",
            [new(funding.Id, -6_000), new(alice.Id, 6_000)]));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var overdraw = await http.PostAsJsonAsync("/entries", new PostEntryRequest("od-1", "too much",
            [new(alice.Id, -9_000), new(shop.Id, 9_000)]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, overdraw.StatusCode);
        Assert.Equal("insufficient_funds", (await overdraw.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);

        var auth = await http.PostAsJsonAsync("/holds", new AuthorizeRequest("auth-1", alice.Id, 1_200));
        Assert.Equal(HttpStatusCode.Created, auth.StatusCode);
        var hold = (await auth.Content.ReadFromJsonAsync<HoldView>(Json))!;

        var afterHold = (await http.GetFromJsonAsync<AccountResponse>($"/accounts/{alice.Id}"))!;
        Assert.Equal(5_000, afterHold.Balance);
        Assert.Equal(3_800, afterHold.Available);

        var capture = await http.PostAsJsonAsync($"/holds/{hold.Hold.Id}/capture", new CaptureRequest("cap-1", shop.Id, 1_000));
        Assert.Equal(HttpStatusCode.OK, capture.StatusCode);

        var alicePost = (await http.GetFromJsonAsync<AccountResponse>($"/accounts/{alice.Id}"))!;
        var shopPost = (await http.GetFromJsonAsync<AccountResponse>($"/accounts/{shop.Id}"))!;
        Assert.Equal(4_000, alicePost.Balance);
        Assert.Equal(3_800, alicePost.Available);   // 200 of the hold is still open
        Assert.Equal(1_000, shopPost.Balance);

        var release = await http.PostAsJsonAsync($"/holds/{hold.Hold.Id}/release", new ReleaseRequest("rel-1"));
        Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        Assert.Equal(HoldStatus.Closed, (await release.Content.ReadFromJsonAsync<HoldView>(Json))!.Status);
        Assert.Contains("\"status\":\"Closed\"", await release.Content.ReadAsStringAsync());
        Assert.Equal(4_000, (await http.GetFromJsonAsync<AccountResponse>($"/accounts/{alice.Id}"))!.Available);

        var missing = await http.GetAsync($"/accounts/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static async Task<AccountResponse> Create(HttpClient http, CreateAccountRequest req)
    {
        var res = await http.PostAsJsonAsync("/accounts", req);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<AccountResponse>())!;
    }
}
