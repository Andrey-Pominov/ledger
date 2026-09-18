using Ledger.Core;

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
        await Ledger.AuthorizeAsync(Key(), a.Id, 300);

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
    public async Task Capture_moves_the_money_and_releases_the_remainder()
    {
        var a = await pg.FundedAccountAsync(1_000); var merchant = await pg.AccountAsync("merchant");
        var hold = await Ledger.AuthorizeAsync(Key(), a.Id, 300);

        var (captured, entry) = await Ledger.CaptureAsync(hold.Id, merchant.Id, amount: 250);

        Assert.Equal(HoldStatus.Captured, captured.Status);
        Assert.Equal(entry.Id, captured.CapturedEntry);
        var view = await Ledger.GetAccountAsync(a.Id);
        Assert.Equal(750, view.Balance);
        Assert.Equal(750, view.Available);            // the uncaptured 50 is no longer held
        Assert.Equal(250, (await Ledger.GetAccountAsync(merchant.Id)).Balance);
    }

    [Fact]
    public async Task Capture_cannot_exceed_the_held_amount()
    {
        var a = await pg.FundedAccountAsync(1_000); var merchant = await pg.AccountAsync("merchant");
        var hold = await Ledger.AuthorizeAsync(Key(), a.Id, 300);
        await Assert.ThrowsAsync<InvalidHoldStateException>(() => Ledger.CaptureAsync(hold.Id, merchant.Id, amount: 301));
    }

    [Fact]
    public async Task Capturing_twice_returns_the_same_entry()
    {
        var a = await pg.FundedAccountAsync(1_000); var merchant = await pg.AccountAsync("merchant");
        var hold = await Ledger.AuthorizeAsync(Key(), a.Id, 300);
        var (_, first) = await Ledger.CaptureAsync(hold.Id, merchant.Id);
        var (_, second) = await Ledger.CaptureAsync(hold.Id, merchant.Id);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(300, (await Ledger.GetAccountAsync(merchant.Id)).Balance);
    }

    [Fact]
    public async Task Release_restores_available_and_is_a_no_op_the_second_time()
    {
        var a = await pg.FundedAccountAsync(1_000);
        var hold = await Ledger.AuthorizeAsync(Key(), a.Id, 300);
        Assert.Equal(700, (await Ledger.GetAccountAsync(a.Id)).Available);

        var released = await Ledger.ReleaseAsync(hold.Id);
        Assert.Equal(HoldStatus.Released, released.Status);
        Assert.Equal(1_000, (await Ledger.GetAccountAsync(a.Id)).Available);

        Assert.Equal(HoldStatus.Released, (await Ledger.ReleaseAsync(hold.Id)).Status);
        await Assert.ThrowsAsync<InvalidHoldStateException>(() => Ledger.CaptureAsync(hold.Id, a.Id));
    }

    [Fact]
    public async Task A_captured_hold_cannot_be_released()
    {
        var a = await pg.FundedAccountAsync(1_000); var merchant = await pg.AccountAsync("merchant");
        var hold = await Ledger.AuthorizeAsync(Key(), a.Id, 300);
        await Ledger.CaptureAsync(hold.Id, merchant.Id);
        await Assert.ThrowsAsync<InvalidHoldStateException>(() => Ledger.ReleaseAsync(hold.Id));
    }

    [Fact]
    public async Task Authorize_is_idempotent_by_key()
    {
        var a = await pg.FundedAccountAsync(1_000);
        var key = Key();
        var first = await Ledger.AuthorizeAsync(key, a.Id, 300);
        var second = await Ledger.AuthorizeAsync(key, a.Id, 300);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(700, (await Ledger.GetAccountAsync(a.Id)).Available);   // held once, not twice
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => Ledger.AuthorizeAsync(key, a.Id, 301));
    }
}
