using Ledger.Core;

namespace Ledger.Api;

public sealed record CreateAccountRequest(string Name, string Currency, bool AllowNegative = false);
public sealed record PostingRequest(Guid AccountId, long Amount);
public sealed record PostEntryRequest(string IdempotencyKey, string Description, IReadOnlyList<PostingRequest> Postings);
public sealed record ReverseRequest(string IdempotencyKey, string? Description = null);
public sealed record AuthorizeRequest(string IdempotencyKey, Guid AccountId, long Amount, int? TimeoutSeconds = null);
public sealed record CaptureRequest(string IdempotencyKey, Guid ToAccountId, long? Amount = null);
public sealed record ReleaseRequest(string IdempotencyKey, long? Amount = null);

public sealed record AccountResponse(Guid Id, string Name, string Currency, bool AllowNegative, long Balance, long Available, DateTime CreatedAt)
{
    public static AccountResponse From(AccountView v) =>
        new(v.Account.Id, v.Account.Name, v.Account.Currency, v.Account.AllowNegative, v.Balance, v.Available, v.Account.CreatedAt);
}

public sealed record CaptureResponse(HoldView Hold, Entry Entry);
public sealed record ErrorResponse(string Error, string Message);
