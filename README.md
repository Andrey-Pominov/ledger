# ledger

A double-entry ledger with holds and idempotent writes. .NET 10, PostgreSQL, plain SQL.

It is small on purpose. The point is not the feature list — it is that every rule about money is enforced in one place, tested against a real database, and impossible to bypass from the application side.

## What it guarantees

- **Every entry balances.** A journal entry is a set of signed postings whose sum is zero. The service checks this; a deferred constraint trigger in PostgreSQL checks it again at commit, so a bug in the application cannot write an unbalanced entry.
- **Balances are never stored.** An account's balance is `SUM(postings.amount)`. There is no column that can drift from the journal.
- **The journal is append-only.** `UPDATE` and `DELETE` on `entries` and `postings` raise at the database level. Corrections are reversing entries that point at what they reverse.
- **No account is overdrawn unless it is allowed to be.** The check runs under a row lock on the account, so two concurrent transfers cannot both pass a check that only one of them should.
- **Every write is idempotent.** Each entry and hold carries a caller-supplied key. Replaying a request returns the original result and writes nothing; reusing a key with a different body is a `409`.
- **Holds reserve without moving.** `authorize` lowers the available balance; `capture` moves the money (partial capture releases the rest); `release` gives it back. Capture and release are themselves idempotent. This is a card authorization → clearing cycle without the card.

The tests in [`tests/Ledger.Tests`](tests/Ledger.Tests) exercise each of these against PostgreSQL — including 40 parallel transfers against a balance that only covers 10 of them, and 16 parallel requests sharing one idempotency key.

## Run it

```bash
docker compose up --build           # API on :8088, PostgreSQL on :5433
curl localhost:8088/health
```

Or without Docker for the API — you still need PostgreSQL on `localhost:5433` (or change `ConnectionStrings:Ledger`):

```bash
dotnet run --project src/Ledger.Api
```

Tests start their own PostgreSQL through Testcontainers; Docker must be running.

```bash
dotnet test
```

## A walk through the API

```bash
# a funding account that may go negative, and a customer
FUND=$(curl -s -X POST localhost:8088/accounts -H 'content-type: application/json' \
  -d '{"name":"funding","currency":"EUR","allowNegative":true}' | jq -r .id)
ALICE=$(curl -s -X POST localhost:8088/accounts -H 'content-type: application/json' \
  -d '{"name":"alice","currency":"EUR"}' | jq -r .id)

# 50.00 EUR onto alice — amounts are integers in minor units
curl -s -X POST localhost:8088/entries -H 'content-type: application/json' -d "{
  \"idempotencyKey\": \"fund-1\", \"description\": \"top up\",
  \"postings\": [ {\"accountId\": \"$FUND\", \"amount\": -5000}, {\"accountId\": \"$ALICE\", \"amount\": 5000} ]
}"

# send it again: same entry back, nothing written
# send it again with a different amount: 409 idempotency_conflict

# reserve 12.00 — balance stays 50.00, available drops to 38.00
HOLD=$(curl -s -X POST localhost:8088/holds -H 'content-type: application/json' \
  -d "{\"idempotencyKey\":\"auth-1\",\"accountId\":\"$ALICE\",\"amount\":1200}" | jq -r .id)
curl -s localhost:8088/accounts/$ALICE

# try to spend 40.00 while 12.00 is held: 422 insufficient_funds

# settle 10.00 of the hold to a shop; the other 2.00 is released
curl -s -X POST localhost:8088/holds/$HOLD/capture -H 'content-type: application/json' \
  -d "{\"toAccountId\":\"$SHOP\",\"amount\":1000}"

curl -s localhost:8088/accounts/$ALICE/statement
```

| Method | Path | |
|---|---|---|
| `POST` | `/accounts` | create; `allowNegative` marks funding / clearing accounts |
| `GET` | `/accounts/{id}` | balance and available |
| `GET` | `/accounts/{id}/statement` | postings with a running balance |
| `POST` | `/entries` | post a balanced entry (idempotent) |
| `GET` | `/entries/{id}` | |
| `POST` | `/entries/{id}/reverse` | new entry with every posting negated (idempotent) |
| `POST` | `/holds` | authorize (idempotent) |
| `GET` | `/holds/{id}` | |
| `POST` | `/holds/{id}/capture` | move up to the held amount, release the rest |
| `POST` | `/holds/{id}/release` | |

Errors: `400 invalid_entry`, `404 not_found`, `409 idempotency_conflict`, `409 invalid_hold_state`, `422 insufficient_funds`.

## Design decisions

**Signed postings, not debit/credit columns.** Accounting vocabulary says debit and credit; the invariant behind it is that an entry nets to zero. With signed amounts the invariant is one line — `SUM(amount) = 0` — and it is the same line in the service, in the trigger, and in the test that checks the whole ledger at the end of a run.

**Integers in minor units.** Amounts are `bigint` cents, never `decimal` or `float`. Rounding is a business decision that belongs where the price is computed, not inside the ledger.

**One currency per entry.** An account has a currency; an entry may only touch accounts of one currency. Foreign exchange is two entries and a rate — a concern for the layer above.

**The balance is a query.** Storing a balance next to the journal creates two sources of truth that must be kept in sync under concurrency. A `SUM` over an indexed `(account_id, id)` is fast for a long time; when it is not, a cached projection can be added *behind* the same invariant tests, not instead of them.

**Locks, not optimism.** Writes take `SELECT ... FOR UPDATE` on every account they touch, in ascending id order. That is what makes the overdraft check correct under load, and the ordering is what keeps two writers on the same pair of accounts from deadlocking. There is a test for each.

**Idempotency is key + body.** The key alone would make a reused key with a different amount silently return the old result. The stored request hash turns that into a `409`. A race on the same key is resolved by the unique index: the loser reads the winner's row and answers with it.

**Holds are two-phase, and the second phase is where money moves.** `authorize` writes no postings. That keeps the journal about things that happened, and makes "available" a derived number — balance minus pending holds — rather than a second balance to maintain.

**The database enforces what the application promises.** Immutability and balance are both checked in triggers. The application check gives a good error message; the trigger makes the promise hold even for a direct `psql` session.

**Plain SQL over an ORM.** A ledger's correctness lives in a dozen statements. They should be readable in the repository as written, with the `FOR UPDATE` visible.

## Not in scope, deliberately

- Multi-currency entries and FX.
- Hold expiry. A pending hold stays pending until captured or released; a sweeper is a few lines on top of `settled_at`.
- Authentication, rate limits, pagination.
- Cached balances and an event stream (outbox) for downstream projections.
- Migrations beyond "apply the SQL files in order once".

## Layout

```
db/migrations/001_init.sql     the whole schema, including the two triggers
src/Ledger.Core/               model, LedgerService (every operation is one transaction), Migrator
src/Ledger.Api/                minimal API; domain errors → HTTP codes in one middleware
tests/Ledger.Tests/            xUnit + Testcontainers: entries, holds, concurrency, HTTP
docker-compose.yml             postgres + api
```

## Working on it

`main` is what is released; `develop` is where work lands. Branch from `develop` as `feature/…`, `fix/…`, `chore/…`, `docs/…`, open a pull request back into `develop`. `main` only takes pull requests from `develop` — a required check enforces it. Commits follow Conventional Commits: `type(scope): description`.
