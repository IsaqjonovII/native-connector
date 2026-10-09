# Rust onec backend — audit for the new Connector

Date 2026-10-07. Read-only audit. Evidence and file:line citations are in the three working
reports, which this document summarises and does not repeat:

- `research/rust-backend-audit/A-connector-python-map.md` — what the new Connector actually calls
  on Python `backend/1c` (1c-arch @ `6d0bb2f`, backend/1c `development` @ `919479b`).
- `research/rust-backend-audit/B-rust-onec-map.md` — the Rust module `next-modules/onec`
  (master @ `6c46165`): stack, schema, routes, capability matrix, weaknesses.
- `research/rust-backend-audit/C-write-path-map.md` — the write path backend → Connector → 1C for
  the normal primitives. (Its "Rust has no command table" is outdated: `onec.command` and the
  `/api/sync/v1/…/commands` routes exist since R9.)
- `research/rust-backend-audit/D-aiba-next-consumers.md` (2026-10-08) — every aiba-next call to the
  1C backend: Python endpoint, caller, Rust equivalent, status, must-have, compat vs migrate.
- `research/rust-backend-audit/E-other-consumers.md` (2026-10-08) — KANSLER, report, partners, legacy
  apps. Python reference there is `origin/production` @ 9b4a50b.
- Follow-ups: `PYTHON_RETIREMENT_PLAN.md`, `DEV_RUST_CUTOVER_RUNBOOK.md`.

Goal (developer, 2026-10-07): **Rust is the backend the new Connector is designed for; Python stays a
temporary compatibility / reference target.** Rust is proven against 1C, not against Python.

---

## 1. The Connector's real dependency on Python today

The new Connector syncs to `backend/1c` **only from the developer/test harnesses** (`LocalBackend`,
`DevBackend`, `SyncMode` with a loopback or `dev` target). No production path uses it. The desktop
itself calls `backend/1c` only through `OneC.Cloud.CloudClient` (connection list / create / delete,
60 s status PATCH). So moving the Connector to Rust breaks no production user.

Engine → Python calls (`PythonMongoSyncTarget`, user Bearer JWT, one refresh on 401):

| # | Call | Python handler behaviour | Problem for us |
|---|---|---|---|
| C1 | `GET /api/v2/onec/{id}/org-bindings` | live bindings (deleting hidden) | — |
| C2 | `GET /api/v1/onec/{id}/sync-tables` | `null` for both "never set" and `[]` | tri-state lost |
| C3 | `POST /api/v2/entity/upload` multipart | keyed upsert; E11000 swallowed; no transaction | ack is not proof |
| C4 | `POST /api/v1/onec/{p}/prune-missing?scope=self` | 5 % cap on a live count; 409 on empty table | count heuristic |
| C5 | `GET /api/v1/onec/{p}/counts?scope=self` | cached `EntityCount` map, only grows | not trustworthy |
| C6 | `POST /api/v1/onec/{p}/reconcile-recorder?scope=self` | range delete on `recorder#`, no cap | no atomicity with upload |
| C7 | `GET /api/v2/onec/{id}` | `connection_state` from Redis | presence fails open |
| C8 | `PATCH /api/v2/onec/connection/{id}` | status heartbeat | PATCH can also move `userId`/`companyId` |

Not wired at all on the Python target: data coverage, stock, refhashes/refids, table purge (refused).

Row identity the Connector sends (`CanonicalMapper.Key`): catalog/document = lower-case GUID; chart =
`Код`; recorder-backed register row = `recorderGuid#lineNo`; independent information register =
metadata-derived `naturalKey`. Batches ≤ 2000 rows / 34 MB, 413 halves, a single over-size row is a
dead letter; transient errors: 3 attempts then the base pauses.

## 2. The Rust module as it is

- **Stack:** axum 0.7, sqlx 0.8 (Postgres), tokio. No Redis, no queue. Port 8012. Inline idempotent
  DDL at boot, deferred plain indexes. `TENANT_MODE=single|multi` (multi: one container, tenant by
  `X-Tenant`, tenant list from central). Six tickers (presence sweep, request reaper, orphan sweep,
  purge re-drive, recon summary, catalog rebuild). 91 unit tests, none DB-backed.
- **Shape:** a deliberate port of the Python API ("cutover = one env var"). It cleans up Mongo
  storage drift (typed ids, split row-key vs content-hash uniqueness) but keeps Python's contract:
  multipart upload, per-table totals, `scope=` query, 5 % prune cap, increment-only counts.
- **Schema (`store.rs:27-367`):** `onec.connection` (one row per base; org bindings are child rows
  with `connection_id` = parent and `org_ref`; config, coverage and `syncTables` live in `raw`
  jsonb); `onec.entity_data` (ONE jsonb table for every synced 1C row; partition key `onec_id` =
  connection or binding id; unique `(onec_id, table_name, row_key)` where keyed, else
  `(onec_id, table_name, data_hash)`; ~10 lookup indexes incl. stored `cp_key`); `entity_count`,
  `retail_daily_rollup`, `retail_rollup_coverage`, `recon_summary`, `catalog_entry`, `catalog_sync`,
  `table_purge_job`. No FKs, no migration versioning.
- **Partition model fits the Connector:** the Connector's `PartitionId` is exactly a Rust `onec_id`
  (connection = shared partition, binding = organisation partition). This is the key reason the
  new contract can sit on the existing table instead of a second store.
- **Readers depend on `entity_data`:** reconciliation, catalog projection, match/bank indexes,
  reports, `by-type` reads. Anything the new sync API writes must land in `entity_data` with the same
  side columns (`entity_type`, `ref_id`, `company_id`, `data_hash`) and the same dirty stamps
  (`recon_dirty_at`, `catalog_dirty_at`), or those readers silently go stale.

## 3. Capability map (Python capability → Rust status)

| Capability | Rust status | Note |
|---|---|---|
| Sync-table discovery | DONE IN RUST | same thin model; `[]` returned as `null` like Python |
| Entity batch upload | DONE IN RUST (weak) | no transaction, per-table totals only, rewrites unchanged rows, accepts uploads into a deleting base, no routing validation |
| Entity counts | PARTIAL | map only grows; "periodic recount" promised in comments, no ticker |
| Prune | DONE IN RUST | 5 % cap (count heuristic); no count decrement |
| Recorder reconcile | DONE IN RUST | per-recorder `LIKE` prefix delete, not index-backed; separate request from the upload |
| Atomic recorder (document + movements) | MISSING | |
| Org bindings | DONE IN RUST | transactional replace; company restamp runs inline in the request |
| Data coverage | DONE IN RUST (racy) | read-modify-write of `connection.raw` |
| Stock | DONE IN RUST | delete + insert in one transaction |
| Status / presence | DONE IN RUST | in-memory hub, lost on restart |
| Connection lookup | DONE IN RUST | |
| Table purge / rebuild | PARTIAL | async per-table purge jobs exist; full purge synchronous, skips side tables |
| Per-row results, idempotent batch id, version guard | MISSING | |
| Trustworthy counts / verification read | MISSING | |
| Write: post | DONE IN RUST | `write.rs:941` |
| Write: catalog CAS update | DONE IN RUST | `write.rs:1335`, capability-gated |
| Write: create document | IMPLEMENTED DIFFERENTLY | raw `write_to_1c` WS relay, no idempotency ledger |
| Write: catalog create | IMPLEMENTED DIFFERENTLY | raw `create_ref` relay |
| Write: update, unpost | MISSING | |
| Write: mark-delete | MISSING (relay only) | |
| eskey signing | MISSING | `onec.rs:30` TODO |
| Durable command store | MISSING | in-flight commands live in memory (`connector.rs:443-530`) |

## 4. Python behaviour Rust must NOT reproduce

Full list of 26 with citations: report A §6. The ones that shape the contract:

1. **Weak acknowledgement.** Rows counted as stored before the write; E11000 swallowed (a 500-row
   batch counted as stored on any duplicate); `totalSkipped = totalItems − totalInserted`, so totals
   always add up and prove nothing. The engine's `CountMismatches` metric can never fire on v2.
2. **Content-hash uniqueness that drops distinct keyed rows** with equal content. Identity is the key.
3. **No version guard** — a retried older write overwrites a newer one.
4. **No transactions** within an upload, between tables, or between a document and its movements.
5. **`scope=connection` deletes**: widening a delete to every partition with one live-key set
   (the multi-org bug, D46). Every delete must name its exact partition and table.
6. **Count heuristics as delete safety** (5 % cap measured on one counter, chunked from another).
7. **No table purge for the syncing principal**; register rebuild impossible from the Connector.
8. **Counts maintained by `$inc`**, never decremented, re-truthed every 6 h.
9. **Coverage as read-modify-write** on the connection document (lost updates).
10. **Unauthenticated maintenance routes**, list without user filter, create without company check,
    PATCH that reassigns `userId`/`companyId`, presence that fails open, DB errors as 400 "not found".
11. **Thin sync-table model**: `[]` collapsed to `null`, unknown fields dropped, list not enforced.
12. **Name-prefix movement classification duplicated in two codebases**; every
    `InformationRegister_*` filed as per-org.
13. **Whole-file in-memory multipart** with no app-level size limit.

## 5. Rust-side weaknesses to fix or avoid in the new path

Security (must be closed before any non-local use, not part of the local sync work):
- connector WS unauthenticated by default (`CONNECTOR_WS_REQUIRE_SECRET=false`) and relays writes;
- any valid JWT reaches any `onec_id` / company (no ownership check);
- multi-tenant: tenant from `X-Tenant`, JWT not bound to it;
- service secret compared with `==`; manifest route unauthenticated; credentials kept in `raw`.

Integrity and performance (avoided by the new sync API, left in place for the legacy routes):
- non-transactional upload, unchanged rows rewritten, increment-only counts, racy coverage write;
- `LIKE` prefix recorder delete and `raw->>'id'` prune not index-backed;
- 128 MB bodies decoded fully in memory; unbounded per-socket queues;
- full purge and orphan sweep as single unbatched DELETEs.

## 6. Writes (summary of report C)

- No layer has a durable command store. Python's `write_idempotency.py` is dead code (`WrittenDoc`
  model does not exist). Rust keeps in-flight commands in memory.
- No command is authenticated or signed end to end.
- New Connector: create / update / post / unpost / mark-delete exist locally (marker idempotency
  `AIBA_<KIND>_<id>`); **catalog create and catalog CAS update are missing**; no cloud command
  channel yet (D41, on purpose).
- **Product conflict to decide:** the new Connector refuses to post / update / delete a document
  whose Комментарий does not start with `AIBA_` (D18); Python's `post_1c_document` exists to post
  drafts made by 1C users.
- Unpost and mark-delete differ (Python unposts first; the new Connector does not) — the contract
  needs an explicit `unpostFirst`.

## 7. What the local proof added (2026-10-07)

The new API was built (`RUST_SYNC_CONTRACT.md`) and run against real 1C data, locally only. Results
and evidence: MIGRATION_STATUS. Findings beyond §1–6:

1. **Rust matches 1C** on bilim (8 tables), on the full document lifecycle (create, post, change +
   repost, unpost, repost, mark-deleted, delete) and on real multi-org KAN (2 organisations, 34 145 rows,
   0 rows in a wrong partition). A re-send of all 34 145 rows answered `unchanged` for every one and
   rewrote nothing — the legacy upload rewrites every row it is sent (§5).
2. **The Connector, not Rust, had a row-shape bug:** two read paths gave one document two canonical
   shapes (empty table parts present or absent). Fixed in the mapper. Any backend would have stored the
   difference; only a 1C comparison found it.
3. **Coverage was a silent cut-over blocker:** the new engine reported none, and the consumer reads "no
   entry" as "whole history", so a windowed or half-copied table would have licensed duplicate
   documents. The engine now reports "loading" before the first row and the real reach after. The
   Python target carries the same call (its backend has the route), so this is not Rust-only.
4. **Crash safety is transactional, not best-effort:** killed inside the transaction → nothing stored,
   no batch record; killed after commit → the replay answers from the batch record. Python cannot give
   either (no transaction, no batch record).
5. **Postgres, not Rust, bounds write throughput** (5 537 inserted rows/s of ~1.2 KB; Rust 15 MB, 13 s
   CPU vs Postgres 40 s). The ≈10 indexes on `entity_data` (several jsonb expression indexes for the
   legacy readers) are the cost; COPY into a staging table is the next lever if needed.
6. **Environment, not backend:** the bilim file base stalls for 10 min – 3 h when a second process opens
   it beside the host, and once inside a write; writes through the Supervisor's own host did not stall.
   Kept separate from every Rust result.

7. **R7 hardening found real defects (2026-10-08):** with an audience configured, a JWT without `aud`
   still passed; a U+0000 in one key or one 1C text value failed the whole batch (jsonb cannot hold it) —
   a 503 the Connector would retry forever; ids were trimmed and parsed loosely. All fixed and covered.
8. **R9/R10 found one more coverage hole, in the Connector:** a base copied before coverage existed kept
   "no entry" (= whole history) forever. Fixed: the final reach is sent once per copied table.
9. Python's own isolated backend received correct coverage after the R10 switch — the Python target
   carries the same coverage call, so a rollback to Python is not a coverage regression.

## 8. Conclusions that drive the design

1. **Keep `entity_data` as the store.** Its partition key equals the Connector's partition, its
   readers are the product. Add the missing guarantees around it, not a second store.
2. **Add a new, separate sync API** (`/api/sync/v1/...`) rather than fixing the Python-shaped routes
   in place. The legacy routes stay for the old Connector until it is retired; the new Connector
   uses only the new API. Python's API does not define it.
3. The new API must give: per-row outcomes, idempotent batch ids, a source-version guard, one
   transaction per batch, an atomic multi-partition recorder call, explicit scoped deletes, a
   scoped table purge, exact counts, a verification read, transactional coverage, tri-state config,
   server-side partition and organisation guards.
4. Writes: design the contract now (`RUST_SYNC_CONTRACT.md` §9), build after Sync is proven.
5. Before any non-local use (R7): JWT company access, tenant binding, constant-time secret,
   `recorder_key` backfill for rows the legacy upload wrote, `sync_batch` cleanup.
