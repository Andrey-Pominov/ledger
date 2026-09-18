using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace Ledger.Core;

/// <summary>
/// All ledger operations. Each public method is one database transaction and is safe to retry:
/// the idempotency key decides whether a retry is a no-op or a conflict.
///
/// Concurrency model: every write locks the accounts it touches with SELECT ... FOR UPDATE, in
/// ascending id order so two writers touching the same pair cannot deadlock. The balance check
/// happens under that lock, so two transfers cannot both pass a check that only one should.
/// </summary>
public sealed class LedgerService(NpgsqlDataSource db)
{
    // ---------- accounts ----------

    public async Task<Account> CreateAccountAsync(string name, string currency, bool allowNegative = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidEntryException("account name is required");
        if (currency is not { Length: 3 }) throw new InvalidEntryException("currency must be a 3-letter code");

        var account = new Account(Guid.NewGuid(), name.Trim(), currency.ToUpperInvariant(), allowNegative, DateTime.UtcNow);
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "insert into accounts (id, name, currency, allow_negative, created_at) values (@Id, @Name, @Currency, @AllowNegative, @CreatedAt)",
            account);
        return account;
    }

    public async Task<AccountView> GetAccountAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var account = await conn.QuerySingleOrDefaultAsync<Account>(
            "select id, name, currency, allow_negative as AllowNegative, created_at as CreatedAt from accounts where id = @id", new { id })
            ?? throw new NotFoundException("account", id);
        var (balance, held) = await BalanceAndHeldAsync(conn, null, id);
        return new AccountView(account, balance, balance - held);
    }

    public async Task<IReadOnlyList<StatementLine>> StatementAsync(Guid accountId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        _ = await conn.QuerySingleOrDefaultAsync<Guid?>("select id from accounts where id = @accountId", new { accountId })
            ?? throw new NotFoundException("account", accountId);
        var rows = await conn.QueryAsync<StatementLine>("""
            select e.id as EntryId, e.created_at as At, e.description as Description, p.amount as Amount,
                   (sum(p.amount) over (order by p.id))::bigint as RunningBalance
            from postings p join entries e on e.id = p.entry_id
            where p.account_id = @accountId
            order by p.id
            """, new { accountId });
        return rows.ToList();
    }

    // ---------- entries ----------

    public async Task<Entry> PostEntryAsync(string idempotencyKey, string description, IReadOnlyList<Posting> postings, CancellationToken ct = default)
    {
        Validate(postings);
        var hash = Hash(new { description, postings = postings.OrderBy(p => p.AccountId).ThenBy(p => p.Amount) });

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        if (await FindEntryByKeyAsync(conn, tx, idempotencyKey, hash) is { } existing) return existing;

        var entry = new Entry(Guid.NewGuid(), idempotencyKey, description, null, DateTime.UtcNow, postings);
        try
        {
            await WriteEntryAsync(conn, tx, entry, hash);
            await tx.CommitAsync(ct);
            return entry;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Two requests raced on the same key and the other one won. Answer with its result
            // (or a conflict, if it carried a different body).
            await tx.RollbackAsync(ct);
            return await FindEntryByKeyAsync(conn, null, idempotencyKey, hash)
                   ?? throw new IdempotencyConflictException(idempotencyKey);
        }
    }

    /// <summary>Corrections never edit history: a reversal is a new entry with every posting negated.</summary>
    public async Task<Entry> ReverseAsync(Guid entryId, string idempotencyKey, string? description = null, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var original = await LoadEntryAsync(conn, tx, entryId) ?? throw new NotFoundException("entry", entryId);
        var postings = original.Postings.Select(p => p with { Amount = -p.Amount }).ToList();
        var desc = description ?? $"reversal of {entryId}";
        var hash = Hash(new { reverses = entryId, desc });

        if (await FindEntryByKeyAsync(conn, tx, idempotencyKey, hash) is { } existing) return existing;

        var entry = new Entry(Guid.NewGuid(), idempotencyKey, desc, entryId, DateTime.UtcNow, postings);
        await WriteEntryAsync(conn, tx, entry, hash);
        await tx.CommitAsync(ct);
        return entry;
    }

    // ---------- holds ----------

    /// <summary>Reserve funds. Nothing moves; the available balance drops until capture or release.</summary>
    public async Task<Hold> AuthorizeAsync(string idempotencyKey, Guid accountId, long amount, CancellationToken ct = default)
    {
        if (amount <= 0) throw new InvalidEntryException("hold amount must be positive");
        var hash = Hash(new { accountId, amount });

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        if (await FindHoldByKeyAsync(conn, tx, idempotencyKey, hash) is { } existing) return existing;

        var account = (await LockAccountsAsync(conn, tx, [accountId])).Single();
        if (!account.AllowNegative)
        {
            var (balance, held) = await BalanceAndHeldAsync(conn, tx, accountId);
            if (balance - held < amount) throw new InsufficientFundsException(accountId, balance - held, amount);
        }

        var hold = new Hold(Guid.NewGuid(), idempotencyKey, accountId, amount, HoldStatus.Pending, null, DateTime.UtcNow, null);
        try
        {
            await conn.ExecuteAsync("""
                insert into holds (id, idempotency_key, request_hash, account_id, amount, status, created_at)
                values (@Id, @IdempotencyKey, @hash, @AccountId, @Amount, 'pending', @CreatedAt)
                """, new { hold.Id, hold.IdempotencyKey, hash, hold.AccountId, hold.Amount, hold.CreatedAt }, tx);
            await tx.CommitAsync(ct);
            return hold;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await tx.RollbackAsync(ct);
            var row = await conn.QuerySingleOrDefaultAsync<(Guid id, byte[] request_hash)>(
                "select id, request_hash from holds where idempotency_key = @key", new { key = idempotencyKey });
            if (row.id == default || !row.request_hash.AsSpan().SequenceEqual(hash)) throw new IdempotencyConflictException(idempotencyKey);
            return (await QueryHoldAsync(conn, null, row.id))!;
        }
    }

    /// <summary>
    /// Settle a hold: move <paramref name="amount"/> (at most the held amount) to <paramref name="toAccountId"/>.
    /// Any remainder is released. Capturing an already-captured hold again is a no-op that returns the same entry.
    /// </summary>
    public async Task<(Hold Hold, Entry Entry)> CaptureAsync(Guid holdId, Guid toAccountId, long? amount = null, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var hold = await LockHoldAsync(conn, tx, holdId);
        if (hold.Status == HoldStatus.Captured)
        {
            var already = await LoadEntryAsync(conn, tx, hold.CapturedEntry!.Value);
            return (hold, already!);
        }
        if (hold.Status != HoldStatus.Pending) throw new InvalidHoldStateException($"hold {holdId} is {hold.Status}, cannot capture");

        var captureAmount = amount ?? hold.Amount;
        if (captureAmount <= 0 || captureAmount > hold.Amount)
            throw new InvalidHoldStateException($"capture of {captureAmount} is outside (0, {hold.Amount}]");

        // The hold already reserved the funds, so the balance check for the source is against
        // balance minus *other* holds — this hold is the one being consumed.
        var accounts = await LockAccountsAsync(conn, tx, [hold.AccountId, toAccountId]);
        var source = accounts.Single(a => a.Id == hold.AccountId);
        var dest = accounts.Single(a => a.Id == toAccountId);
        if (source.Currency != dest.Currency) throw new InvalidEntryException("capture across currencies is not supported");
        if (!source.AllowNegative)
        {
            var (balance, held) = await BalanceAndHeldAsync(conn, tx, hold.AccountId);
            var availableIncludingThisHold = balance - held + hold.Amount;
            if (availableIncludingThisHold < captureAmount)
                throw new InsufficientFundsException(hold.AccountId, availableIncludingThisHold, captureAmount);
        }

        var entry = new Entry(Guid.NewGuid(), $"capture:{holdId}", $"capture of hold {holdId}", null, DateTime.UtcNow,
            [new Posting(hold.AccountId, -captureAmount), new Posting(toAccountId, captureAmount)]);
        await WriteEntryAsync(conn, tx, entry, Hash(new { holdId, toAccountId, captureAmount }), skipBalanceCheck: true);

        await conn.ExecuteAsync(
            "update holds set status = 'captured', captured_entry = @entryId, settled_at = now() where id = @holdId",
            new { entryId = entry.Id, holdId }, tx);
        await tx.CommitAsync(ct);
        return (hold with { Status = HoldStatus.Captured, CapturedEntry = entry.Id, SettledAt = DateTime.UtcNow }, entry);
    }

    /// <summary>Drop a hold without moving money. Releasing twice is a no-op.</summary>
    public async Task<Hold> ReleaseAsync(Guid holdId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var hold = await LockHoldAsync(conn, tx, holdId);
        if (hold.Status == HoldStatus.Released) return hold;
        if (hold.Status == HoldStatus.Captured) throw new InvalidHoldStateException($"hold {holdId} is captured, cannot release");

        await conn.ExecuteAsync("update holds set status = 'released', settled_at = now() where id = @holdId", new { holdId }, tx);
        await tx.CommitAsync(ct);
        return hold with { Status = HoldStatus.Released, SettledAt = DateTime.UtcNow };
    }

    public async Task<Hold> GetHoldAsync(Guid holdId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await QueryHoldAsync(conn, null, holdId) ?? throw new NotFoundException("hold", holdId);
    }

    public async Task<Entry> GetEntryAsync(Guid entryId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await LoadEntryAsync(conn, null, entryId) ?? throw new NotFoundException("entry", entryId);
    }

    // ---------- internals ----------

    private static void Validate(IReadOnlyList<Posting> postings)
    {
        if (postings.Count < 2) throw new InvalidEntryException("an entry needs at least two postings");
        if (postings.Any(p => p.Amount == 0)) throw new InvalidEntryException("a posting cannot be zero");
        if (postings.Sum(p => p.Amount) != 0) throw new InvalidEntryException("postings must sum to zero");
    }

    /// <summary>Writes the entry rows after locking accounts and checking that no account is overdrawn.</summary>
    private static async Task WriteEntryAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Entry entry, byte[] hash, bool skipBalanceCheck = false)
    {
        var accountIds = entry.Postings.Select(p => p.AccountId).Distinct().ToArray();
        var accounts = await LockAccountsAsync(conn, tx, accountIds);
        if (accounts.Select(a => a.Currency).Distinct().Count() > 1)
            throw new InvalidEntryException("all postings in an entry must be in the same currency");

        if (!skipBalanceCheck)
        {
            foreach (var group in entry.Postings.GroupBy(p => p.AccountId))
            {
                var delta = group.Sum(p => p.Amount);
                if (delta >= 0) continue;
                var account = accounts.Single(a => a.Id == group.Key);
                if (account.AllowNegative) continue;
                var (balance, held) = await BalanceAndHeldAsync(conn, tx, account.Id);
                if (balance - held + delta < 0) throw new InsufficientFundsException(account.Id, balance - held, -delta);
            }
        }

        await conn.ExecuteAsync("""
            insert into entries (id, idempotency_key, request_hash, description, reverses, created_at)
            values (@Id, @IdempotencyKey, @hash, @Description, @Reverses, @CreatedAt)
            """, new { entry.Id, entry.IdempotencyKey, hash, entry.Description, entry.Reverses, entry.CreatedAt }, tx);
        await conn.ExecuteAsync(
            "insert into postings (entry_id, account_id, amount) values (@EntryId, @AccountId, @Amount)",
            entry.Postings.Select(p => new { EntryId = entry.Id, p.AccountId, p.Amount }), tx);
    }

    private static async Task<IReadOnlyList<Account>> LockAccountsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid[] ids)
    {
        var rows = (await conn.QueryAsync<Account>(
            "select id, name, currency, allow_negative as AllowNegative, created_at as CreatedAt from accounts where id = any(@ids) order by id for update",
            new { ids }, tx)).ToList();
        var missing = ids.Except(rows.Select(r => r.Id)).FirstOrDefault();
        if (missing != default) throw new NotFoundException("account", missing);
        return rows;
    }

    private static async Task<(long Balance, long Held)> BalanceAndHeldAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid accountId)
    {
        var row = await conn.QuerySingleAsync<(long balance, long held)>("""
            select (select coalesce(sum(amount), 0) from postings where account_id = @accountId)::bigint,
                   (select coalesce(sum(amount), 0) from holds where account_id = @accountId and status = 'pending')::bigint
            """, new { accountId }, tx);
        return row;
    }

    private static async Task<Entry?> FindEntryByKeyAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string key, byte[] hash)
    {
        var row = await conn.QuerySingleOrDefaultAsync<(Guid id, byte[] request_hash)>(
            "select id, request_hash from entries where idempotency_key = @key", new { key }, tx);
        if (row.id == default) return null;
        if (!row.request_hash.AsSpan().SequenceEqual(hash)) throw new IdempotencyConflictException(key);
        return await LoadEntryAsync(conn, tx, row.id);
    }

    private static async Task<Entry?> LoadEntryAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id)
    {
        var head = await conn.QuerySingleOrDefaultAsync<(Guid id, string key, string description, Guid? reverses, DateTime created_at)>(
            "select id, idempotency_key, description, reverses, created_at from entries where id = @id", new { id }, tx);
        if (head.id == default) return null;
        var postings = (await conn.QueryAsync<Posting>(
            "select account_id as AccountId, amount as Amount from postings where entry_id = @id order by id", new { id }, tx)).ToList();
        return new Entry(head.id, head.key, head.description, head.reverses, head.created_at, postings);
    }

    private static async Task<Hold?> FindHoldByKeyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string key, byte[] hash)
    {
        var row = await conn.QuerySingleOrDefaultAsync<(Guid id, byte[] request_hash)>(
            "select id, request_hash from holds where idempotency_key = @key", new { key }, tx);
        if (row.id == default) return null;
        if (!row.request_hash.AsSpan().SequenceEqual(hash)) throw new IdempotencyConflictException(key);
        return await QueryHoldAsync(conn, tx, row.id);
    }

    private static async Task<Hold> LockHoldAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid holdId)
        => await QueryHoldAsync(conn, tx, holdId, forUpdate: true) ?? throw new NotFoundException("hold", holdId);

    private static async Task<Hold?> QueryHoldAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid holdId, bool forUpdate = false)
    {
        var row = await conn.QuerySingleOrDefaultAsync<(Guid id, string key, Guid account_id, long amount, string status, Guid? captured_entry, DateTime created_at, DateTime? settled_at)>(
            $"select id, idempotency_key, account_id, amount, status::text, captured_entry, created_at, settled_at from holds where id = @holdId{(forUpdate ? " for update" : "")}",
            new { holdId }, tx);
        if (row.id == default) return null;
        return new Hold(row.id, row.key, row.account_id, row.amount, Enum.Parse<HoldStatus>(row.status, ignoreCase: true), row.captured_entry, row.created_at, row.settled_at);
    }

    private static byte[] Hash(object request)
        => SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request)));
}
