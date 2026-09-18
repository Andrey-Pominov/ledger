using System.Text.Json.Serialization;
using Ledger.Api;
using Ledger.Core;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Ledger")
    ?? throw new InvalidOperationException("ConnectionStrings:Ledger is not configured");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<LedgerService>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

await Migrator.ApplyAsync(app.Services.GetRequiredService<NpgsqlDataSource>());

// Domain errors become HTTP status codes here and nowhere else.
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (LedgerException e)
    {
        var (status, code) = e switch
        {
            NotFoundException             => (StatusCodes.Status404NotFound, "not_found"),
            InvalidEntryException         => (StatusCodes.Status400BadRequest, "invalid_entry"),
            InsufficientFundsException    => (StatusCodes.Status422UnprocessableEntity, "insufficient_funds"),
            IdempotencyConflictException  => (StatusCodes.Status409Conflict, "idempotency_conflict"),
            InvalidHoldStateException     => (StatusCodes.Status409Conflict, "invalid_hold_state"),
            _                             => (StatusCodes.Status500InternalServerError, "ledger_error"),
        };
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new ErrorResponse(code, e.Message));
    }
});

var accounts = app.MapGroup("/accounts");
accounts.MapPost("/", async (CreateAccountRequest req, LedgerService ledger) =>
{
    var account = await ledger.CreateAccountAsync(req.Name, req.Currency, req.AllowNegative);
    return Results.Created($"/accounts/{account.Id}", AccountResponse.From(await ledger.GetAccountAsync(account.Id)));
});
accounts.MapGet("/{id:guid}", async (Guid id, LedgerService ledger) => AccountResponse.From(await ledger.GetAccountAsync(id)));
accounts.MapGet("/{id:guid}/statement", async (Guid id, long? after, int? limit, LedgerService ledger) => await ledger.StatementAsync(id, after ?? 0, limit ?? 100));

var entries = app.MapGroup("/entries");
entries.MapPost("/", async (PostEntryRequest req, LedgerService ledger) =>
{
    var postings = req.Postings.Select(p => new Posting(p.AccountId, p.Amount)).ToList();
    var entry = await ledger.PostEntryAsync(req.IdempotencyKey, req.Description, postings);
    return Results.Created($"/entries/{entry.Id}", entry);
});
entries.MapGet("/{id:guid}", async (Guid id, LedgerService ledger) => await ledger.GetEntryAsync(id));
entries.MapPost("/{id:guid}/reverse", async (Guid id, ReverseRequest req, LedgerService ledger) =>
{
    var entry = await ledger.ReverseAsync(id, req.IdempotencyKey, req.Description);
    return Results.Created($"/entries/{entry.Id}", entry);
});

var holds = app.MapGroup("/holds");
holds.MapPost("/", async (AuthorizeRequest req, LedgerService ledger) =>
{
    var timeout = req.TimeoutSeconds is { } secs ? TimeSpan.FromSeconds(secs) : (TimeSpan?)null;
    var hold = await ledger.AuthorizeAsync(req.IdempotencyKey, req.AccountId, req.Amount, timeout);
    return Results.Created($"/holds/{hold.Hold.Id}", hold);
});
holds.MapGet("/{id:guid}", async (Guid id, LedgerService ledger) => await ledger.GetHoldAsync(id));
holds.MapPost("/{id:guid}/capture", async (Guid id, CaptureRequest req, LedgerService ledger) =>
{
    var (hold, entry) = await ledger.CaptureAsync(id, req.IdempotencyKey, req.ToAccountId, req.Amount);
    return new CaptureResponse(hold, entry);
});
holds.MapPost("/{id:guid}/release", async (Guid id, ReleaseRequest req, LedgerService ledger) => await ledger.ReleaseAsync(id, req.IdempotencyKey, req.Amount));

app.MapGet("/events", async (long? after, int? limit, LedgerService ledger) => await ledger.EventsAsync(after ?? 0, limit ?? 100));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

public partial class Program;
