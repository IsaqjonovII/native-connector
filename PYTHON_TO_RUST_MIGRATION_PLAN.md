# Python → Rust onec backend — migration plan

Date 2026-10-07. Order (developer): **Sync / writes → Rust backend → prove Rust against 1C →
Python deprecation → managed Connector features later.** Contract: `RUST_SYNC_CONTRACT.md`. Audit:
`RUST_ONEC_BACKEND_AUDIT.md`.

Safety for every block: local only (isolated Postgres on 127.0.0.1, local Rust process, local test
bases), no production, no shared dev writes, no real data migration, live 1C writes only on
`AIBA_REWRITE_` test-owned objects, no commit/push without the developer's go.

---

## 1. Capability comparison

| Capability | Python today | Rust today | Desired Rust | Connector impact | Difficulty |
|---|---|---|---|---|---|
| Batch upsert | multipart, E11000 swallowed, totals only | multipart, no txn, totals only | `/rows`: one txn, per-row `i/u/n/s/r`, batch id, version guard | new target class | medium |
| Duplicate keys in batch | first wins silently | last wins silently | 400 naming keys | engine already dedupes | low |
| Atomic recorder | none | none | `/recorder`: one txn over all partitions × tables | `RecorderSync` becomes multi-partition | medium |
| Recorder reconcile scope | `scope=` widening bug | per-partition loop, `LIKE` | exact `recorder_key` equality inside the atomic call | none | low |
| Explicit delete | prune with 5 % cap | prune with 5 % cap | `/delete` by key, idempotent, no cap | `Approved` path unused | low |
| Table purge | none for the user | async jobs (admin) | `/purge` scoped, confirmed, chunked | rebuild works | low |
| Counts | cached `$inc` | cached, increment-only | exact `count(*)` per table | `ReportsTrustworthyCounts = true` | low |
| Verification read | `GET /api/v2/entity` pages | `by-type` (admin) | `/rows` keyset by key | `sync-verify` gets a Rust mode | low |
| Data coverage | read-modify-write | read-modify-write | row-locked merge, same shape (built) | engine now sends "loading" before the first row and the real reach after the snapshot (built, verified on KAN) | low |
| Sync-table config | `[]`→`null` | `[]`→`null` | tri-state | none | low |
| Org guard | none | none | per-row server check | rejections surface | low |
| Status | PATCH (can move company) | PATCH | `/status` status columns only | new route | low |
| Presence (D-1) | Redis, fails open | in-memory hub | `/presence` from the module's own hub (built) | unreadable = unknown, engine waits | low |
| Auth | user JWT + company check (fails closed on outage) | any valid JWT, no ownership | JWT + company access + tenant binding | none | **high (needs aiba-next claims)** |
| Writes (create/update/post/unpost/mark-delete) | WS relay, no durability | post + relay | durable command table, typed results | later | high |
| Catalog create / CAS update | relay / yes | relay / yes | commands, CAS `expected` | Host ops missing in Connector | medium |

**MUST HAVE BEFORE CUTOVER:** batch upsert with per-row results; batch idempotency; version guard;
atomic multi-partition recorder; explicit delete; table purge; exact counts; verification read;
data coverage (proven consumer: avtoprovodka duplicate-posting guard); tri-state config;
organisation guard; status; **auth with company access and tenant binding**; legacy-row
`recorder_key` backfill on any database that already holds rows; 1C comparison green on bilim and
KAN; the old Connector off for the base (D-1 cut-over rule).

**CAN COME LATER:** the write command table and its API; catalog create / CAS in the Connector;
presence; stock snapshot on the new API (Python/Rust legacy route keeps working); refhashes-style
change detection (the new engine does not need it); `sync_batch` cleanup ticker (bounded by time,
can start as a manual job); per-table coverage history.

**DO NOT PORT:** the 26 items in audit A §6 — swallowed duplicate keys, totals that always add up,
content-hash uniqueness for keyed rows, `scope=connection` deletes, 5 % caps as safety, `$inc`
counts, read-modify-write coverage, unauthenticated maintenance routes, PATCH reassigning
ownership, presence failing open, `[]`→`null`, name-prefix classification duplicated in two code
bases without a check, whole-file in-memory multipart, the dead write-idempotency service,
`_postCreateMethod`, forced posting, hard delete.

## 2. Blocks

**State 2026-10-08 (later):** write ownership per operation (D53) proven on customer objects; consumer
gap audit done (`research/rust-backend-audit/D`, `E`); the must-have Rust gaps it found are closed
(MIGRATION_STATUS). Dev cut-over: `DEV_RUST_CUTOVER_RUNBOOK.md` (prepared, not run — needs access).
Retirement stages and exact blockers: `PYTHON_RETIREMENT_PLAN.md`. Polling is a temporary transport.

**State 2026-10-08:** R7 (security / data integrity), R9 (normal writes: documents create / update /
post / unpost / mark-deleted, catalogs create / compare-and-set, each proven Rust → Connector → 1C → Sync
→ Rust = 1C) and R10 (per-base explicit switch + rollback between the isolated backend/1c and Rust, coverage
intact) DONE locally — MIGRATION_STATUS. Production stays closed until the developer approves.

**State 2026-10-07:** R0–R6 and R8 DONE locally (details and evidence: MIGRATION_STATUS). Rust
matches 1C on bilim (R4, 8 tables), on the full bilim document lifecycle (R5, 7 steps) and on real
multi-org KAN data (R6, 34 145 rows, 2 organisations). Crash/replay proven (R6 recovery). Coverage is
now reported by the engine (it was not — a cut-over blocker, closed). Next: R7 (security) before any
non-local use, then the developer's go for R9 / R10. Not committed.

| Block | Scope | Done when |
|---|---|---|
| **R0** | Isolated local stack: Postgres 12 cluster on 127.0.0.1:55440 (own data dir, the 1C cluster untouched), Rust module on a local port with `SERVICE_SECRET`/`JWT_SECRET` test values | `/health` answers, schema migrated |
| **R1** | Rust `/api/sync/v1`: capabilities, config, rows, delete, recorder, purge, counts, rows read, coverage, status; `sync_batch`; new columns + index | DB-backed Rust tests green (`cargo test` against the local cluster) |
| **R2** | Connector: `RustSyncTarget` (`src/OneC.Sync.Targets.Rust`), `RecorderSync` multi-partition, stub updated, engine atomic branch one call | unit + contract tests green, Python target untouched |
| **R3** | Wiring: `sync.json` `"target": "rust:http://127.0.0.1:PORT"` (loopback only), `sync-verify` Rust mode, a local helper that creates a test connection (and optional bindings) on the local Rust | engine syncs bilim into local Rust |
| **R4** | Prove against 1C on bilim: every configured table, every row compared field by field; counts vs 1C `COUNT` | zero missing / extra / wrong content |
| **R5** | Lifecycle on `AIBA_REWRITE_` objects in bilim: create, post, unpost, repost with fewer lines, mark-delete, delete; after each step `sync-verify` green; recorder rows exactly match 1C movements | all steps green |
| **R6** | Multi-org (KAN, read-only sync — no writes into KAN): bindings for ≥2 organisations on the local Rust; org guard; organisation change handled atomically (stub/unit + a synthetic multi-org fixture where KAN has no such document); recovery: kill the Connector mid-snapshot, kill Rust mid-batch, replay the same batch ids | verify green, no duplicates, no stale movements |
| **R7** | Before any non-local use: JWT company access check, tenant binding, constant-time secret, `recorder_key` backfill job for existing data, `sync_batch` cleanup | security review |
| **R8** | Measure: rows/s, request sizes, Rust CPU/RAM, Connector RAM, Postgres write throughput, transaction latency (p50/p95 of `/rows` and `/recorder`) | numbers in MIGRATION_STATUS |
| **R9** | Writes: command table + API (§9 of the contract), Connector Host ops catalog create / CAS update | separate approval |
| **R10** | Python deprecation: the old Connector off per base, the new Connector on Rust per base; Python routes kept read-only for history until consumers migrate | developer decision per tenant |

R0–R6 and R8 run now, automatically. R7 is the gate before anything leaves the machine. R9–R10 need
the developer.

## 3. Test strategy

```
                 1C
               /    \
         Python      Rust
```

Python is a compatibility reference only. **Rust must match 1C.** Where Python disagrees with 1C,
Rust follows 1C and the disagreement is written down as a Python bug.

Bases: **bilim** (file base, writes allowed on `AIBA_REWRITE_` objects), **KAN / KANSLER** (read-only
sync; no writes).

What is compared, per table and partition, after every scenario:

- catalog rows, documents, accounting registers, accumulation registers, independent information
  registers — key sets equal, every field equal (numbers by value);
- recorder movements — exactly the lines 1C has for each recorder, none stale after repost / unpost /
  delete;
- deletes — a row deleted in 1C is gone from Rust;
- organisation routing — every movement row in the partition of its organisation, nothing bound in
  the shared partition;
- counts — Rust exact count = 1C `COUNT` for the same filter;
- data coverage — `dataFrom` / `complete` match what the snapshot actually loaded.

Rust-side DB tests (no 1C): per-row codes, duplicate keys, version guard, replay, batch id reuse,
organisation guard, atomic recorder scope check and rollback on rejection, organisation change,
delete idempotency, purge resume, exact counts, coverage merge under concurrent writers.

Recovery and idempotency: Connector killed between upload and local completion (the S12 fault
hook), Rust killed mid-transaction (no partial rows: transaction), the same batch replayed (stored
answer), network failure after commit (replay returns the stored answer).

## 4. Measurements to record

rows/s for a full snapshot (bilim, KAN), request size distribution, Rust process CPU and working
set, Connector (Supervisor) working set, Postgres rows/s and WAL bytes, `/rows` and `/recorder`
latency p50/p95, transactions per document. Compared with the Python local backend numbers already in
MIGRATION_STATUS (S12).

**Measured 2026-10-07 (this PC, PG 12 from the 1C install, Rust debug build for the 1C runs, release
build for the bench):**

| What | Result |
|---|---|
| Synthetic insert, 200 000 register rows ~1.2 KB, 2000-row batches (`sync-rust bench`) | 5 537 rows/s; batch p50 206 ms, p95 1 392 ms |
| Same 200 000 rows re-sent, all unchanged | 37 106 rows/s; batch p50 52 ms, p95 56 ms |
| Recorder unit (document + 6 lines, then repost with 4), 4 000 units | 275 units/s; p50 3.5 ms, p95 4.7 ms |
| Rust module during the bench | peak 15 MB private, 13 s CPU for 58 s wall |
| Postgres during the bench | 40 s CPU — the write bottleneck (≈10 indexes on `entity_data`, several jsonb expression indexes) |
| KAN first snapshot, 8 tables / 34 145 rows (1C read included) | ≈2 min |
| KAN second snapshot from fresh local state (all rows unchanged) | 31 s, 77 batches, 0 rows rewritten |
| bilim 8 tables / 2 000 rows | 13.5 min, of which ≈13 min a bilim file-base stall (environment) |
| Lifecycle step → Rust changed | 2–11 s per step through the edge |
| Connector at idle (bilim run) | Supervisor 48 MB private, host 367 MB, Rust module 10 MB |
| Request size | ≤ 2000 rows and ≤ 6 MiB uncompressed per part; bodies over 64 KB sent gzip; server caps 8 MiB wire / 64 MiB decoded |

Not measured yet: WAL bytes, a full unwindowed KAN accounting register (3 041 863 rows) — the next
throughput run, and the place where COPY into a staging table may replace the multi-row INSERT if
Postgres stays the bottleneck.
