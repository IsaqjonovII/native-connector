# A — New .NET Connector → Python backend/1c: actual dependency map

Read-only audit, 2026-10-07. Nothing was called, edited or run against any server.

- .NET side: `D:\aiba\1c-arch` @ `6d0bb2f` (2026-10-03). Paths below are relative to `D:\aiba\1c-arch\src`.
- Python side: `D:\aiba\backend\1c` branch `development` @ `919479b` (2026-10-04). Paths relative to `D:\aiba\backend\1c`.
- Router mounts: `/api/v1` (`main.py:70`), `/api/v2` (`main.py:77`); v1 onec router at `/api/v1/onec` (`app/api/v1/api.py:17`), v2 onec at `/api/v2/onec` (`app/api/v2/api.py:16`), v2 entity at `/api/v2/entity` (`app/api/v2/api.py:22`).

## 0. Where the Python target is actually wired

`PythonMongoSyncTarget` (`OneC.Sync.Targets.Python/PythonMongoSyncTarget.cs:37`) is **only** constructed by test/measurement harnesses:

| Constructor site | Host | Auth | HTTP timeout |
|---|---|---|---|
| `OneC.Supervisor/DevBackend.cs:20-28` | dev only (`aiba-1c-dev.aiba.uz`, `*.aiba.group` refused `:22-23`) | desktop Development session JWT, refresh on 401 via `CloudClient` (`:71-77`) | 5 min (`:27`) |
| `OneC.Supervisor/LocalBackend.cs:24-41` | loopback isolated backend | self-minted HS256 JWT from the instance's test secret (`:44-50`); creates the record with `X-Service-Secret` (`:28-36`) | 5 min (`:27`) |
| `OneC.Supervisor/SyncMode.cs:33-55` | picks stub / `dev` / loopback URL only; anything else refused (`:54`) | — | — |

There is no production wiring: the desktop does not run the engine against backend/1c in prod. The desktop's only prod calls to backend/1c are `OneC.Cloud.CloudClient` (section 2).

## 1. Every HTTP call the engine makes (`IBackendSyncTarget` over v2)

Interface: `OneC.Sync.Abstractions/IBackendSyncTarget.cs:9-45`. Contracts: `OneC.Sync.Abstractions/Contracts.cs`.

Common transport (`PythonMongoSyncTarget.cs:245-264`): `Authorization: Bearer <user JWT>` only (no `X-Device-Id`, no service secret, no company header). One 401 → `RefreshAsync` → one retry (`:258`). Network error → status 0, timeout → status 0 "timeout" (`:253-254`). Status mapping `Classify` (`:277-295`): 2xx Ok; **400 containing "not found" → Transient** (`:287`); 400/422 Validation; 401/403 Auth; 409 "being deleted" → Gone; other 409 → Policy; 429 RateLimited (Retry-After honoured); everything else Transient. `ForPartition` (`:272-275`): Gone from a binding (not the connection) is downgraded to Transient.

Retry above the target: `Uploader` retries Transient/RateLimited up to 3 attempts with 1 s, 2 s backoff, then throws `SyncPausedException`; Auth/Gone/Policy/Validation pause at once (`OneC.Sync/Upload/Uploader.cs:106-136`). Reconcile and delete failures throw `SyncPausedException` directly (`OneC.Sync/Incremental/RecorderHandler.cs:112,141`).

Declared capabilities for v2 (`PythonMongoSyncTarget.cs:42-44`): no per-row results, no atomic recorder, no source-version guard, 34 MB / 10 000 rows per batch, delete cap 0.05, no gzip, `DuplicateKeys = KeepFirstSilently`, `ReportsTrustworthyCounts = false`. Snapshot batch is `min(2000, 10000)` rows (`OneC.Sync/Snapshot/SnapshotRunner.cs:19,207`); recorder movements `min(2000, MaxBatchRows)` after an in-memory key dedupe (`RecorderHandler.cs:147-151`).

### 1.1 Call table

| # | Engine op | Route | Body / query sent | What it parses | Python handler |
|---|---|---|---|---|---|
| C1 | `GetSyncConfigAsync` (1/2) | `GET /api/v2/onec/{connectionId}/org-bindings` | — | `bindings[].{id, orgRef, companyId, status}`; drops `status=="deleting"` (`:52-54`) | `app/api/v2/routes/onec.py:515-524` |
| C2 | `GetSyncConfigAsync` (2/2) | `GET /api/v1/onec/{connectionId}/sync-tables` | — | `tables`: array of `{table}` objects or strings, or `null` → `TableListStored=false` (`:55-65`) | `app/api/v1/routes/onec.py:804-826` |
| C3 | `UploadRowsAsync` | `POST /api/v2/entity/upload` multipart | form `oneCId` = partition id; part `file` named `chunk.json`, `application/json`, body `{"<table>":[row,…]}`; rows carry `__rowKey` (`OneC.Sync/Mapping/CanonicalMapper.cs:34`); no gzip | only `totalInserted + totalSkipped` as `ReportedCount` (`:135`) | `app/api/v2/routes/entity.py:308-458` |
| C4 | `DeleteRowsAsync` | `POST /api/v1/onec/{partition}/prune-missing?scope=self` | `{tableName, missingKeys[], liveCount: 0}` (`:157-161`) | `deleted` | `app/api/v1/routes/onec.py:638-746` |
| C5 | approved delete sizing | `GET /api/v1/onec/{partition}/counts?scope=self` (`:216-222`) | — | `[{tableName, quantity}]` → chunk = `max(1, stored×0.05)` (`:149-154`) | `app/api/v1/routes/onec.py:185-285` |
| C6 | `ReconcileRecorderAsync` | `POST /api/v1/onec/{partition}/reconcile-recorder?scope=self` | `{tableName, recorderRef, liveKeys[]}` (`:173-178`) | `deleted` | `app/api/v1/routes/onec.py:497-623` |
| C7 | `GetPresenceAsync` | `GET /api/v2/onec/{connectionId}` | — | `connection_state`; anything but `offline`/`deleting` = old Connector online; missing = unknown (`:207-213`) | `app/api/v2/routes/onec.py:127-135` + `app/services/connection_state.py:48-82` |
| C8 | `ReportStatusAsync` | `PATCH /api/v2/onec/connection/{id}` | `{status, totalCount?}` (`:194-200`) | status only | `app/api/v2/routes/onec.py:138-168` |
| — | `PurgeTableAsync` | none — returns Policy "no table purge route" (`:188-189`) | | | (no v2 user route exists) |
| — | `SyncRecorderAtomicAsync` | none — throws `NotSupportedException` (`:191-192`) | | | |
| V1 | `SyncVerify` read-back (harness) | `GET /api/v2/entity?oneCId={p}&pageSize=1000&pageNumber=n` (`OneC.Supervisor/SyncVerify.cs:103`) | — | `results[].{tableName, rawData, id}` | `app/api/v2/routes/entity.py:246` |
| V2 | `DevBackend.GuardAsync` | `GET /api/v2/onec/{id}` (`OneC.Supervisor/DevBackend.cs:46`) | — | `name, odataName, connection_state` | as C7 |

Notes on usage:

- **`ReportStatusAsync` is never called by the engine.** No call site in `OneC.Sync` (grep: only the interface, the stub and the target define it). The cloud `status` heartbeat comes from the desktop's `StatusHeartbeat` instead (section 2).
- **Data coverage, stock snapshot, `refids`, `refhashes`, connection rebuild/reset are not called** by the engine. Rebuild is local (`OneC.Sync/Engine/SyncEngineHost.cs:166-186`) and calls `PurgeTableAsync`, which on v2 refuses.
- **Partition identity.** A partition is a backend `oneCId`: the connection's own id is the shared partition, each binding's id is an organisation partition (`PythonMongoSyncTarget.cs:64`, `OneC.Sync/Routing/OrgRouter.cs:24-52`). Reference rows → shared; movement rows → partition bound to the row's lower-cased `orgRef`; unbound org → not sent, counted in `unmapped_orgs`; orgless `IndependentInfoRegister` row → shared (counted); any other orgless movement on a multi-org base → `RowMappingException`. Movement classification is a port of backend's `is_movement_table` (`PythonMongoSyncTarget.cs:69-71`) and `OrgRouter.Validate` refuses disagreement (`OrgRouter.cs:58-82`).
- **`scope` is hard-coded `self`** (`PythonMongoSyncTarget.cs:233`), the D46 fix: the engine reconciles/deletes in every partition the recorder has or had (`RecorderHandler.cs:83-86`, `:134-137`) instead of letting the backend widen.
- **Row identity sent** (`CanonicalMapper.cs:57-69`): `guid` → lower-case `D` GUID from `id`; `code` → `Код`; `natural_key` → `naturalKey`; `recorder_line` → `<lower guid recorderRef>#<lineNo>`. Stamped as `__rowKey`; `dataVersion` removed from the row (`:31-33`). No `__rowHash` is sent by this target. Table names follow `BackendTableNames.Of` (two `_RecordType` suffixes, `OneC.Sync/Source/BackendTableNames.cs:13-20`).
- **Batch handling:** split by 34 MB (`:86-97`); on 413 halve recursively to depth 10 (`:123-130`); a single row still 413 → Validation (dead letter) (`:132-134`).
- **Counts/acks:** `UploadResult.Applied` = rows sent (`:83`), not rows stored. `Uploader` bumps `CountMismatches` when `ReportedCount != rows` (`Uploader.cs:127`) — see finding P1: this can never fire.
- **Delete flow:** non-approved: one prune call with all keys (409 over cap → Policy → `needs_approval` dead letter). Approved: per loop recount via C5, send `max(1, 5 %)` keys, stop when count is 0 (`:146-168`). "nothing stored … refusing to prune" is read as success (`:163`).
- **Recorder flow (v2):** upload document row, upload movement rows per partition, then reconcile every (partition × register) with that partition's live keys; a document that left a partition is deleted there via C4 (`RecorderHandler.cs:99-118`). Deleted document: reconcile with empty live set in every history partition + shared (`:131-143`).
- **Presence (D-1):** checked every `PresenceCheckEvery`; unknown → skip activation, never cached as allowed; online → Paused (`OneC.Sync/Engine/BaseSyncAgent.cs:221-238`).

## 2. What `OneC.Cloud` calls

`OneC.Cloud/CloudClient.cs`. Every call: `Bearer` + `X-Device-Id`; 401 → one refresh (single-flight `SemaphoreSlim`) → one retry, failed refresh signs out (`:137-156`); HttpClient timeout 30 s (`:38`). Bases: prod `https://api.aiba.group/api/v1/` and `https://1c.aiba.group/api/v2/`; dev `api-dev.aiba.uz` / `aiba-1c-dev.aiba.uz` (`OneC.Cloud/CloudEnvironment.cs:13-17`).

| Call | Route | Service | Python handler (backend/1c only) |
|---|---|---|---|
| Login | `POST {api}/auth/login` form `phone, password, is_socket_device=false` (`:53-69`) | main backend | — |
| Names | `GET {api}/user/me` (`:74-83`); userId taken from JWT `sub` (`:84`) | main backend | — |
| Refresh | `POST {api}/auth/refresh` `{refresh_token}` (`:110-134`) | main backend | — |
| Companies | `GET {api}/company/?pageNumber=&pageSize=50&page=&size=50`, ≤100 pages (`:161-176`) | main backend | — |
| 1C records | `GET {1c}/onec?page=&size=50&companyId=` → `items[]`, drops `deleting` (`:179-196`) | backend/1c | `app/api/v2/routes/onec.py:97-103` |
| Create record | `POST {1c}/onec {userId, companyId, name, odataName, provider, version}` (`:200-211`) | backend/1c | `app/api/v2/routes/onec.py:77-94` + `app/utils/onec.py:336-358` |
| Delete record | `DELETE {1c}/onec/connection/{id}`; 404/410 = gone (`:218-226`) | backend/1c | `app/api/v2/routes/onec.py:171-201` → `tasks.purge_onec` `app/core/celery/api.py:2640-2694` |
| Status heartbeat | `PATCH {1c}/onec/connection/{id} {status}` every 60 s for records created here, `inactive` on shutdown (`:229-233`, `OneC.Cloud/StatusHeartbeat.cs:16-32`, caller `OneC.Desktop/MainWindow.xaml.cs:265`) | backend/1c | `app/api/v2/routes/onec.py:138-168` |

`OneCRecord` reads `id/_id, name, odataName, provider, companyId, status, version, connectionId, isShared, connection_state, totalCount, percentage, lastError` (`OneC.Cloud/CloudModels.cs:26-35`). Multi-org records are recognised (`IsMultiOrg`) but "this app does not handle yet" (`CloudModels.cs:9-12`).

## 3. Python handlers in detail

Auth for every route below is `get_current_user` (`app/dependencies/auth.py:45-64`): a valid `X-Service-Secret` returns `{is_service: True}` with **no `id`**; otherwise a JWT (HS256 shared secret, signature + expiry only) with `id = sub`. Then `get_onec_with_ownership` (`auth.py:77-88`): `OneCRepository.get_by_id` returns `None` on **any** exception (`app/repositories/mongodb_repository.py:23-28`) → 400 "OneC connection not found"; access = `str(resource.userId) == user_id` or `check_company_access(resource.companyId)`, which calls the main backend (`app/dependencies/company_access.py:21-60`), caches 300 s in Redis, and returns `[]` on any failure → 403. A service caller fails ownership (user_id None) except where a route special-cases it (`sync-tables`, `POST /onec`).

### 3.1 C3 `POST /api/v2/entity/upload` (`app/api/v2/routes/entity.py:308-458`)

Flow:
1. Ownership on the partition id; **409 if `status=="deleting"`** (`:331-336`). `entitiesPurging` is not checked.
2. Side effects before parsing: Redis `sync-of-infobase:{id}` TTL 120 s (`:344`, `app/services/connection_state.py:144-162`) and `status="running"` written to the partition doc (`:345`) — also on bindings.
3. Whole file read into memory, UTF-8, JSON root object (`:348-362`); tables taken from `{oneCId:{table:[…]}}` or root (`parse_entity_entries` `:89-107`). No allow-list: an unknown table is stored with `entityType = tableName` (`:143-150`); `sync-tables` is not enforced server-side.
4. Per table `process_entity_entries` (`:131-238`): `split_upload_items` pops `__rowKey/__rowHash`, computes `dataHash = sha256(json(item + companyId))` — **mutating `rawData` to include `companyId`** (`app/utils/hash.py:12-15`, `app/utils/entity_dedup.py:174-201`). Keyed rows → `upsert_entities_by_ref`; key-less → hash dedup + `bulk_insert_documents`.
5. `upsert_entities_by_ref` (`app/utils/entity_dedup.py:19-171`): find existing by `(oneCId, tableName, rowKey $type string $in)` (`:59-73`); bootstrap match of key-less legacy rows by `dataHash` (`:84-93`); build ops; **`bulk_write(ops, ordered=True)`** for updates/collapses (`:162-163`, not caught — a BulkWriteError mid-way leaves earlier ops applied and returns 500), then **`insert_many(inserts, ordered=False)` with E11000 swallowed** (`:164-169`). Counters are computed **before** the write (`inserted += 1` at `:135`), so a swallowed duplicate is still reported inserted.
6. Hash path: `bulk_insert_documents` batches 500 (`entity.py:73-86`) → `EntityDataRepository.bulk_create` (`app/repositories/mongodb_repository.py:282-343`) `insert_many(ordered=False)`; on E11000 it **returns `len(entities)`** — the whole batch counted inserted (`:338-342`).
7. Optional projections `register_lines` / `doc_rows` (env-flagged, never fail the upload) (`entity.py:201-220`).
8. `apply_upload_deltas`: `$inc` EntityCount by `inserted − collapsed` (`app/utils/entity_counts.py:27-62`) — uses the over-reported numbers.
9. If anything changed: debounced `tasks.check_one_onec_cache` (`entity_counts.py:88-120`) and report-cache invalidation (`entity.py:414-428`).
10. Response (`:441-450`): `results[{tableName, entityType, total, inserted, updated, collapsed, skipped = total − inserted − updated}]`, `totalItems`, `totalInserted = Σ(inserted+updated+collapsed)`, `totalSkipped = totalItems − totalInserted`.

Transactions: none. Tables in one file are processed sequentially and independently; a failure in table N leaves tables 1..N−1 written and returns 500. A non-dict row raises inside hashing → 500 for the whole chunk.

Indexes (`app/models/mongodb_models.py:257-302`, created by `init_beanie` `app/core/mongodb.py:122`):
- `oneCId_1_tableName_1_rowKey_1` unique, partial `rowKey $type string` (`:259-264`);
- `oneCId_1_tableName_1_dataHash_1` **unique, non-partial** (`:278-282`) — two distinct keyed rows with identical content collide, the second is silently dropped (the model comment says so, `:265-277`);
- `oneCId_1_tableName_1_refId_1` non-unique partial (`:286-290`), plus single-field indexes. No `(oneCId, tableName)` index by design (`:233-249`).
- `oneCId` stored as ObjectId on new rows and string on legacy rows; every read must query both forms.

### 3.2 C4 `POST /api/v1/onec/{id}/prune-missing` (`app/api/v1/routes/onec.py:638-746`)

- Empty key list → 200 `{deleted:0, skipped:"nothing to prune"}` (`:670-672`).
- `scope=self` → `[path id]`; `scope=connection` → all non-deleting partitions of the base (`:42-75`, `app/utils/onec_scope.py:144-178`); `connection` on a binding → 400 (`:64-73`).
- Denominator: **live** `count_documents` over the scope (`:690-691`). 0 → 409 "nothing stored … refusing to prune" (`:693-697`). Over `max(1, int(stored×0.05))` → 409 (`:699-708`).
- Match `$or` of rowKey / refId / **`rawData.id` (unindexed)** across the `$or` oneCId forms (`:710-722`); `dryRun` counts only (`:724-733`); `delete_many` (`:735`). No transaction, no EntityCount decrement, no cache invalidation. `liveCount` is echoed only.

### 3.3 C6 `POST /api/v1/onec/{id}/reconcile-recorder` (`onec.py:497-623`)

- Required `recorderRef` (`:528-530`). Scope as above. Range `[ref#, ref$)` with `$type: string` (`:585`) per (oneCId form × rowKey/refId) → 4 `delete_many` calls (`:599-616`), excluding `liveKeys` with `$nor` (`:587-597`). Empty `liveKeys` deletes every row of the recorder in that table. No cap, no transaction, no EntityCount decrement.
- **Org-partition semantics:** under `scope=connection` the exclusion is base-wide: a row in partition A survives if its key is live anywhere, and a partition-specific live set deletes the other partitions' rows (the D46 bug). Under `scope=self` it touches exactly one partition.
- Key range is byte-exact: upper-case GUIDs or old `Регистратор#НомерСтроки` keys are not addressed.

### 3.4 C5 `GET /api/v1/onec/{id}/counts` (`onec.py:185-285`; v2 twin `app/api/v2/routes/onec.py:288-367`)

- Fast path: sum of `EntityCount.counts` per partition (`:217-227`); union with the provider's expected tables, which report 0 (`:198-205`).
- Cold path: per-table `count_documents` per oneCId form, concurrently; self-warms EntityCount only under `self` (`:235-283`).
- The maintained map moves only by upload deltas (over-reported, section 3.1) and the 6-hourly `retruth_entity_counts` (`app/core/celery/app.py:356-365`). Prune and reconcile never decrement it; the backend admits this itself (`app/api/v2/routes/onec.py:563-567`). The comment "backend counts only grow between refreshes" (`onec.py:209-212`) is false.

### 3.5 C1 `GET /api/v2/onec/{id}/org-bindings` (`app/api/v2/routes/onec.py:515-524`)

Ownership on the connection; `OneC.find({connectionId, status != deleting})` (`:432-444`). Returns `{connectionId, isShared, bindings[{id, orgRef, orgName, orgCode, companyId, status}]}`. Called with a binding id it returns an empty list (bindings of a binding). orgRef is stored lower-case (`normalize_org_ref` `:379-395`). PUT (`:606-732`) is whole-set replace with uniqueness checks both directions; remap `update_many` of `EntityData.companyId` only (`:688-692`), not `rawData.companyId` / `dataHash`; removal moves `orgRef → orgRefRemoved` and fires `tasks.purge_onec` (`:478-512`).

### 3.6 C2 `GET /api/v1/onec/{id}/sync-tables` (`onec.py:804-826`)

Service secret skips ownership (`:777-801`). Returns `{"tables": stored if stored else None}` (`:825`): **`[]` and "never set" both answer `null`**, contradicting the route's own docstring (`:806-820`). PUT (`:829-842`) full replace, first-wins dedupe, drops unknown fields such as `schedule`.

### 3.7 C7 / C8 `GET` and `PATCH /api/v2/onec/...`

- `GET /{id}` (`:127-135`): ownership; `connection_state` from Redis (`connection_state.py:48-82`): `deleting` → deleting; no route key → **offline**; sync key → syncing; `running` → syncing; else writable. Redis failure → every base **offline** (`connection_state.py:108-111`). The route key is set by the connector WebSocket, which has no authentication (`main.py:91-101`).
- `PATCH /connection/{id}` (`:138-168`): 409 if currently `deleting`; applies `OneCUpdate` verbatim (`app/schemas/mongodb_schemas.py:19-30`): `userId`, `companyId`, `provider`, `odataName`, `name`, `version`, `totalCount`, and **`status` including `deleting`** (`:29`). No check on the new company; existing rows are not re-stamped.

### 3.8 Cloud routes

- `GET /api/v2/onec` (`:97-103`): `OneCRepository.list(company_id=company_id)` — **no user filter**; with no `companyId` it is `find_all()` (`mongodb_repository.py:137-170`). Any valid JWT lists every connection fleet-wide, or any company's. (v1 passes `user_id`, `app/api/v1/routes/onec.py:132`.)
- `POST /api/v2/onec` (`:77-94`, `app/utils/onec.py:336-358`): only `userId == sub`; **no check that the caller may act for `companyId`**; uniqueness is check-then-insert (no DB unique index on `(companyId, odataName, name)` — `mongodb_models.py:183` is non-unique).
- `DELETE /connection/{id}` (`:171-201`): sets `deleting`, fires `tasks.purge_onec`: chunked delete of `{"oneCId": ObjectId}` only (`app/core/celery/api.py:2663`), then `entity_counts`/`cached_metrics`, then the OneC doc (`:2678`). **String-form legacy rows are never purged**; **bindings of a deleted connection are not purged** (no `connectionId` cascade anywhere in the task). Backstop re-drives anything `status=="deleting"` older than 2 min (`api.py:2765-2795`).

### 3.9 Not called by the engine but in the contract the old Connector uses

| Route | Handler | Behaviour |
|---|---|---|
| `POST /api/v2/entity/data-coverage` | `entity.py:565-601` | read-modify-write merge into `OneC.dataCoverage` (`:581-594`), lost-update race; per SECOND_PASS §6 it gates avtoprovodka duplicate prevention |
| `POST /api/v2/entity/stock-snapshot` | `entity.py:516-540` | delete all `STOCK_BALANCE` of the oneCId (`:467-473`), then insert in 500s; not atomic; empty `rows` wipes; same `bulk_create` over-report |
| `GET /api/v1/onec/{id}/refids`, `/refhashes` | `onec.py:288-354`, `:357-480` | unpaged partition streams; refhashes sorts by `updatedAt` unindexed |
| `DELETE /api/v2/entity/oneC/{id}` | `entity.py:604-634` | `entitiesPurging` + `tasks.purge_onec_entities` (ObjectId-only, `api.py:2727`) |
| `POST /api/v2/onec/connection/rebuild`, `/backfill-total-count` | `app/api/v2/routes/onec.py:234-285` | **no auth**; rebuild without `oneCId` deletes all `CachedMetric`+`EntityCount` |

## 4. Findings already recorded in 1c-arch (do not re-derive)

| Doc | What it already records |
|---|---|
| `SYNC_ENGINE_ARCHITECTURE.md` §18 (595-612) | v2 call table; "a matching total does not prove every row was stored"; concurrent writers lose silently; counts drift; no doc+movement transaction; `rawData.companyId`/`dataHash` not updated on remap; `scope=self` everywhere (D46); `_RecordType` names; sync-tables objects/null; 400 "not found" retried; first sync of re-keyed tables needs a rebuild (D-3) |
| `SYNC_ARCHITECTURE_AUDIT.md` §10 (342-372) | full endpoint table incl. upload 409 but not `entitiesPurging`; non-dict row = 500; stock not atomic; data-coverage lost-update; reconcile/prune do no count update; PATCH can change `userId/companyId/provider`; entity purge ObjectId-only; WS no auth; service secret has no user id; company access cached 300 s, `[]` on failure |
| `SYNC_ARCHITECTURE_AUDIT.md` §11, §20 | backend does not route by `orgRef`; RS-3 orgless register rows land in shared; B-1 v1 upload always 500; B-2 `tasks.create_entities` missing; B-3 unauth rebuild; B-4 gzip mismatch |
| `BACKEND_SECURITY_FINDINGS.md` | F-1 unauth `/connection/rebuild` (fleet wipe), F-2 unauth `/backfill-total-count`; over-reported inserts noted as correctness |
| `MIGRATION_STATUS.md` known limits (485-486) | "v2 counts a row as inserted before writing it and swallows a duplicate-key error" (pinned 2 reported / 1 stored); no per-table purge → register rebuild refused |
| `DECISIONS.md` D46 (920-950) | the multi-org `scope=connection` deletion bug, reproduced and fixed client-side; `totalSkipped = totalItems − totalInserted` |
| `OLD_CONNECTOR_SECOND_PASS.md` §10-11 | old Connector does not have the multi-org bug (reads once, sends base-wide keys with `scope=connection`); `[]`-vs-`null` sync-tables bug confirmed with two workarounds in other codebases; `schedule` dropped on PUT; data coverage is accounting-load-bearing |

## 5. New findings from this pass

| ID | Finding | Evidence |
|---|---|---|
| P1 | The engine's `CountMismatches` metric is a tautology on v2: `ReportedCount = totalInserted + totalSkipped = totalItems`, always equal to rows sent. It can never fire, so it is not even a partial hint (docs call it "a hint") | `PythonMongoSyncTarget.cs:135`, `Uploader.cs:127`, `entity.py:448` |
| P2 | Hash-path inserts: on any E11000 `bulk_create` returns `len(entities)` — a whole 500-row batch reported inserted even if most rows were dropped. Separate from the keyed-path over-report already recorded | `mongodb_repository.py:338-342` |
| P3 | Keyed path: `bulk_write(ordered=True)` is not caught; a unique-index collision on an update (`dataHash` unique non-partial) raises after earlier ops applied → 500 with partial state | `entity_dedup.py:162-163`, `mongodb_models.py:278-282` |
| P4 | Over-reported inserts feed `EntityCount` via `$inc`, and deletes never decrement it. Interaction with the engine: approved-delete chunk size is computed from this drifting counter (C5) while the backend caps with a **live** count → chunk can exceed the real cap → 409 → approved delete returns Policy mid-way | `entity_counts.py:41-60`, `PythonMongoSyncTarget.cs:149-154`, `onec.py:691-708` |
| P5 | Presence fails open: Redis down → every base `offline` → engine thinks no old Connector is online and syncs next to it (D-1 defeated). The route key itself is self-declared over an unauthenticated WS | `connection_state.py:108-111`, `:76-77`, `main.py:91-101`, `PythonMongoSyncTarget.cs:211-212` |
| P6 | Main-backend outage → `check_company_access` returns `[]` → 403 for every non-owner member → engine classifies Auth and **pauses the base** for a transient upstream failure | `company_access.py:47-60`, `auth.py:67-74`, `PythonMongoSyncTarget.cs:290` |
| P7 | `GET /api/v2/onec` has no user filter: any valid JWT lists all connections fleet-wide, or any company's (`OneC.Cloud.OneCListAsync` relies on it) | `app/api/v2/routes/onec.py:97-103`, `mongodb_repository.py:137-170` |
| P8 | `POST /api/v2/onec` does not check company access: a user can create a connection for any `companyId`, and by-company resolution (`get_by_company_id`, `/by-company`) could then pick it up | `app/utils/onec.py:336-358`, `app/api/v2/routes/onec.py:106-124` |
| P9 | `PATCH` accepts `status: "deleting"` → the 2-minute backstop runs `purge_onec` on it: a hidden delete path with no 202/delete-status contract | `mongodb_schemas.py:29`, `api.py:2789-2795` |
| P10 | Deleting a multi-org connection purges only its own partition (ObjectId form); its bindings stay with a dangling `connectionId` and their rows stay | `api.py:2655-2678`, `app/api/v2/routes/onec.py:171-201` |
| P11 | Upload writes `status="running"` and the sync key on whatever partition it targets, bindings included, and does not check `entitiesPurging` (rows uploaded during an entity purge are deleted or survive by race) | `entity.py:331-345` |
| P12 | Upload accepts any table name (no allow-list against `sync-tables` or provider mappings); `sync-tables` is advisory | `entity.py:143-150` |
| P13 | `get_by_id` swallows every exception as "not found" (400), so a Mongo outage looks like a missing connection; the engine has to retry 400s by text match | `mongodb_repository.py:23-28`, `PythonMongoSyncTarget.cs:287` |
| P14 | `prune-missing` matches `rawData.id` with no index, inside an `$or` across oneCId forms — the deletion query can scan the partition; also deletes by `rawData.id` regardless of the row's real key | `onec.py:710-722` |
| P15 | `ReportStatusAsync` is implemented but never called; the target also sends engine states verbatim, which the PATCH pattern (`active|inactive|running|deleting`) would 422 | `PythonMongoSyncTarget.cs:194-200`, `mongodb_schemas.py:29` |
| P16 | `rawData` is mutated to include `companyId` before hashing and storage, so content hash and stored row both depend on the company, and a remap leaves stale `rawData.companyId` | `hash.py:12-15` |

## 6. Python behaviours the Rust backend MUST NOT reproduce

Acks and identity
1. Counting a row as stored before the write, and swallowing E11000 (keyed path `entity_dedup.py:135,164-169`; hash path `mongodb_repository.py:338-342`). Return per-row results; a duplicate in a batch is a named rejection or a deterministic last-wins, never silent.
2. `totalSkipped = totalItems − totalInserted` (`entity.py:448`): a response whose totals always add up proves nothing.
3. Unique content-hash index that drops distinct keyed rows with equal content (`mongodb_models.py:278-282`). Identity is the key; content hash is metadata only.
4. Mutating the client row (`hash.py:13`) and salting hash/identity with `companyId`.
5. Dual `oneCId` storage (string + ObjectId) that forces every query to `$or` and lets purges miss rows (`api.py:2663`). One typed partition id.
6. No version guard: concurrent or retried older writes overwrite newer ones. Accept a monotonic `sourceVersion` and refuse stale rows (the engine already produces one, `CanonicalMapper.cs:47-54`).

Transactions and partial state
7. No transaction across tables in one upload, across ordered bulk ops, between a document and its movements, or for stock delete-then-insert (`entity.py:510-512`). Batch = one transaction; atomic recorder sync.
8. Side effects (status `running`, sync key) written before the payload is validated (`entity.py:344-345`).

Deletes and scope
9. `scope=connection` that widens a delete to every partition while keeping one global live set (`onec.py:42-75`, `:599-616`). Deletes/reconciles must be per named partition, or carry the live set per partition.
10. Reconcile with no cap and byte-exact key ranges that miss case variants and legacy keys (`onec.py:585`). Normalise keys on write; reject non-canonical keys.
11. Prune cap measured on a live count while clients size chunks from a drifting cached counter (P4); deletes that do not update counts.
12. Delete-by-`rawData.id` (`onec.py:718`): delete by key only.
13. No register purge / table rebuild route for the syncing principal (`PythonMongoSyncTarget.cs:184-189`). Provide an authenticated, scoped `purge(partition, table)`.
14. Connection delete that leaves bindings and legacy rows behind (P10, `api.py:2663`); hidden delete via `PATCH status=deleting` (P9).

Counts and coverage
15. `EntityCount` maintained by `$inc` of over-reported deltas, never decremented, re-truthed every 6 h (`entity_counts.py`, `app.py:356-365`). Counts must come from the same transaction as the write.
16. Data coverage as a read-modify-write merge on the connection document (`entity.py:581-594`): lost updates. Per-table rows with conditional update.

Auth and tenancy
17. Unauthenticated destructive/maintenance routes (`app/api/v2/routes/onec.py:234-285`, F-1/F-2).
18. List with no user filter (P7) and create with no company check (P8).
19. `PATCH` that can reassign `userId`/`companyId`/`provider` without checks or re-stamping rows (`mongodb_schemas.py:19-30`).
20. Service-secret principal with no identity, making ownership checks deny by accident and routes special-case it (`auth.py:50-51`, `onec.py:777-801`). Use explicit service principals with explicit grants.
21. Access check that fails closed on upstream outage as 403 (P6) and caches grants 300 s; distinguish "denied" from "could not check" (503).
22. Presence that fails open (Redis down = offline) and is self-declared over an unauthenticated socket (P5).
23. Swallowing DB errors into 400 "not found" (P13).

Config model
24. Thin `sync-tables` model: list of `{table, reports}` on the connection doc, `[]` collapsed to `null` (`onec.py:825`), unknown fields (`schedule`) dropped, not enforced on upload (P12). Needs an explicit stored/unset state, enforcement, and the movement/reference family stored per table instead of a name-prefix rule duplicated in two codebases (`onec_scope.py:66-83` vs `PythonMongoSyncTarget.cs:69-71`).
25. Movement classification that files every `InformationRegister_*` as per-org, pushing orgless independent rows into a shared partition no company reads (`onec_scope.py:66-71`, RS-3).
26. Whole-file in-memory multipart JSON with no gzip and no app-level size limit (`entity.py:348`); 1 MB part limit unless a filename is given (`PythonMongoSyncTarget.cs:120`).
