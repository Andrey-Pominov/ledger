using Ledger.Core;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ledger.Tests;

/// <summary>One real PostgreSQL per test run. Tests create their own accounts, so they never share state.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public NpgsqlDataSource Db { get; private set; } = null!;
    public LedgerService Ledger { get; private set; } = null!;
    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Db = NpgsqlDataSource.Create(ConnectionString);
        await Migrator.ApplyAsync(Db);
        Ledger = new LedgerService(Db);
    }

    public async Task DisposeAsync()
    {
        await Db.DisposeAsync();
        await _container.DisposeAsync();
    }

    // Small helpers so tests read as scenarios, not setup.
    public Task<Account> AccountAsync(string name = "acct", string currency = "EUR", bool allowNegative = false)
        => Ledger.CreateAccountAsync($"{name}-{Guid.NewGuid():N}"[..20], currency, allowNegative);

    /// <summary>Puts money on an account from a funding account that is allowed to go negative.</summary>
    public async Task<Account> FundedAccountAsync(long amount, string currency = "EUR")
    {
        var funding = await AccountAsync("funding", currency, allowNegative: true);
        var account = await AccountAsync("user", currency);
        await Ledger.PostEntryAsync(Guid.NewGuid().ToString(), "fund", [new Posting(funding.Id, -amount), new Posting(account.Id, amount)]);
        return account;
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
