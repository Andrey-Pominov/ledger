-- v0.2: holds stop being mutable rows with a status column. A hold is written once; what
-- happened to it afterwards is in the journal (captures) and in hold_releases (releases),
-- and its state is derived from those. This is the same principle the journal already
-- follows: nothing about money is ever updated in place.

alter table holds drop column status, drop column captured_entry, drop column settled_at;
drop type hold_status;
drop index if exists holds_pending_idx;
create index holds_account_idx on holds (account_id);

-- Optional expiry. An expired hold no longer reserves funds and can no longer be captured.
-- Nothing runs to expire it: the state is a function of the clock.
alter table holds add column expires_at timestamptz;

-- A capture is an ordinary balanced entry that also points at the hold it settles.
alter table entries add column hold_id uuid references holds(id);
create index entries_hold_idx on entries (hold_id) where hold_id is not null;

-- A release gives part or all of the remaining reservation back. No postings: nothing moved.
create table hold_releases (
    id               bigserial   primary key,
    hold_id          uuid        not null references holds(id),
    idempotency_key  text        not null unique,
    request_hash     bytea       not null,
    amount           bigint      not null check (amount > 0),
    created_at       timestamptz not null default now()
);
create index hold_releases_hold_idx on hold_releases (hold_id);

create trigger holds_immutable         before update or delete on holds         for each row execute function reject_mutation();
create trigger hold_releases_immutable before update or delete on hold_releases for each row execute function reject_mutation();

-- Everything a caller wants to know about a hold, computed from the rows above.
--   captured  = what the journal moved out of the account against this hold
--   released  = what was handed back without moving
--   remaining = amount - captured - released      (what still reserves funds)
--   status    = closed   when nothing remains
--               expired  when something remains but the clock has passed expires_at
--               open     otherwise
create view hold_state as
select h.id, h.idempotency_key, h.account_id, h.amount, h.created_at, h.expires_at,
       c.captured, r.released,
       h.amount - c.captured - r.released as remaining,
       case
           when h.amount - c.captured - r.released = 0 then 'closed'
           when h.expires_at is not null and h.expires_at <= now() then 'expired'
           else 'open'
       end as status
from holds h
cross join lateral (
    select coalesce(sum(-p.amount), 0)::bigint as captured
    from entries e join postings p on p.entry_id = e.id
    where e.hold_id = h.id and p.account_id = h.account_id
) c
cross join lateral (
    select coalesce(sum(amount), 0)::bigint as released
    from hold_releases where hold_id = h.id
) r;
