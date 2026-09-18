# ledger

A double-entry ledger with holds and idempotent writes. .NET 10, PostgreSQL, plain SQL.

It is small on purpose. The point is not the feature list — it is that every rule about money is enforced in one place, tested against a real database, and impossible to bypass from the application side.

## What it guarantees

- **Every entry balances.** A journal entry is a set of signed postings whose sum is zero. The service checks this; a deferred constraint trigger in PostgreSQL checks it again at commit, so a bug in the application cannot write an unbalanced entry.
- **Balances are never stored.** An account's balance is `SUM(postings.amount)`. There is no column that can drift from the journal.
- **The journal is append-only.** `UPDATE` and `DELETE` on `entries` and `postings` raise at the database level. Corrections are reversing entries that point at what they reverse.
- **No account is overdrawn unless it is allowed to be.** The check runs under a row lock on the account, so two concurrent transfers cannot both pass a check that only one of them should.
- **Every write is idempotent.** Each entry and hold carries a caller-supplied key. Replaying a request returns the original result and writes nothing; reusing a key with a different body is a `409`.
- **Holds reserve without moving, and are never edited.** `authorize` lowers the available balance and writes one immutable row. `capture` moves money — a hold may be captured several times, the way a card authorization is often cleared in more than one message — and `release` gives back what remains. A hold's state (what was captured, what was released, what still reserves funds, whether it is open, closed or expired) is derived from the journal; nothing about it is updated in place. Expiry is a timestamp, not a job: an expired hold simply stops counting.

- **Every change is announced, exactly in journal order.** Each write appends one row to an outbox in the same transaction. `GET /events?after=<cursor>` reads it forward. Nothing is marked delivered: consumers own their cursors, follow at their own pace and resume after downtime. Delivery is at-least-once by construction, and a cursor can never skip a row that commits late.

The tests in [`tests/Ledger.Tests`](tests/Ledger.Tests) exercise each of these against PostgreSQL — including 40 parallel transfers against a balance that only covers 10 of them, 12 parallel partial captures of a hold that fits 6, 16 parallel requests sharing one idempotency key, and an outbox reader facing a transaction that took an id and has not committed.

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

# reserve 12.00 for up to an hour — balance stays 50.00, available drops to 38.00
HOLD=$(curl -s -X POST localhost:8088/holds -H 'content-type: application/json' \
  -d "{\"idempotencyKey\":\"auth-1\",\"accountId\":\"$ALICE\",\"amount\":1200,\"timeoutSeconds\":3600}" | jq -r .hold.id)
curl -s localhost:8088/accounts/$ALICE

# try to spend 40.00 while 12.00 is held: 422 insufficient_funds

# clear 7.00 of it to a shop, then another 3.00 — two captures against one authorization
curl -s -X POST localhost:8088/holds/$HOLD/capture -H 'content-type: application/json' \
  -d "{\"idempotencyKey\":\"clr-1\",\"toAccountId\":\"$SHOP\",\"amount\":700}"
curl -s -X POST localhost:8088/holds/$HOLD/capture -H 'content-type: application/json' \
  -d "{\"idempotencyKey\":\"clr-2\",\"toAccountId\":\"$SHOP\",\"amount\":300}"

# give back the last 2.00; the hold is now closed
curl -s -X POST localhost:8088/holds/$HOLD/release -H 'content-type: application/json' \
  -d '{"idempotencyKey":"rel-1"}'
curl -s localhost:8088/holds/$HOLD

curl -s localhost:8088/accounts/$ALICE/statement

# everything that happened, in order — a projection or a fraud check would start here
curl -s 'localhost:8088/events?after=0&limit=100'
```

| Method | Path | |
|---|---|---|
| `POST` | `/accounts` | create; `allowNegative` marks funding / clearing accounts |
| `GET` | `/accounts/{id}` | balance and available |
| `GET` | `/accounts/{id}/statement` | postings with a running balance |
| `POST` | `/entries` | post a balanced entry (idempotent) |
| `GET` | `/entries/{id}` | |
| `POST` | `/entries/{id}/reverse` | new entry with every posting negated (idempotent) |
| `POST` | `/holds` | authorize, optional `timeoutSeconds` (idempotent) |
| `GET` | `/holds/{id}` | the hold and its derived state |
| `POST` | `/holds/{id}/capture` | move up to what remains; may be repeated with new keys (idempotent) |
| `POST` | `/holds/{id}/release` | give back up to what remains (idempotent) |
| `GET` | `/events?after=&limit=` | the outbox, forward from a cursor; `next` is the cursor for the following page |

Errors: `400 invalid_entry`, `404 not_found`, `409 idempotency_conflict`, `409 invalid_hold_state`, `422 insufficient_funds`.

## Design decisions

**Signed postings, not debit/credit columns.** Accounting vocabulary says debit and credit; the invariant behind it is that an entry nets to zero. With signed amounts the invariant is one line — `SUM(amount) = 0` — and it is the same line in the service, in the trigger, and in the test that checks the whole ledger at the end of a run.

**Integers in minor units.** Amounts are `bigint` cents, never `decimal` or `float`. Rounding is a business decision that belongs where the price is computed, not inside the ledger.

**One currency per entry.** An account has a currency; an entry may only touch accounts of one currency. Foreign exchange is two entries and a rate — a concern for the layer above.

**The balance is a query.** Storing a balance next to the journal creates two sources of truth that must be kept in sync under concurrency. A `SUM` over an indexed `(account_id, id)` is fast for a long time; when it is not, a cached projection can be added *behind* the same invariant tests, not instead of them.

**Locks, not optimism.** Writes take `SELECT ... FOR UPDATE` on every account they touch, in ascending id order. That is what makes the overdraft check correct under load, and the ordering is what keeps two writers on the same pair of accounts from deadlocking. There is a test for each.

**Idempotency is key + body.** The key alone would make a reused key with a different amount silently return the old result. The stored request hash turns that into a `409`. A race on the same key is resolved by the unique index: the loser reads the winner's row and answers with it.

**Holds are two-phase, and the second phase is where money moves.** `authorize` writes no postings. That keeps the journal about things that happened, and makes "available" a derived number — balance minus what remains on open holds — rather than a second balance to maintain.

**A hold's state is a query, like a balance.** The first version of this ledger kept a `status` column on the hold and updated it on capture — the one place where a row about money was edited after the fact. It is gone. A hold is written once; captures are journal entries that point at it, releases are rows in `hold_releases`, and `remaining`, `captured`, `released`, `open / closed / expired` are computed in a view. Several partial captures against one hold fall out of this for free, and so does expiry: there is no sweeper, the state is a function of the clock.

**Where this differs from TigerBeetle.** The account / two-phase transfer model here follows TigerBeetle's shape — pending, post, void, an id-based idempotency contract — because it is the right shape. TigerBeetle stores four counters per account (`debits_pending`, `debits_posted`, `credits_pending`, `credits_posted`) and updates them atomically with each transfer; this ledger stores none and derives everything from the journal. At TigerBeetle's scale the counters are the point. At this scale one invariant is easier to keep true than four, and the counters can be introduced later as a cache behind the same tests — which is the order these things should happen in.

**The outbox is a table, and the cursor is not the sequence.** Events are rows written in the writer's own transaction — the only way to make "the change happened" and "the change was announced" the same fact. The tempting reader is `WHERE id > cursor`, and it is wrong: `bigserial` assigns ids at `INSERT`, not at `COMMIT`. Transaction A can take id 5, transaction B take 6 and commit first; a reader that sees 6 and moves on will never see 5. The reader therefore accepts only rows whose writing transaction (`xmin`) is older than every transaction still in progress — those cannot be overtaken any more. That rule is one `WHERE` clause in a view, [`events_stable`](db/migrations/003_outbox.sql), and one test that opens a transaction, takes an id, and checks that everything after it is held back until it commits. `xmin` is 32-bit, so the comparison is valid until xid wraparound; that is stated in the migration rather than hidden.

**The database enforces what the application promises.** Immutability and balance are both checked in triggers. The application check gives a good error message; the trigger makes the promise hold even for a direct `psql` session.

**Plain SQL over an ORM.** A ledger's correctness lives in a dozen statements. They should be readable in the repository as written, with the `FOR UPDATE` visible.

## Not in scope, deliberately

- Multi-currency entries and FX.
- Authentication, rate limits, pagination.
- Cached balances. The outbox exists so a projection can be built; none is built here.
- A broker. The outbox is polled over HTTP; pushing it into Kafka or a queue is a consumer's job, not the ledger's.
- Migrations beyond "apply the SQL files in order once".

## Layout

```
db/migrations/001_init.sql     accounts, journal, the balance and immutability triggers
db/migrations/002_immutable_holds.sql   holds become append-only; hold_state view derives the rest
db/migrations/003_outbox.sql   events table and the events_stable view with the cursor rule
src/Ledger.Core/               model, LedgerService (every operation is one transaction), Migrator
src/Ledger.Api/                minimal API; domain errors → HTTP codes in one middleware
tests/Ledger.Tests/            xUnit + Testcontainers: entries, holds, concurrency, HTTP
docker-compose.yml             postgres + api
```

## Working on it

`main` is what is released; `develop` is where work lands. Branch from `develop` as `feature/…`, `fix/…`, `chore/…`, `docs/…`, open a pull request back into `develop`. `main` only takes pull requests from `develop` — a required check enforces it. Commits follow Conventional Commits: `type(scope): description`.
