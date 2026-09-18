using Ledger.Core;

namespace Ledger.Tests;

[Collection("postgres")]
public sealed class StatementTests(PostgresFixture pg)
{
    private LedgerService Ledger => pg.Ledger;
    private static string Key() => Guid.NewGuid().ToString();

    [Fact]
    public async Task Paging_returns_every_line_once_and_the_running_balance_carries_across_pages()
    {
        var funding = await pg.AccountAsync("funding", allowNegative: true);
        var a = await pg.AccountAsync();
        var amounts = Enumerable.Range(1, 23).Select(i => (long)i * 10).ToList();       // 10, 20, ..., 230
        foreach (var amt in amounts)
            await Ledger.PostEntryAsync(Key(), $"in {amt}", [new(funding.Id, -amt), new(a.Id, amt)]);

        var all = new List<StatementLine>();
        var cursor = 0L;
        var pages = 0;
        while (true)
        {
            var page = await Ledger.StatementAsync(a.Id, cursor, limit: 5);
            if (page.Lines.Count == 0) break;
            pages++;
            all.AddRange(page.Lines);
            cursor = page.Next;
        }

        Assert.Equal(5, pages);                                                            // 23 lines, 5 per page
        Assert.Equal(amounts, all.Select(l => l.Amount));
        Assert.Equal(all.Select(l => l.Id).OrderBy(x => x), all.Select(l => l.Id));
        Assert.Equal(all.Select(l => l.Id).Distinct().Count(), all.Count);

        // The running balance on every line equals the sum of amounts up to it — including across a page boundary.
        long running = 0;
        foreach (var line in all)
        {
            running += line.Amount;
            Assert.Equal(running, line.RunningBalance);
        }
        Assert.Equal(running, (await Ledger.GetAccountAsync(a.Id)).Balance);
    }

    [Fact]
    public async Task A_page_after_the_last_line_is_empty_and_keeps_the_cursor()
    {
        var a = await pg.FundedAccountAsync(100);
        var first = await Ledger.StatementAsync(a.Id);
        Assert.Single(first.Lines);

        var empty = await Ledger.StatementAsync(a.Id, first.Next);
        Assert.Empty(empty.Lines);
        Assert.Equal(first.Next, empty.Next);
    }

    [Fact]
    public async Task Limit_is_bounded()
    {
        var a = await pg.AccountAsync();
        await Assert.ThrowsAsync<InvalidEntryException>(() => Ledger.StatementAsync(a.Id, 0, limit: 0));
        await Assert.ThrowsAsync<InvalidEntryException>(() => Ledger.StatementAsync(a.Id, 0, limit: 1001));
        await Assert.ThrowsAsync<NotFoundException>(() => Ledger.StatementAsync(Guid.NewGuid()));
    }
}
