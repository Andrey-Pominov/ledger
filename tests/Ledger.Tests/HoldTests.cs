using Dapper;
using Ledger.Core;
using Npgsql;

namespace Ledger.Tests;

[Collection("postgres")]
public sealed class HoldTests(PostgresFixture pg)
{
    private LedgerService Ledger => pg.Ledger;
    private static string Key() => Guid.NewGuid().ToString();

    [Fact]
    public async Task A_hold_reduces_available_but_not_balance()
    {
        var a = await pg.FundedAccountAsync(1_000);
        var hold = await Ledger.AuthorizeAsync(Key(), a.Id, 300);

        Assert.Equal(HoldStatus.Open, hold.Status);
        Assert.Equal(300, hold.Remaining);
        var view = await Ledger.GetAccountAsync(a.Id);
        Assert.Equal(1_000, view.Balance);
        Assert.Equal(700, view.Available);
    }

    [Fact]
    public async Task A_hold_cannot_exceed_available_funds()
    {
        var a = await pg.FundedAccountAsync(100);
        await Ledger.AuthorizeAsync(Key(), a.Id, 80);
        var ex = await Assert.ThrowsAsync<InsufficientFundsException>(() => Ledger.AuthorizeAsync(Key(), a.Id, 30));
        Assert.Equal(20, ex.Available);
    }

    [Fact]
    public async Task Held_funds_cannot_be_spent_by_a_plain_entry_either()
    {
        var a = await pg.FundedAccountAsync(100); var b = await pg.AccountAsync();
        await Ledger.AuthorizeAsync(Key(), a.Id, 80);
        await Assert.ThrowsAsync<InsufficientFundsException>(() =>
            Ledger.PostEntryAsync(Key(), "spend held", [new(a.Id, -30), new(b.Id, 30)]));
    }

    [Fact]
    public async Task A_hold_can_be_captured_in_several_parts_until_nothing_remains()
    {
        var a = await pg.FundedAccountAsync(1_000); var shop = await pg.AccountAsync("shop");
        var hold = await Ledger.AuthorizeAsync(Key(), a.Id, 300);

        var (afterFirst, _) = await Ledger.CaptureAsync(hold.Hold.Id, Key(), shop.Id, 120);
        Assert.Equal(HoldStatus.Open, afterFirst.Status);
        Assert.Equal(120, afterFirst.Captured);
        Assert.Equal(180, afterFirst.Remaining);

        var (afterSecond, _) = await Ledger.CaptureAsync(hold.Hold.Id, Key(), shop.Id, 180);
        Assert.Equal(HoldStatus.Closed, afterSecond.Status);
        Assert.Equal(300, afterSecond.Captured);
        Assert.Equal(0, afterSecond.Remaining);

        var view = await Ledger.GetAccountAsync(a.Id);
        Assert.Equal(700, view.Balance);
        Assert.Equal(700, view.Available);
        Assert.Equal(300, (await Ledger.GetAccountAsync(shop.Id)).Balance);
    }

    [Fact]
    public async Task Captures_cannot_exceed_what_remains_on_the_hold()
    {
        var a = await pg.FundedAccountAsync(1_000); var shop = await pg.AccountAsync("shop");
        var hold = await Ledger.AuthorizeAsync(Key(), a.Id, 300);
        await Ledger.CaptureAsync(hold.Hold.Id, Key(), shop.Id, 200);

        var ex = await Assert.ThrowsAsync<InvalidHoldStateException>(() => Ledger.CaptureAsync(hold.Hold.Id, Key(), shop.Id, 101));
        Assert.Contains("100] remaining", ex.Message);
        Assert.Equal(200, (await Ledger.GetAccountAsync(shop.Id)).Balance);
    }

    [Fact]
    public async Task Capture_is_idempotent_by_key_and_a_full_capture_leaves_nothing_held()
    {
        var a = await pg.FundedAccountAsync(1_000); var shop = await pg.AccountAsync("shop");
        var hold = await Ledger.AuthorizeAsync(Key(), a.Id, 300);
        var key = Key();

        var (_, first) = await Ledger.CaptureAsync(hold.Hold.Id, key, shop.Id);
        var (view, second) = await Ledger.CaptureAsync(hold.Hold.Id, key, shop.Id);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(HoldStatus.Closed, view.Status);
        Assert.Equal(300, (await Ledger.GetAccountAsync(shop.Id)).Balance);
        Assert.Equal(700, (await Ledger.GetAccountAsync(a.Id)).Available);
    }

    [Fact]
    public async Task Partial_capture_then_release_restores_the_rest()
    {
        var a = await pg.FundedAccountAsync(1_000); var shop = await pg.AccountAsync("shop");
        var hold = await Ledger.AuthorizeAsync(Key(), a.Id, 300);
        await Ledger.CaptureAsync(hold.Hold.Id, Key(), shop.Id, 250);
        Assert.Equal(700, (await Ledger.GetAccountAsync(a.Id)).Available);   // 250 gone, 50 still held

        var released = await Ledger.ReleaseAsync(hold.Hold.Id, Key());
        Assert.Equal(HoldStatus.Closed, released.Status);
        Assert.Equal(50, released.Released);
        Assert.Equal(750, (await Ledger.GetAccountAsync(a.Id)).Available);
    }

    [Fact]
    public async Task Release_is_idempotent_and_a_closed_hold_cannot_be_captured()
    {
        var a = await pg.FundedAccountAsync(1_000);
        var hold = await Ledger.AuthorizeAsync(Key(), a.Id, 300);
        var key = Key();

        await Ledger.ReleaseAsync(hold.Hold.Id, key);
        var again = await Ledger.ReleaseAsync(hold.Hold.Id, key);           // same key: no-op
        var another = await Ledger.ReleaseAsync(hold.Hold.Id, Key());      // nothing left: no-op
        Assert.Equal(HoldStatus.Closed, again.Status);
        Assert.Equal(300, another.Released);
        Assert.Equal(1_000, (await Ledger.GetAccountAsync(a.Id)).Available);

        await Assert.ThrowsAsync<InvalidHoldStateException>(() => Ledger.CaptureAsync(hold.Hold.Id, Key(), a.Id));
        await Assert.ThrowsAsync<InvalidHoldStateException>(() => Ledger.ReleaseAsync(hold.Hold.Id, Key(), amount: 1));
    }

    [Fact]
    public async Task Authorize_is_idempotent_by_key()
    {
        var a = await pg.FundedAccountAsync(1_000);
        var key = Key();
        var first = await Ledger.AuthorizeAsync(key, a.Id, 300);
        var second = await Ledger.AuthorizeAsync(key, a.Id, 300);
        Assert.Equal(first.Hold.Id, second.Hold.Id);
        Assert.Equal(700, (await Ledger.GetAccountAsync(a.Id)).Available);   // held once, not twice
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => Ledger.AuthorizeAsync(key, a.Id, 301));
    }

    [Fact]
    public async Task An_expired_hold_stops_reserving_funds_and_cannot_be_captured()
    {
        var a = await pg.FundedAccountAsync(1_000); var shop = await pg.AccountAsync("shop");
        var hold = await Ledger.AuthorizeAsync(Key(), a.Id, 300, timeout: TimeSpan.FromMilliseconds(200));
        Assert.Equal(700, (await Ledger.GetAccountAsync(a.Id)).Available);

        await Task.Delay(400);

        Assert.Equal(HoldStatus.Expired, (await Ledger.GetHoldAsync(hold.Hold.Id)).Status);
        Assert.Equal(1_000, (await Ledger.GetAccountAsync(a.Id)).Available);   // no sweeper ran; the clock did
        var ex = await Assert.ThrowsAsync<InvalidHoldStateException>(() => Ledger.CaptureAsync(hold.Hold.Id, Key(), shop.Id));
        Assert.Contains("expired", ex.Message);
    }

    [Fact]
    public async Task Holds_and_releases_are_immutable_at_the_database_level()
    {
        var a = await pg.FundedAccountAsync(1_000);
        var hold = await Ledger.AuthorizeAsync(Key(), a.Id, 300);
        await Ledger.ReleaseAsync(hold.Hold.Id, Key(), amount: 100);
        await using var conn = await pg.Db.OpenConnectionAsync();

        var h = await Assert.ThrowsAsync<PostgresException>(() =>
            conn.ExecuteAsync("update holds set amount = 1 where id = @id", new { id = hold.Hold.Id }));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, h.SqlState);

        var r = await Assert.ThrowsAsync<PostgresException>(() =>
            conn.ExecuteAsync("delete from hold_releases where hold_id = @id", new { id = hold.Hold.Id }));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, r.SqlState);
    }
}
