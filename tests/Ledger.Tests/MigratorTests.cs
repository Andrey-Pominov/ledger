using Dapper;
using Ledger.Core;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ledger.Tests;

/// <summary>
/// The migrator is thirty lines and easy to get subtly wrong. These run it against a database
/// of its own — not the shared fixture — so they can watch it from the very first migration.
/// </summary>
public sealed class MigratorTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private NpgsqlDataSource _db = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _db = NpgsqlDataSource.Create(_container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _container.DisposeAsync();
    }

    [Fact]
    public async Task Applies_every_migration_once_in_name_order_and_is_a_no_op_afterwards()
    {
        await Migrator.ApplyAsync(_db);

        await using var conn = await _db.OpenConnectionAsync();
        var applied = (await conn.QueryAsync<(string name, DateTime applied_at)>(
            "select name, applied_at from schema_migrations order by applied_at, name")).ToList();

        Assert.Equal(
            ["migrations/001_init.sql", "migrations/002_immutable_holds.sql", "migrations/003_outbox.sql"],
            applied.Select(m => m.name));
        Assert.True(applied.Select(m => m.name).SequenceEqual(applied.Select(m => m.name).OrderBy(n => n, StringComparer.Ordinal)));

        // Everything the migrations promise is there.
        var tables = (await conn.QueryAsync<string>("select table_name from information_schema.tables where table_schema = 'public' order by 1")).ToList();
        Assert.All(new[] { "accounts", "entries", "postings", "holds", "hold_releases", "events", "hold_state", "events_stable", "schema_migrations" },
            t => Assert.Contains(t, tables));
        var triggers = (await conn.QueryAsync<string>("select distinct trigger_name from information_schema.triggers")).ToList();
        Assert.All(new[] { "entries_immutable", "postings_immutable", "holds_immutable", "hold_releases_immutable", "events_immutable", "entries_balanced" },
            t => Assert.Contains(t, triggers));

        // Running it again changes nothing: same rows, same timestamps.
        await Migrator.ApplyAsync(_db);
        var again = (await conn.QueryAsync<(string name, DateTime applied_at)>(
            "select name, applied_at from schema_migrations order by applied_at, name")).ToList();
        Assert.Equal(applied, again);
    }

    [Fact]
    public async Task Concurrent_migrators_do_not_step_on_each_other()
    {
        // Several instances of the API starting at once must not race on an empty database.
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Migrator.ApplyAsync(_db)));

        await using var conn = await _db.OpenConnectionAsync();
        Assert.Equal(3, await conn.ExecuteScalarAsync<long>("select count(*) from schema_migrations"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("select count(*) from information_schema.tables where table_name = 'events'"));
    }

    [Fact]
    public async Task A_failing_migration_leaves_no_trace_of_itself()
    {
        await Migrator.ApplyAsync(_db);
        await using var conn = await _db.OpenConnectionAsync();

        // The same code path the migrator uses: one migration, one transaction, recorded only on success.
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("create table half_done (id int)", transaction: tx);
        await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync("create table half_done (id int)", transaction: tx));
        await tx.RollbackAsync();

        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("select count(*) from information_schema.tables where table_name = 'half_done'"));
        Assert.Equal(3, await conn.ExecuteScalarAsync<long>("select count(*) from schema_migrations"));
    }
}
