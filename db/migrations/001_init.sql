-- Ledger schema. Amounts are integers in the currency's minor unit (cents, satoshi, ...).
-- Balances are never stored: an account's balance is the sum of its postings.

create table accounts (
    id              uuid primary key,
    name            text        not null,
    currency        char(3)     not null,
    allow_negative  boolean     not null default false,
    created_at      timestamptz not null default now()
);

-- A journal entry is the unit of change. It is either fully in the ledger or not at all,
-- and once written it is never updated or deleted — corrections are new entries.
create table entries (
    id               uuid primary key,
    idempotency_key  text        not null unique,
    request_hash     bytea       not null,           -- to reject a reused key with a different body
    description      text        not null,
    reverses         uuid        references entries(id),
    created_at       timestamptz not null default now()
);

create table postings (
    id          bigserial   primary key,
    entry_id    uuid        not null references entries(id),
    account_id  uuid        not null references accounts(id),
    amount      bigint      not null check (amount <> 0)   -- signed: +credit to the account, -debit from it
);
create index postings_account_idx on postings (account_id, id);
create index postings_entry_idx   on postings (entry_id);

-- A hold reserves funds without moving them (card authorization). It reduces the
-- available balance until it is captured (money moves) or released (nothing moves).
create type hold_status as enum ('pending', 'captured', 'released');

create table holds (
    id               uuid primary key,
    idempotency_key  text        not null unique,
    request_hash     bytea       not null,
    account_id       uuid        not null references accounts(id),
    amount           bigint      not null check (amount > 0),
    status           hold_status not null default 'pending',
    captured_entry   uuid        references entries(id),
    created_at       timestamptz not null default now(),
    settled_at       timestamptz
);
create index holds_pending_idx on holds (account_id) where status = 'pending';

-- The journal is append-only. Enforce it in the database, not just in code.
create or replace function reject_mutation() returns trigger language plpgsql as $$
begin
    raise exception 'ledger rows are immutable: % on %', tg_op, tg_table_name
        using errcode = 'restrict_violation';
end $$;

create trigger entries_immutable  before update or delete on entries  for each row execute function reject_mutation();
create trigger postings_immutable before update or delete on postings for each row execute function reject_mutation();

-- Every entry must balance. The application checks this before writing; this constraint
-- is the backstop that makes the check impossible to bypass.
create or replace function assert_entry_balanced() returns trigger language plpgsql as $$
declare total bigint; n int;
begin
    select coalesce(sum(amount), 0), count(*) into total, n from postings where entry_id = new.id;
    if n < 2 then
        raise exception 'entry % needs at least two postings, has %', new.id, n using errcode = 'check_violation';
    end if;
    if total <> 0 then
        raise exception 'entry % does not balance: sum of postings is %', new.id, total using errcode = 'check_violation';
    end if;
    return null;
end $$;

-- Deferred so the postings can be inserted after the entry within the same transaction.
create constraint trigger entries_balanced
    after insert on entries deferrable initially deferred
    for each row execute function assert_entry_balanced();
