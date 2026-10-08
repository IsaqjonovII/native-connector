# Rust sync contract — normal Sync, new Connector ↔ Rust onec

Status: **v1 implemented locally and proven against 1C, not deployed, not committed** (2026-10-07):
bilim 8 tables and the full document lifecycle, KAN real multi-org (2 organisations, 34 145 rows),
crash/replay — evidence in MIGRATION_STATUS. R7 security items open before any non-local use. Scope: only what NORMAL SYNC needs.
No control plane, no presence/fleet, no manifest, no remote diagnostics (`CONTROL_PLANE_DESIGN.md`
is parked). Writes (§9) are designed here, built later.

Inputs: `RUST_ONEC_BACKEND_AUDIT.md`, `SYNC_ENGINE_ARCHITECTURE.md` (§7 keys, §9 recorder, §17
target abstraction, §19 v3 proposal, §21 org routing), D46 (multi-org scope bug).

The Connector keeps one abstraction, `IBackendSyncTarget`. Targets:
`PythonMongoSyncTarget` (temporary, compatibility/reference), `RustSyncTarget` (intended permanent),
`StubSyncTarget` (tests). The engine branches on `TargetCapabilities` only.

---

## 0. Ground rules

1. **1C is the truth.** Where Python and 1C disagree, Rust follows 1C.
2. **The store is `onec.entity_data`.** The partition id is the existing `onec_id` (connection row =
   shared partition, binding row = one organisation). The readers that already exist
   (reconciliation, catalog projection, indexes, reports) keep working unchanged.
3. **A separate API** under `/api/sync/v1`. The Python-shaped routes stay for the old Connector and
   are not changed by this work. The new Connector calls nothing else.
4. **Exact scope everywhere.** Every write, delete and reconcile names its connection, partition and
   table. No call widens from organisation to connection. `READ SCOPE == RECONCILE/DELETE SCOPE`.
5. **No count heuristics.** Deletes are explicit keys or an explicit recorder reconcile; nothing is
   refused or allowed because of a fraction of a counter.
6. **Proof, not optimism.** Every row gets an outcome. Counts are computed, not maintained.
7. **Bounded.** Request ≤ 8 MiB on the wire (gzip allowed), ≤ 64 MiB decoded, ≤ 2000 rows per table
   batch, ≤ 20 000 movement rows per recorder call. Bigger is a 413 the Connector splits.

## 1. Auth and scope check

- `Authorization: Bearer <user JWT>` (the desktop's token) or `X-Service-Secret` for tests and
  service callers — the module's existing `secret_or_jwt`.
- Every route takes `{conn}` in the path. The server resolves `{conn}` to a **connection** row
  (`connection_id IS NULL`) that is not `deleting`; a binding id there is a 400.
- Every `partition` in a body must be `{conn}` itself or a live binding of `{conn}`
  (`connection_id = {conn}`, `org_ref IS NOT NULL`, `status <> 'deleting'`). Otherwise the whole
  request is refused (`400 partition_not_in_connection`) — before any write.
- A connection in `deleting` answers `409 {"error":"connection_deleting"}` (engine: `Gone`); a
  binding in `deleting` answers `409 {"error":"partition_deleting"}` (engine: transient, re-read
  bindings).
- **Built and proven 2026-10-08 (R7):** the JWT's `tenant` must be this tenant (multi: `X-Tenant`;
  single: `ONEC_TENANT_SLUG` when set); `aud` required when `AIBA_JWT_AUDIENCE` is set; superadmin refused;
  a user reaches only the companies aiba-next's `user_company_ids` gives them (tenant admin: all), for the
  connection AND for every partition named; a connection/partition they may not touch answers exactly
  like a missing one; every auth failure is the same flat 401; "could not check" is 503; constant-time
  secret compare; strict ids; U+0000 refused per row (`nul_in_data`), never failing a batch.

## 2. Row identity and storage

| Family | Key | Stored as |
|---|---|---|
| catalog, document | lower-case GUID `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx` | `row_key`; also `ref_id` |
| chart of accounts / chart of characteristic types | `Код` | `row_key` |
| recorder-backed register (accounting, accumulation, recorded information) | `recorderGuid#lineNo` | `row_key`, plus `recorder_key = recorderGuid` |
| independent information register | metadata-derived `naturalKey` | `row_key` |

- Uniqueness: the existing `onec_entity_rowkey_uq (onec_id, table_name, row_key) WHERE row_key IS
  NOT NULL`. Keyless rows are refused by this API (the content-hash path is legacy only).
- New columns on `entity_data` (metadata-only `ALTER … ADD COLUMN`, no table rewrite):
  `source_version bigint`, `recorder_key text`. New partial index
  `(onec_id, table_name, recorder_key) WHERE recorder_key IS NOT NULL` — the reconcile delete is an
  index range on exact equality, never a `LIKE`.
- Key validation (per row, `rejected`): non-empty, ≤ 512 bytes, no control characters; a key of the
  GUID form must be lower-case; a row whose key contains `#` must be `<lower-case GUID>#<digits>`.
- One canonical row per object whichever 1C read produced it: empty table parts are dropped by the
  Connector's mapper (the host's list read omits them, its by-id read sent `[]` — found by R5).
- `data` is the Connector's canonical JSON object (it carries `__rowKey`; the server strips
  `__rowKey` before storing so stored `raw` matches what legacy readers expect, and checks it equals
  `key`).
- Side columns written exactly as the legacy upload does: `entity_type` (provider table map),
  `ref_id` (GUID in `id`/`Ref_Key`), `company_id` (the partition's company), `data_hash` (the
  module's canonical hash). `recon_dirty_at` / `catalog_dirty_at` stamped in the same transaction
  when a row changed.

## 3. Organisation guard (server side, per row)

On a connection **with** live bindings:

| Partition | Table kind (`scope::is_movement_table`) | Rule | Rejection |
|---|---|---|---|
| binding | movement | `data.orgRef` (lower-cased GUID) = binding `org_ref` | `org_mismatch` |
| binding | reference | refused | `reference_in_org_partition` |
| connection (shared) | movement | `orgRef` must NOT be a live bound organisation | `org_bound_elsewhere` |
| connection (shared) | reference | allowed | — |

On a connection without bindings everything goes to the connection partition. The rule mirrors the
Connector's `OrgRouter`; a disagreement is a rejection the Connector reports, never a silent misfile.

## 4. Endpoints

All bodies JSON (`Content-Type: application/json`, optional `Content-Encoding: gzip`).
Errors: `{"error": "<code>", "detail": "<text>"}`.

### 4.1 `GET /api/sync/v1/capabilities`

```json
{ "contract": 1, "perRowResults": true, "atomicRecorder": true, "sourceVersionGuard": true,
  "maxBatchBytes": 8388608, "maxBatchRows": 2000, "maxRecorderRows": 20000, "gzip": true,
  "duplicateKeys": "reject", "trustworthyCounts": true }
```

### 4.2 `GET /api/sync/v1/connections/{conn}/config`

```json
{ "connectionId": "12", "provider": "unisoft", "sharedPartition": "12",
  "partitions": [ { "partitionId": "13", "orgRef": "…guid…", "companyId": "7", "status": "active" } ],
  "tables": null }
```

`tables` is **tri-state**: `null` = never set (Connector applies its defaults), `[]` = deliberately
nothing, `[{"table":"…","reports":false}]` = the list. Unlike Python, `[]` is returned as `[]`.

### 4.3 `POST /api/sync/v1/connections/{conn}/rows` — batch upsert

```json
{ "batchId": "b-…", "partition": "13", "table": "Document_РеализацияТоваровУслуг",
  "rows": [ { "key": "…guid…", "version": 4711, "data": { … } } ] }
```

One transaction: idempotency record → per (partition, table) advisory lock → read the stored
`(row_key, data_hash, source_version)` of the batch's keys → classify → one multi-row upsert of
the inserted/updated rows → counts → dirty stamps → commit.

Per row, in input order:

| Code | Meaning |
|---|---|
| `i` | inserted (no row with this key before) |
| `u` | updated (content changed) |
| `n` | unchanged (same content hash; source version raised if the incoming one is higher) |
| `s` | stale: stored `source_version` is higher than the incoming one; nothing written |
| `r` | rejected (validation, organisation guard); listed in `rejected` |

```json
{ "batchId": "b-…", "replayed": false, "status": "iiunsr",
  "counts": { "inserted": 2, "updated": 1, "unchanged": 1, "stale": 1, "rejected": 1 },
  "rejected": [ { "index": 5, "key": "…", "reason": "org_mismatch", "retryable": false } ] }
```

- A key twice in one batch: `400 duplicate_keys` naming them; nothing written.
- A rejected row does not fail the batch; the valid rows commit. Rejections are never retryable
  automatically (the Connector dead-letters them and shows them).
- Same `batchId` again: the stored answer with `"replayed": true`; nothing re-executed. A
  `batchId` reused with a different body hash is `409 batch_id_reused`.

### 4.4 `POST /api/sync/v1/connections/{conn}/delete` — explicit delete by key

```json
{ "batchId": "…", "partition": "12", "table": "Catalog_Номенклатура", "keys": ["…"], "reason": "deleted_in_1c" }
```

Deletes exactly those keys in exactly that partition and table. Idempotent: an absent key is
`a`, a deleted one `d`. Response `{ "batchId", "replayed", "status": "dda", "deleted": 2, "absent": 1 }`.
No cap — the Connector only sends keys it proved gone in 1C (`ByIds` miss), never a guessed set.

### 4.5 `POST /api/sync/v1/connections/{conn}/recorder` — atomic recorder sync

The whole unit for one document (post, unpost, repost, movement change, organisation change,
deletion) in **one transaction across every partition and table it names**:

```json
{ "batchId": "…", "recorderKey": "…guid…", "version": 4711,
  "documentTable": "Document_РеализацияТоваровУслуг",
  "document": { "partition": "13", "data": { … } },          // null = the document is gone
  "partitions": ["12", "13"],                                // reconcile scope: partitions
  "movementTables": ["AccountingRegister_Хозрасчетный_RecordType", "AccumulationRegister_ТоварыНаСкладах_RecordType"],
  "movements": [ { "partition": "13", "table": "AccountingRegister_Хозрасчетный_RecordType",
                   "rows": [ { "key": "…guid…#1", "data": { … } } ] } ] }
```

Server, in one transaction:

1. Advisory lock on `(conn, recorderKey)`.
2. **Scope check:** every `movements[].partition` ∈ `partitions`, every `movements[].table` ∈
   `movementTables`, `document.partition` ∈ `partitions`, every movement key starts with
   `recorderKey#`. Any violation → `400 out_of_scope`, nothing written.
3. **Stale guard:** if any stored document row with key `recorderKey` in `partitions` has
   `source_version > version` → answer `{"stale": true}`, nothing written.
4. Document: upsert into `document.partition`; delete the document row from every other partition
   in `partitions` (organisation changed). `document: null` → delete it from all of `partitions`.
5. For every `(p, t)` in `partitions × movementTables`: upsert the given rows (organisation guard
   applies; any rejection aborts the whole unit — a half-applied recorder is exactly what this call
   exists to prevent), then delete rows with `recorder_key = recorderKey` in `(p, t)` whose key is
   not in that `(p, t)`'s live set.
6. Commit.

**Invariant:** the delete scope is exactly `partitions × movementTables × recorderKey`, the same set
the Connector read. Nothing outside it can be touched. One transaction covers every affected row
because all of them live in `entity_data`.

Response:

```json
{ "batchId": "…", "replayed": false, "stale": false, "document": "u",
  "movements": { "inserted": 3, "updated": 0, "unchanged": 5, "removed": 2 },
  "tables": [ { "partition": "13", "table": "…", "inserted": 3, "updated": 0, "unchanged": 5, "removed": 2 } ] }
```

### 4.6 `POST /api/sync/v1/connections/{conn}/purge` — table rebuild

```json
{ "batchId": "…", "partition": "12", "table": "AccumulationRegister_…", "confirm": "purge AccumulationRegister_…" }
```

Removes every row of one table in one partition, in chunks of 20 000 (one transaction per chunk,
so a 19 M-row register does not hold one huge transaction). `confirm` must equal
`"purge " + table`. Response `{ "deleted": n, "complete": true }`; an interrupted purge is resumed by
calling again (idempotent). Only the D-3 rebuild flow (user confirmed that table) calls it.

### 4.7 `GET /api/sync/v1/connections/{conn}/counts?partition=P[&table=T…]`

Exact `count(*)` per table from the `(onec_id, table_name)` index, in one statement
(`GROUP BY table_name`). Never a cached counter. `{ "partition": "12", "tables": { "T": 1234 } }`.

### 4.8 `GET /api/sync/v1/connections/{conn}/rows?partition=P&table=T&after=K&limit=N`

Verification read: rows in key order (`row_key COLLATE "C"`), keyset paging, `limit ≤ 5000`.
`{ "rows": [ { "key", "version", "hash", "data" } ], "next": "K" | null }`.
Used by first-copy validation, `sync-verify`, and tests. `fields=key` returns keys and hashes only.

### 4.9 Coverage — `PUT` / `GET /api/sync/v1/connections/{conn}/coverage`

```json
{ "partition": "12", "tables": [ { "table": "Document_…", "dataFrom": "2025-01-01", "complete": true,
                                   "checkpoint": "snapshot:…" } ] }
```

**What the engine sends (built 2026-10-07, `BaseSyncAgent`; `ICoverageTarget` on the Rust and
Python targets).** The consumer (aiba-next `sotuv.rs mirror_reach_from_onec`, backend/1c
`coverage.py`) reads: no entry = whole history; `complete:true` = whole history; `complete:false` +
`dataFrom` = rows from that day on only; `complete:false` without `dataFrom` = unknown, every miss
doubted. So a table with no entry is the dangerous default, and the engine never leaves one:

| When | Sent | Where |
|---|---|---|
| before the first row of a document or register table's snapshot (must be acknowledged, else the snapshot does not start) | `complete:false`, `dataFrom:null` (loading = unknown) | every partition of a movement table; the shared one otherwise |
| after the table's snapshot completed | windowed (`from` set): `complete:false, dataFrom:<from>`; unwindowed: `complete:true` | same |
| the final report failed | kept pending in the local state, sent first on the next activation | same |
| catalogs, charts | nothing (they load whole; "no entry" = whole is right for them) | — |

A table rebuild (D-3) starts its snapshot again, so it goes back to "loading" first. Verified live on
KAN (2026-10-07): every windowed table `complete:false, dataFrom:2026-06-01` in the shared and both
organisation partitions, readable on the consumer's route `/api/internal/admin/onecs/{id}`.

Stored where the existing consumers read it (`connection.raw.dataCoverage` of the partition's row,
same `{dataFrom, complete}` shape plus `updatedAt` and optional `checkpoint`), but merged **in SQL in
one statement** (`jsonb_set` per table, row locked), so two writers never lose an update.
Semantics unchanged until consumers migrate: `dataFrom` = earliest date the synced history covers,
`complete` = the table holds everything 1C has from `dataFrom` on. Coverage is written only after
the rows it describes are committed (the Connector sends it after the snapshot's last batch was
acknowledged).

### 4.10 `POST /api/sync/v1/connections/{conn}/status`

`{ "state": "running", "totalCount": 123, "percentage": 40, "lastError": null }` — written to
`connection.raw.syncState` plus `total_count`, `last_error`, `status_updated_at`. It cannot change
company, user, provider, or the connection's `status` column (whose `deleting` value drives the purge
ticker) — unlike Python's PATCH.

### 4.11 `GET /api/sync/v1/connections/{conn}/presence`

`{ "otherConnectorOnline": false, "detail": "0 old-Connector socket(s) on the hub" }` — D-1: any live
socket on the module's own connector hub for the connection or one of its bindings is the old
Connector (the new one opens none, D41). Exact for the single-process module (one per tenant).

## 5. Idempotency

`onec.sync_batch (batch_id text PK, connection_id bigint, op text, body_hash text, result jsonb,
created_at timestamptz)`. Inserted inside the operation's transaction, so a crash leaves either the
whole effect plus the record, or neither. Replays return the stored result. Rows older than 7 days
are removed by a ticker (bounded table). Batch ids are random per logical attempt; a lost answer is
retried with the same id.

## 6. Mapping onto `IBackendSyncTarget`

| Interface | Rust route | Note |
|---|---|---|
| `GetCapabilitiesAsync` | §4.1 | |
| `GetSyncConfigAsync` | §4.2 | `TableListStored = tables != null` |
| `UploadRowsAsync` | §4.3 | `Applied = i+u+n`, `Stale`, `Rejected` from the per-row answer |
| `DeleteRowsAsync` | §4.4 | `Approved` ignored (no cap) |
| `ReconcileRecorderAsync` | §4.5 with `document` omitted | kept for the non-atomic path; Rust always advertises atomic |
| `SyncRecorderAtomicAsync` | §4.5 | **one call for all partitions** (contract change, §7) |
| `PurgeTableAsync` | §4.6 | |
| `ReportStatusAsync` | §4.10 | |
| `GetPresenceAsync` | §4.11 | unreadable → `null` (unknown): the engine waits, never assumes "no old Connector" |
| (new) counts / verification read | §4.7 / §4.8 | used by `sync-verify` and tests |
| (new) coverage | §4.9 | |

HTTP → `Outcome`: 2xx Ok; 400/413-single-row/422 Validation; 401/403 Auth; 409
`connection_deleting` Gone; 409 `partition_deleting` Transient; 409 `batch_id_reused` Validation;
413 split; 429 RateLimited; 5xx/network Transient.

## 7. Connector contract change: multi-partition atomic recorder

`RecorderSync` today is per partition, and the engine calls it once per touched partition — two
transactions when a document's organisation changes. It becomes one call:

```csharp
public sealed record PartitionMovements(string PartitionId, string Table, IReadOnlyList<SyncRow> Rows);
public sealed record RecorderSync(string BatchId, string RecorderKey, long? SourceVersion,
    string DocumentTable, string? DocumentPartition, SyncRow? Document,
    IReadOnlyList<string> Partitions, IReadOnlyList<string> MovementTables,
    IReadOnlyList<PartitionMovements> Movements);
```

`Partitions` = the engine's existing `touch` set (current ∪ history ∪ shared, all partitions when
history is unknown); `MovementTables` = every configured recorded register. The stub implements the
same semantics.

## 8. Counts and verification

- Sync status and first-copy validation use §4.7 (exact) — never the batch answers summed.
- `sync-verify` reads every stored row through §4.8 and compares field by field with a fresh 1C read
  through the engine's own reader and mapper (missing, extra, wrong partition, wrong content).
- `entity_count` (legacy cache) is still adjusted in the same transaction (+inserted, −deleted) so
  legacy readers stay close, but nothing in the new path reads it.

## 9. Writes (built and proven locally 2026-10-08 — R9)

**As built:** `onec.command` (text uuid id, connection, partition, kind, idempotency key unique per
connection, payload + hash, state, attempts, lease, result, error, created_by). Routes:
`POST …/commands` `{kind, idempotencyKey, partition, payload}` → `202 {commandId, state, deduplicated}`
(same key + same request = same command; same key, other request = `409 idempotency_key_reused`);
`POST …/commands/lease` `{max ≤ 10, leaseSeconds 30–3600}` (oldest first; an expired lease is handed out
again; `dead` after 5 attempts); `GET …/commands/{id}`; `POST …/commands/{id}/result`
`{state: succeeded|failed, result, error}` (idempotent). Kinds and payloads are exactly the host's own
write operations: `document.create {docType, body, post}`, `document.update {docType, ref, fields, post,
autoUnpost}`, `document.post|unpost|markDeleted {docType, ref}`, `catalog.create {catalog, body}`,
`catalog.update {catalog, ref, expected, set}` (compare-and-set). Refused before storing: other kinds
(no delete, no procedures), unknown keys (so `exchange`), `_` directives except `_idempotencyMarker`, a
create without the `AIBA_` marker. The Supervisor PULLS (D41: nothing inbound) with
`"commands": true` in sync.json, one command at a time; a transport/timeout/retryable error is left for
the lease, everything else reported with the host's / 1C's own error. The changed object reaches
`entity_data` only through Sync. Pull is a v1 choice, not P-1: the table and states fit a push channel later.

Original design notes (kept):

Normal primitives only: create document, update document, post, unpost, mark-delete, catalog
create, catalog compare-and-set update. Not: arbitrary procedures, `_postCreateMethod`, scripts.

Command model the Rust data/API must allow (nothing in §1–8 blocks it):

- **Durable command row** created before dispatch: `onec.command (command_id uuid PK, connection_id,
  partition, kind, idempotency_key, payload jsonb, payload_hash, expected jsonb /*CAS*/,
  state, result jsonb, error_class, created_at, updated_at)`; states
  `queued → dispatched → running → succeeded | failed | unknown → (reconciling) → dead`; attempts in
  `onec.command_attempt`.
- **Idempotency key** = the `AIBA_<KIND>_<id>` marker the Connector writes into Комментарий; a retry
  with the same key returns the existing 1C object (`idempotentReuse: true`).
- **Target** = connection + partition (organisation) + base key, never a bare oneCId string.
- **Typed result:** `ref`, `number`, `date`, `posted`, `deletionMark`, `idempotentReuse`, plus an
  error class that keeps "never sent" apart from "timed out, may have landed" (`unknown`, resolved by
  looking the marker up in 1C).
- **Async API:** `POST …/commands` → `202 {commandId}`; `GET …/commands/{id}`. The HTTP handler does
  not own the outcome; deadlines grow outward (Connector < backend wait < row lease).
- **Envelope** with room for a signature over the body; routing keys in an outer frame, not inside
  the signed body.
- **Per-action permission**, not one shared secret.
- Explicit `unpostFirst` on mark-delete; CAS update carries `expected` field values and fails with
  `conflict` when 1C differs.
- After a write the Connector's normal Sync brings the object into `entity_data` — the command result
  never writes `entity_data` directly (one source of truth: 1C via Sync).

Open product decision before building: posting / updating documents not created by AIBA (D18 refuses
them; Python posts user drafts).
