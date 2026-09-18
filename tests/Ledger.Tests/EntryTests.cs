using Dapper;
using Ledger.Core;
using Npgsql;

namespace Ledger.Tests;

[Collection("postgres")]
public sealed class EntryTests(PostgresFixture pg)
{
    private LedgerService Ledger => pg.Ledger;
    private static string Key() => Guid.NewGuid().ToString();

    [Fact]
    public async Task Entry_must_balance()
    {
        var a = await pg.AccountAsync(); var b = await pg.AccountAsync();
        var ex = await Assert.ThrowsAsync<InvalidEntryException>(() =>
            Ledger.PostEntryAsync(Key(), "unbalanced", [new(a.Id, -100), new(b.Id, 99)]));
        Assert.Contains("sum to zero", ex.Message);
    }

    [Fact]
    public async Task Entry_needs_at_least_two_postings()
    {
        var a = await pg.AccountAsync();
        await Assert.ThrowsAsync<InvalidEntryException>(() => Ledger.PostEntryAsync(Key(), "one", [new(a.Id, 0)]));
        await Assert.ThrowsAsync<InvalidEntryException>(() => Ledger.PostEntryAsync(Key(), "zero", [new(a.Id, 0), new(a.Id, 0)]));
    }

    [Fact]
    public async Task Entry_cannot_mix_currencies()
    {
        var eur = await pg.AccountAsync(currency: "EUR", allowNegative: true);
        var usd = await pg.AccountAsync(currency: "USD");
        await Assert.ThrowsAsync<InvalidEntryException>(() =>
            Ledger.PostEntryAsync(Key(), "fx", [new(eur.Id, -100), new(usd.Id, 100)]));
    }

    [Fact]
    public async Task Balance_is_the_sum_of_postings_and_statement_runs_it()
    {
        var a = await pg.FundedAccountAsync(1_000);
        var b = await pg.AccountAsync();
        await Ledger.PostEntryAsync(Key(), "pay 300", [new(a.Id, -300), new(b.Id, 300)]);
        await Ledger.PostEntryAsync(Key(), "pay 50", [new(a.Id, -50), new(b.Id, 50)]);

        Assert.Equal(650, (await Ledger.GetAccountAsync(a.Id)).Balance);
        Assert.Equal(350, (await Ledger.GetAccountAsync(b.Id)).Balance);

        var statement = await Ledger.StatementAsync(a.Id);
        Assert.Equal([1_000, 700, 650], statement.Select(l => l.RunningBalance));
    }

    [Fact]
    public async Task Replaying_an_idempotency_key_returns_the_same_entry_and_writes_nothing()
    {
        var a = await pg.FundedAccountAsync(500); var b = await pg.AccountAsync();
        var key = Key();
        var first = await Ledger.PostEntryAsync(key, "once", [new(a.Id, -100), new(b.Id, 100)]);
        var second = await Ledger.PostEntryAsync(key, "once", [new(a.Id, -100), new(b.Id, 100)]);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(400, (await Ledger.GetAccountAsync(a.Id)).Balance);
    }

    [Fact]
    public async Task Reusing_a_key_with_a_different_body_is_a_conflict()
    {
        var a = await pg.FundedAccountAsync(500); var b = await pg.AccountAsync();
        var key = Key();
        await Ledger.PostEntryAsync(key, "once", [new(a.Id, -100), new(b.Id, 100)]);
        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            Ledger.PostEntryAsync(key, "once", [new(a.Id, -200), new(b.Id, 200)]));
        Assert.Equal(400, (await Ledger.GetAccountAsync(a.Id)).Balance);
    }

    [Fact]
    public async Task An_account_cannot_be_overdrawn_by_default()
    {
        var a = await pg.FundedAccountAsync(100); var b = await pg.AccountAsync();
        var ex = await Assert.ThrowsAsync<InsufficientFundsException>(() =>
            Ledger.PostEntryAsync(Key(), "too much", [new(a.Id, -101), new(b.Id, 101)]));
        Assert.Equal(100, ex.Available);
        Assert.Equal(101, ex.Requested);
        Assert.Equal(100, (await Ledger.GetAccountAsync(a.Id)).Balance);
    }

    [Fact]
    public async Task A_funding_account_may_go_negative()
    {
        var funding = await pg.AccountAsync("funding", allowNegative: true);
        var user = await pg.AccountAsync();
        await Ledger.PostEntryAsync(Key(), "fund", [new(funding.Id, -1_000), new(user.Id, 1_000)]);
        Assert.Equal(-1_000, (await Ledger.GetAccountAsync(funding.Id)).Balance);
    }

    [Fact]
    public async Task A_reversal_nets_to_zero_and_points_at_the_original()
    {
        var a = await pg.FundedAccountAsync(500); var b = await pg.AccountAsync();
        var original = await Ledger.PostEntryAsync(Key(), "pay", [new(a.Id, -200), new(b.Id, 200)]);
        var reversal = await Ledger.ReverseAsync(original.Id, Key());

        Assert.Equal(original.Id, reversal.Reverses);
        Assert.Equal(500, (await Ledger.GetAccountAsync(a.Id)).Balance);
        Assert.Equal(0, (await Ledger.GetAccountAsync(b.Id)).Balance);
    }

    [Fact]
    public async Task The_journal_is_immutable_at_the_database_level()
    {
        var a = await pg.FundedAccountAsync(100);
        await using var conn = await pg.Db.OpenConnectionAsync();

        var update = await Assert.ThrowsAsync<PostgresException>(() =>
            conn.ExecuteAsync("update postings set amount = amount + 1 where account_id = @id", new { id = a.Id }));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, update.SqlState);

        var delete = await Assert.ThrowsAsync<PostgresException>(() =>
            conn.ExecuteAsync("delete from entries where id in (select entry_id from postings where account_id = @id)", new { id = a.Id }));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, delete.SqlState);
    }

    [Fact]
    public async Task The_database_rejects_an_unbalanced_entry_even_if_the_application_is_bypassed()
    {
        var a = await pg.AccountAsync(allowNegative: true); var b = await pg.AccountAsync(allowNegative: true);
        await using var conn = await pg.Db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var id = Guid.NewGuid();
        await conn.ExecuteAsync("insert into entries (id, idempotency_key, request_hash, description) values (@id, @id, '\\x00', 'raw')", new { id }, tx);
        await conn.ExecuteAsync("insert into postings (entry_id, account_id, amount) values (@id, @a, -5), (@id, @b, 4)", new { id, a = a.Id, b = b.Id }, tx);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }
}
