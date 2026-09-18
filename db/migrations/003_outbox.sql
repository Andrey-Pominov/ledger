-- Transactional outbox. Every write that changes the ledger also appends one row here, in
-- the same transaction. A consumer reads forward from a cursor; nothing is ever marked as
-- delivered, so any number of consumers can follow at their own pace and resume after
-- downtime. Delivery is at-least-once by construction: the consumer owns its cursor.

create table events (
    id           bigserial   primary key,
    type         text        not null,   -- account.created | entry.posted | hold.authorized | hold.released
    occurred_at  timestamptz not null default now(),
    payload      jsonb       not null
);

create trigger events_immutable before update or delete on events for each row execute function reject_mutation();

-- Why a reader cannot simply take "id > cursor":
--
-- bigserial hands out ids at INSERT time, not at COMMIT time. Transaction A can take id 5,
-- transaction B take id 6 and commit first. A reader that sees 6, advances its cursor past 5
-- and only then has A commit would never see 5. The reader therefore only accepts rows whose
-- writing transaction is older than every transaction still in progress: those rows cannot be
-- overtaken by a lower id any more. `xmin` is the row's writing transaction; the snapshot's
-- xmin is the oldest transaction still running.
--
-- xmin is a 32-bit xid, so this comparison is valid until xid wraparound. That is enough for
-- this ledger and stated here so nobody mistakes it for a general solution.
create view events_stable as
select id, type, occurred_at, payload
from events
where xmin::text::bigint < pg_snapshot_xmin(pg_current_snapshot())::text::bigint;
