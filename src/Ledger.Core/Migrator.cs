using System.Reflection;
using Dapper;
using Npgsql;

namespace Ledger.Core;

/// <summary>
/// Applies db/migrations/*.sql in name order, once each. Deliberately small: a ledger's schema
/// should be readable as plain SQL, and the tool that applies it should not hide anything.
/// </summary>
public static class Migrator
{
    public static async Task ApplyAsync(NpgsqlDataSource db, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);

        // One migrator at a time per database. The lock comes first: CREATE TABLE IF NOT EXISTS
        // is not atomic under concurrent creation, so even the bookkeeping table needs it.
        await conn.ExecuteAsync("select pg_advisory_lock(hashtext('ledger.migrations'))");
        try
        {
            await conn.ExecuteAsync("create table if not exists schema_migrations (name text primary key, applied_at timestamptz not null default now())");
            var applied = (await conn.QueryAsync<string>("select name from schema_migrations")).ToHashSet();
            var asm = Assembly.GetExecutingAssembly();
            var names = asm.GetManifestResourceNames()
                .Where(n => n.StartsWith("migrations/", StringComparison.Ordinal))
                .OrderBy(n => n, StringComparer.Ordinal);

            foreach (var name in names)
            {
                if (applied.Contains(name)) continue;
                await using var stream = asm.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                var sql = await reader.ReadToEndAsync(ct);

                await using var tx = await conn.BeginTransactionAsync(ct);
                await conn.ExecuteAsync(sql, transaction: tx);
                await conn.ExecuteAsync("insert into schema_migrations (name) values (@name)", new { name }, tx);
                await tx.CommitAsync(ct);
            }
        }
        finally
        {
            await conn.ExecuteAsync("select pg_advisory_unlock(hashtext('ledger.migrations'))");
        }
    }
}
