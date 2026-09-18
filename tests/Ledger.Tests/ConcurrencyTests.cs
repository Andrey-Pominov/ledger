using Dapper;
using Ledger.Core;

namespace Ledger.Tests;

/// <summary>
/// These run real parallel transactions against PostgreSQL. They are the reason the
/// service locks accounts with SELECT ... FOR UPDATE instead of trusting a read-then-write.
/// </summary>
[Collection("postgres")]
public sealed class ConcurrencyTests(PostgresFixture pg)
{
    private LedgerService Ledger => pg.Ledger;

    [Fact]
    public async Task Parallel_transfers_never_overdraw_the_source()
    {
        const int balance = 100, each = 10, attempts = 40;   // only 10 of 40 can succeed
        var a = await pg.FundedAccountAsync(balance);
        var b = await pg.AccountAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, attempts).Select(async i =>
        {
            try
            {
                await Ledger.PostEntryAsync($"xfer-{a.Id}-{i}", "parallel", [new(a.Id, -each), new(b.Id, each)]);
                return true;
            }
            catch (InsufficientFundsException) { return false; }
        }));

        Assert.Equal(balance / each, results.Count(ok => ok));
        Assert.Equal(0, (await Ledger.GetAccountAsync(a.Id)).Balance);
        Assert.Equal(balance, (await Ledger.GetAccountAsync(b.Id)).Balance);
    }

    [Fact]
    public async Task Parallel_holds_never_reserve_more_than_available()
    {
        const int balance = 100, each = 30, attempts = 20;   // only 3 of 20 can succeed
        var a = await pg.FundedAccountAsync(balance);

        var results = await Task.WhenAll(Enumerable.Range(0, attempts).Select(async i =>
        {
            try { await Ledger.AuthorizeAsync($"hold-{a.Id}-{i}", a.Id, each); return true; }
            catch (InsufficientFundsException) { return false; }
        }));

        Assert.Equal(balance / each, results.Count(ok => ok));
        Assert.Equal(balance - each * (balance / each), (await Ledger.GetAccountAsync(a.Id)).Available);
    }

    [Fact]
    public async Task Parallel_partial_captures_of_one_hold_never_exceed_it()
    {
        const int held = 300, each = 50, attempts = 12;   // only 6 of 12 fit
        var a = await pg.FundedAccountAsync(1_000); var shop = await pg.AccountAsync("shop");
        var hold = await Ledger.AuthorizeAsync(Guid.NewGuid().ToString(), a.Id, held);

        var results = await Task.WhenAll(Enumerable.Range(0, attempts).Select(async i =>
        {
            try { await Ledger.CaptureAsync(hold.Hold.Id, $"cap-{hold.Hold.Id}-{i}", shop.Id, each); return true; }
            catch (InvalidHoldStateException) { return false; }
        }));

        Assert.Equal(held / each, results.Count(ok => ok));
        var view = await Ledger.GetHoldAsync(hold.Hold.Id);
        Assert.Equal(HoldStatus.Closed, view.Status);
        Assert.Equal(held, view.Captured);
        Assert.Equal(held, (await Ledger.GetAccountAsync(shop.Id)).Balance);
        Assert.Equal(1_000 - held, (await Ledger.GetAccountAsync(a.Id)).Balance);
    }

    [Fact]
    public async Task Parallel_requests_with_one_idempotency_key_write_exactly_once()
    {
        var a = await pg.FundedAccountAsync(1_000); var b = await pg.AccountAsync();
        var key = Guid.NewGuid().ToString();

        var entries = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            Ledger.PostEntryAsync(key, "same", [new(a.Id, -100), new(b.Id, 100)])));

        Assert.Single(entries.Select(e => e.Id).Distinct());
        Assert.Equal(900, (await Ledger.GetAccountAsync(a.Id)).Balance);
    }

    [Fact]
    public async Task Transfers_in_both_directions_between_two_accounts_do_not_deadlock()
    {
        var a = await pg.FundedAccountAsync(10_000); var b = await pg.FundedAccountAsync(10_000);

        // Lock order is by account id, so a→b and b→a writers acquire the same locks in the same order.
        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => i % 2 == 0
            ? Ledger.PostEntryAsync($"ab-{a.Id}-{i}", "a→b", [new(a.Id, -1), new(b.Id, 1)])
            : Ledger.PostEntryAsync($"ba-{a.Id}-{i}", "b→a", [new(b.Id, -1), new(a.Id, 1)])));

        Assert.Equal(10_000, (await Ledger.GetAccountAsync(a.Id)).Balance);
        Assert.Equal(10_000, (await Ledger.GetAccountAsync(b.Id)).Balance);
    }

    [Fact]
    public async Task After_everything_the_whole_ledger_still_sums_to_zero()
    {
        // Runs last-ish by name within the collection; asserts the global invariant over everything
        // every other test in this run has written. Money is never created or destroyed.
        await using var conn = await pg.Db.OpenConnectionAsync();
        var total = await conn.ExecuteScalarAsync<long>("select coalesce(sum(amount), 0) from postings");
        var entries = await conn.ExecuteScalarAsync<long>("select count(*) from entries");
        Assert.Equal(0, total);
        Assert.True(entries > 0);
    }
}
