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

    /// <summary>
    /// Reserve funds. Nothing moves; the available balance drops until the reservation is
    /// captured, released, or — if <paramref name="timeout"/> is given — expires.
    /// </summary>
    public async Task<HoldView> AuthorizeAsync(string idempotencyKey, Guid accountId, long amount, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (amount <= 0) throw new InvalidEntryException("hold amount must be positive");
        if (timeout is { } t && t <= TimeSpan.Zero) throw new InvalidEntryException("hold timeout must be positive");
        var hash = Hash(new { accountId, amount, timeout });

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        if (await FindHoldByKeyAsync(conn, tx, idempotencyKey, hash) is { } existing) return existing;

        var account = (await LockAccountsAsync(conn, tx, [accountId])).Single();
        if (!account.AllowNegative)
        {
            var (balance, held) = await BalanceAndHeldAsync(conn, tx, accountId);
            if (balance - held < amount) throw new InsufficientFundsException(accountId, balance - held, amount);
        }

        var now = DateTime.UtcNow;
        var hold = new Hold(Guid.NewGuid(), idempotencyKey, accountId, amount, now, timeout is { } to ? now + to : null);
        try
        {
            await conn.ExecuteAsync("""
                insert into holds (id, idempotency_key, request_hash, account_id, amount, created_at, expires_at)
                values (@Id, @IdempotencyKey, @hash, @AccountId, @Amount, @CreatedAt, @ExpiresAt)
                """, new { hold.Id, hold.IdempotencyKey, hash, hold.AccountId, hold.Amount, hold.CreatedAt, hold.ExpiresAt }, tx);
            await tx.CommitAsync(ct);
            return new HoldView(hold, 0, 0, amount, HoldStatus.Open);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await tx.RollbackAsync(ct);
            return await FindHoldByKeyAsync(conn, null, idempotencyKey, hash)
                   ?? throw new IdempotencyConflictException(idempotencyKey);
        }
    }

    /// <summary>
    /// Settle part of a hold: move <paramref name="amount"/> (default: everything remaining) to
    /// <paramref name="toAccountId"/>. A hold may be captured several times — a card authorization
    /// is often cleared in more than one message — as long as the captures stay within the amount.
    /// </summary>
    public async Task<(HoldView Hold, Entry Entry)> CaptureAsync(Guid holdId, string idempotencyKey, Guid toAccountId, long? amount = null, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var hold = await LockHoldAsync(conn, tx, holdId);
        // Hash the request as the caller sent it. Resolving "everything remaining" first would
        // make a replay hash differently once the first call has changed what remains.
        var hash = Hash(new { holdId, toAccountId, amount });
        if (await FindEntryByKeyAsync(conn, tx, idempotencyKey, hash) is { } existing)
            return (hold, existing);
        var captureAmount = amount ?? hold.Remaining;

        if (hold.Status != HoldStatus.Open) throw new InvalidHoldStateException($"hold {holdId} is {hold.Status.ToString().ToLowerInvariant()}, cannot capture");
        if (captureAmount <= 0 || captureAmount > hold.Remaining)
            throw new InvalidHoldStateException($"capture of {captureAmount} is outside (0, {hold.Remaining}] remaining on hold {holdId}");

        var accounts = await LockAccountsAsync(conn, tx, [hold.Hold.AccountId, toAccountId]);
        var source = accounts.Single(a => a.Id == hold.Hold.AccountId);
        var dest = accounts.Single(a => a.Id == toAccountId);
        if (source.Currency != dest.Currency) throw new InvalidEntryException("capture across currencies is not supported");

        // The reservation already covers this capture, so the source check is against
        // balance minus *other* reservations. It cannot fail while the invariants hold;
        // it stays here as a backstop.
        if (!source.AllowNegative)
        {
            var (balance, held) = await BalanceAndHeldAsync(conn, tx, source.Id);
            var coverage = balance - held + hold.Remaining;
            if (coverage < captureAmount) throw new InsufficientFundsException(source.Id, coverage, captureAmount);
        }

        var entry = new Entry(Guid.NewGuid(), idempotencyKey, $"capture of hold {holdId}", null, DateTime.UtcNow,
            [new Posting(source.Id, -captureAmount), new Posting(toAccountId, captureAmount)]);
        await WriteEntryAsync(conn, tx, entry, hash, skipBalanceCheck: true, holdId: holdId);
        await tx.CommitAsync(ct);

        return ((await QueryHoldAsync(conn, null, holdId))!, entry);
    }

    /// <summary>
    /// Give back part of a hold (default: everything remaining) without moving money.
    /// Releasing a hold that has nothing left is a no-op.
    /// </summary>
    public async Task<HoldView> ReleaseAsync(Guid holdId, string idempotencyKey, long? amount = null, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var hold = await LockHoldAsync(conn, tx, holdId);
        var hash = Hash(new { holdId, amount });
        var releaseAmount = amount ?? hold.Remaining;

        var prior = await conn.QuerySingleOrDefaultAsync<(long id, byte[] request_hash)>(
            "select id, request_hash from hold_releases where idempotency_key = @key", new { key = idempotencyKey }, tx);
        if (prior.id != 0)
        {
            if (!prior.request_hash.AsSpan().SequenceEqual(hash)) throw new IdempotencyConflictException(idempotencyKey);
            return hold;
        }

        if (releaseAmount == 0 && amount is null) return hold;   // nothing left to release
        if (hold.Status == HoldStatus.Closed) throw new InvalidHoldStateException($"hold {holdId} is closed, nothing to release");
        if (releaseAmount <= 0 || releaseAmount > hold.Remaining)
            throw new InvalidHoldStateException($"release of {releaseAmount} is outside (0, {hold.Remaining}] remaining on hold {holdId}");

        await conn.ExecuteAsync(
            "insert into hold_releases (hold_id, idempotency_key, request_hash, amount) values (@holdId, @idempotencyKey, @hash, @releaseAmount)",
            new { holdId, idempotencyKey, hash, releaseAmount }, tx);
        await tx.CommitAsync(ct);
        return (await QueryHoldAsync(conn, null, holdId))!;
    }

    public async Task<HoldView> GetHoldAsync(Guid holdId, CancellationToken ct = default)
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
    private static async Task WriteEntryAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Entry entry, byte[] hash, bool skipBalanceCheck = false, Guid? holdId = null)
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
            insert into entries (id, idempotency_key, request_hash, description, reverses, hold_id, created_at)
            values (@Id, @IdempotencyKey, @hash, @Description, @Reverses, @holdId, @CreatedAt)
            """, new { entry.Id, entry.IdempotencyKey, hash, entry.Description, entry.Reverses, holdId, entry.CreatedAt }, tx);
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

    /// <summary>Balance is the sum of postings; held is the sum of what still remains on open holds.</summary>
    private static async Task<(long Balance, long Held)> BalanceAndHeldAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid accountId)
    {
        var row = await conn.QuerySingleAsync<(long balance, long held)>("""
            select (select coalesce(sum(amount), 0) from postings where account_id = @accountId)::bigint,
                   (select coalesce(sum(remaining), 0) from hold_state where account_id = @accountId and status = 'open')::bigint
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

    private static async Task<HoldView?> FindHoldByKeyAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string key, byte[] hash)
    {
        var row = await conn.QuerySingleOrDefaultAsync<(Guid id, byte[] request_hash)>(
            "select id, request_hash from holds where idempotency_key = @key", new { key }, tx);
        if (row.id == default) return null;
        if (!row.request_hash.AsSpan().SequenceEqual(hash)) throw new IdempotencyConflictException(key);
        return await QueryHoldAsync(conn, tx, row.id);
    }

    /// <summary>Locks the (immutable) hold row so concurrent captures and releases of one hold serialize.</summary>
    private static async Task<HoldView> LockHoldAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid holdId)
    {
        _ = await conn.QuerySingleOrDefaultAsync<Guid?>("select id from holds where id = @holdId for update", new { holdId }, tx)
            ?? throw new NotFoundException("hold", holdId);
        return (await QueryHoldAsync(conn, tx, holdId))!;
    }

    private static async Task<HoldView?> QueryHoldAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid holdId)
    {
        var r = await conn.QuerySingleOrDefaultAsync<(Guid id, string key, Guid account_id, long amount, DateTime created_at, DateTime? expires_at, long captured, long released, long remaining, string status)>(
            "select id, idempotency_key, account_id, amount, created_at, expires_at, captured, released, remaining, status from hold_state where id = @holdId",
            new { holdId }, tx);
        if (r.id == default) return null;
        var hold = new Hold(r.id, r.key, r.account_id, r.amount, r.created_at, r.expires_at);
        return new HoldView(hold, r.captured, r.released, r.remaining, Enum.Parse<HoldStatus>(r.status, ignoreCase: true));
    }

    private static byte[] Hash(object request)
        => SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request)));
}
