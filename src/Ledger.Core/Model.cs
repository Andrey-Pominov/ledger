namespace Ledger.Core;

/// <summary>An account holds a balance in one currency. The balance is derived from postings, never stored.</summary>
public sealed record Account(Guid Id, string Name, string Currency, bool AllowNegative, DateTime CreatedAt);

/// <summary>Account plus the two numbers a caller usually wants.</summary>
/// <param name="Balance">Sum of all postings.</param>
/// <param name="Available">Balance minus pending holds — what can still be spent or reserved.</param>
public sealed record AccountView(Account Account, long Balance, long Available);

/// <summary>Signed movement on one account. Positive credits the account, negative debits it.</summary>
public sealed record Posting(Guid AccountId, long Amount);

/// <summary>An immutable, balanced group of postings.</summary>
public sealed record Entry(Guid Id, string IdempotencyKey, string Description, Guid? Reverses, DateTime CreatedAt, IReadOnlyList<Posting> Postings);

public enum HoldStatus { Open, Expired, Closed }

/// <summary>
/// A reservation against an account's available balance, written once and never updated.
/// What happened to it lives in the journal (captures) and in hold_releases (releases).
/// </summary>
public sealed record Hold(Guid Id, string IdempotencyKey, Guid AccountId, long Amount, DateTime CreatedAt, DateTime? ExpiresAt);

/// <summary>A hold with its state derived from the journal: what was captured, released, and what still reserves funds.</summary>
public sealed record HoldView(Hold Hold, long Captured, long Released, long Remaining, HoldStatus Status);

/// <summary>One line of an account statement.</summary>
public sealed record StatementLine(Guid EntryId, DateTime At, string Description, long Amount, long RunningBalance);

public abstract class LedgerException(string message) : Exception(message);

/// <summary>The request is malformed: unbalanced, single posting, mixed currencies, zero amount.</summary>
public sealed class InvalidEntryException(string message) : LedgerException(message);

/// <summary>The account would go below zero and is not allowed to.</summary>
public sealed class InsufficientFundsException(Guid accountId, long available, long requested)
    : LedgerException($"account {accountId} has {available} available, {requested} requested")
{
    public Guid AccountId { get; } = accountId;
    public long Available { get; } = available;
    public long Requested { get; } = requested;
}

/// <summary>An idempotency key was reused with a different request body.</summary>
public sealed class IdempotencyConflictException(string key)
    : LedgerException($"idempotency key '{key}' was already used with a different request");

public sealed class NotFoundException(string what, Guid id) : LedgerException($"{what} {id} not found");

/// <summary>The hold is closed or expired, or the amount exceeds what remains on it.</summary>
public sealed class InvalidHoldStateException(string message) : LedgerException(message);

/// <summary>One row of the outbox: what changed, when, and the record as it was written.</summary>
public sealed record LedgerEvent(long Id, string Type, DateTime OccurredAt, System.Text.Json.JsonElement Payload);

/// <summary>A page of events plus the cursor to ask for the next one.</summary>
public sealed record EventPage(IReadOnlyList<LedgerEvent> Events, long Next);
