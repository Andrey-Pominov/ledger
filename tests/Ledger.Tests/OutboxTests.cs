using Dapper;
using Ledger.Core;
using Npgsql;

namespace Ledger.Tests;

/// <summary>
/// The outbox is written in the same transaction as the change it describes, and read through
/// a stability rule that keeps a cursor from skipping rows whose transaction commits late.
/// </summary>
[Collection("postgres")]
public sealed class OutboxTests(PostgresFixture pg)
{
    private LedgerService Ledger => pg.Ledger;
    private static string Key() => Guid.NewGuid().ToString();

    /// <summary>Events from this test only: the collection shares one database, so filter by the ids we touched.</summary>
    private async Task<List<LedgerEvent>> EventsMentioning(long after, params Guid[] ids)
    {
        var all = new List<LedgerEvent>();
        var cursor = after;
        while (true)
        {
            var page = await Ledger.EventsAsync(cursor, 200);
            if (page.Events.Count == 0) break;
            all.AddRange(page.Events);
            cursor = page.Next;
        }
        return all.Where(e => ids.Any(id => e.Payload.GetRawText().Contains(id.ToString()))).ToList();
    }

    /// <summary>Where the outbox ends right now, so a test can read only what it wrote.</summary>
    private async Task<long> Cursor()
    {
        await using var conn = await pg.Db.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<long>("select coalesce(max(id), 0) from events");
    }

    [Fact]
    public async Task Every_write_leaves_exactly_one_event_in_journal_order()
    {
        var start = await Cursor();
        var funding = await pg.AccountAsync("funding", allowNegative: true);
        var a = await pg.AccountAsync();
        var shop = await pg.AccountAsync("shop");
        var fund = await Ledger.PostEntryAsync(Key(), "fund", [new(funding.Id, -1_000), new(a.Id, 1_000)]);
        var hold = await Ledger.AuthorizeAsync(Key(), a.Id, 300);
        var (_, capture) = await Ledger.CaptureAsync(hold.Hold.Id, Key(), shop.Id, 200);
        await Ledger.ReleaseAsync(hold.Hold.Id, Key());

        var events = await EventsMentioning(start, funding.Id, a.Id, shop.Id, fund.Id, hold.Hold.Id, capture.Id);

        Assert.Equal(
            ["account.created", "account.created", "account.created", "entry.posted", "hold.authorized", "entry.posted", "hold.released"],
            events.Select(e => e.Type));
        Assert.True(events.Select(e => e.Id).SequenceEqual(events.Select(e => e.Id).OrderBy(x => x)));
        Assert.Equal(hold.Hold.Id.ToString(), events[5].Payload.GetProperty("holdId").GetString());   // the capture names its hold
        Assert.Equal(2, events[3].Payload.GetProperty("postings").GetArrayLength());
    }

    [Fact]
    public async Task A_replayed_write_and_a_rejected_write_leave_no_event()
    {
        var start = await Cursor();
        var a = await pg.FundedAccountAsync(100); var b = await pg.AccountAsync();
        var key = Key();
        await Ledger.PostEntryAsync(key, "once", [new(a.Id, -50), new(b.Id, 50)]);
        await Ledger.PostEntryAsync(key, "once", [new(a.Id, -50), new(b.Id, 50)]);                       // replay
        await Assert.ThrowsAsync<InsufficientFundsException>(() =>
            Ledger.PostEntryAsync(Key(), "too much", [new(a.Id, -500), new(b.Id, 500)]));               // rolled back

        var posted = (await EventsMentioning(start, b.Id)).Where(e => e.Type == "entry.posted").ToList();
        Assert.Single(posted);
    }

    [Fact]
    public async Task Paging_by_cursor_returns_everything_exactly_once()
    {
        var start = await Cursor();
        var funding = await pg.AccountAsync("funding", allowNegative: true);
        var a = await pg.AccountAsync();
        for (var i = 0; i < 25; i++)
            await Ledger.PostEntryAsync(Key(), $"n{i}", [new(funding.Id, -1), new(a.Id, 1)]);

        var seen = new List<long>();
        var cursor = start;
        while (true)
        {
            var page = await Ledger.EventsAsync(cursor, limit: 7);
            if (page.Events.Count == 0) break;
            seen.AddRange(page.Events.Select(e => e.Id));
            cursor = page.Next;
        }

        var mine = (await EventsMentioning(start, a.Id)).Select(e => e.Id).ToList();
        Assert.Equal(26, mine.Count);                                        // a itself + 25 entries
        Assert.Equal(mine.Count, mine.Distinct().Count());
        Assert.True(mine.All(seen.Contains));
    }

    [Fact]
    public async Task A_row_from_a_transaction_still_in_progress_holds_back_everything_after_it()
    {
        // Transaction A takes an event id and does not commit. Transaction B commits with a later id.
        // A reader must not hand out B's row yet: advancing past A's id would lose A when it lands.
        var start = await Cursor();
        await using var slow = await pg.Db.OpenConnectionAsync();
        await using var tx = await slow.BeginTransactionAsync();
        var slowId = await slow.ExecuteScalarAsync<long>(
            "insert into events (type, payload) values ('test.slow', '{}') returning id", transaction: tx);

        var account = await Ledger.CreateAccountAsync("after-the-slow-one", "EUR");   // commits, id > slowId

        var before = await Ledger.EventsAsync(start, 1000);
        Assert.DoesNotContain(before.Events, e => e.Id >= slowId);                    // nothing past the open transaction

        await tx.CommitAsync();

        var after = await Ledger.EventsAsync(start, 1000);
        var ids = after.Events.Select(e => e.Id).ToList();
        Assert.Contains(slowId, ids);
        Assert.Contains(after.Events, e => e.Type == "account.created" && e.Payload.GetProperty("id").GetGuid() == account.Id);
        Assert.True(ids.SequenceEqual(ids.OrderBy(x => x)));
    }

    [Fact]
    public async Task Events_are_immutable_at_the_database_level()
    {
        await pg.AccountAsync();
        await using var conn = await pg.Db.OpenConnectionAsync();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync("delete from events where id = (select max(id) from events)"));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, ex.SqlState);
    }
}
