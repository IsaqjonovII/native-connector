# D — backend/1c ↔ Connector contract (read-only audit)

## 0. What was read

| Item | Value |
|---|---|
| Repo | `D:\aiba\backend\1c` |
| Local branch | `development` @ `8107bc0` ("internal: serve bank accounts, operation kinds and document metadata over HTTPS") |
| Remote | `origin/development` @ `b2e3590` — **58 commits ahead** of local, 0 behind |
| **Source of truth used** | **`origin/development` (b2e3590)**, read via `git show`/`git grep`. Local working tree is stale and has uncommitted edits in `app/api/v2/routes/reconciliation.py`, `app/utils/probe_hash.py` (ignored). |
| Connector cross-check | `D:\aiba\connector` branch `release` @ `cfab3a1` (= `origin/release`), `apps/app/src` |
| Also cross-checked | `D:\aiba\aiba-next\backend` (only for who calls sync-tables/manifest) |

All file:line references below are `origin/development` for backend paths (`app/...`, `main.py`) and connector `release` for connector paths (`src/...` = `D:\aiba\connector\apps\app\src\...`).

Connector base URL: `NEXT_PUBLIC_1C_API` = `https://1c.aiba.group/api/v2` (prod), `https://aiba-1c-dev.aiba.uz/api/v2` (dev) (`apps/app/.env.production:9-10`). Connector rewrites `/api/v2` → `/api/v1` for counts/refids/refhashes/reconcile/prune/sync-tables (`src/utils/backend-refids-fetcher.ts:11`, `src/utils/backend-counts-fetcher.ts:20`, `src/utils/sync-tables-config-fetcher.ts:18`). 1uz bases go to a separate clone backend (`src/core/config.ts:165-176`, `resolveOnecApiUrl` :246).

---

## 1. Auth model (code)

| Mechanism | Where | Behaviour |
|---|---|---|
| `get_current_user` | `app/dependencies/auth.py` | Accepts `X-Service-Secret` == `SERVICE_SECRET_KEY` → `{"is_service": True}` **with no `id`**; else Bearer JWT (HS, `JWT_SECRET_KEY`) → `user["id"]=sub`. |
| Ownership | `get_onec_with_ownership` / `verify_ownership` | `resource.userId == user.id` OR company access via `GET {AIBA_API_URL}/api/v1/internal/user-accessible-companies/{user}` (Redis cache `accessible_companies:{user}`, 300s) (`app/dependencies/company_access.py`). A **service caller has no id → denied** by every route that calls this without an `is_service` branch. |
| `verify_service_key` / `verify_write_caller` | `app/api/internal/dependencies.py` | `X-Service-Secret` must equal ANY of `SERVICE_SECRET_KEY`, `ODOO_API_TOKEN`, `MCP_API_TOKEN`, `AIBA_BI_API_TOKEN` (timing-safe). All four are full peers on **all** `/api/internal/admin/*` incl. write pipeline. |
| None | `/api/internal/connector/*`, `/api/v2/connector/ws`, `/ws/1c`, `/api/v2/connector/ping`, `/health*`, `/`, `POST /api/v2/onec/connection/backfill-total-count`, `POST /api/v2/onec/connection/rebuild` | see §9 |

Mounts (`main.py`): `/api/v1` (v1), `/api/v2` (v2), `/api/internal/admin` (internal, hidden from schema), `/api/internal/connector` (connector_router, **no auth**), `/api/v2/connector` (WS hub), `/ws/1c` (legacy cloud router), `/api/ai`, health.

---

## 2. Route inventory with Connector usage

Legend: **ACTIVE(C)** = connector caller found; **ACTIVE(o)** = other caller known (kansler/aiba-next/cloud-os/admin) or self-described; **UNUSED(C)** = no connector caller; **DEAD** = no caller anywhere found and/or superseded.

### 2.1 OneC connection records — v2 (`app/api/v2/routes/onec.py`)

| Method Path | Auth | Does | Status / caller |
|---|---|---|---|
| POST `/api/v2/onec` (:77) | JWT/svc | `validate_onec_create` (userId must equal caller unless service; 409 on same companyId+odataName+name), insert OneC. Stamps `companyInn` from backend directory on insert (`mongodb_models.py:162`). **No company-access check on `companyId`.** | ACTIVE(C) `src/features/accounting/lib/use-info-base-form.ts:339` (also :248 for 1uz backend) |
| GET `/api/v2/onec?companyId=` (:97) | JWT/svc | `OneCRepository.list(company_id)` — **no user filter, no ownership check**; without companyId returns the **whole fleet**. Adds `percentage` + `connection_state`. | ACTIVE(C) `src/utils/info-base.ts:82` (paged), `use-info-base-form.ts:65` |
| GET `/api/v2/onec/by-company/{company_id}` (:106) | JWT | `LIVE_BY_COMPANY` (not shared, not deleting), ownership. | UNUSED(C) (only `scripts/adapter-tests/kan-multiorg-e2e.ts:101`) — used by cloud side |
| GET `/api/v2/onec/{id}` (:127) | JWT | ownership; `importedCount` from EntityCount; state annotate | ACTIVE(C) `src/utils/info-base.ts:391` |
| PATCH `/api/v2/onec/connection/{id}` (:138) | JWT | ownership; **409 if status=deleting**; applies `OneCUpdate` (status ∈ active/inactive/running/deleting, version, odataName, totalCount...) verbatim | ACTIVE(C) heartbeat `src/providers/connection-status.tsx:126` (`{status,totalCount}`), metadata `src/utils/info-base.ts:258` (adapterDbName/odataName/version), edit form `use-info-base-form.ts:537`, `setConnectionRunning` from `sync-orchestrator.ts:691,979` |
| DELETE `/api/v2/onec/connection/{id}` (:171) | JWT | status→`deleting`, send `tasks.purge_onec`; 202 | ACTIVE(C) `src/app/(dashboard)/accounting/page.tsx:149` |
| GET `/api/v2/onec/connection/{id}/delete-status` (:204) | JWT (only if doc still exists) | live EntityData remaining (ObjectId form only) | UNUSED(C) |
| POST `/api/v2/onec/connection/backfill-total-count` (:234) | **NONE** | one-time fleet backfill of `totalCount` | DEAD (one-off) — unauthenticated write |
| POST `/api/v2/onec/connection/rebuild?oneCId=` (:255) | **NONE** | deletes CachedMetric+EntityCount for one id **or the whole fleet** and enqueues rebuild | UNUSED(C); **unauthenticated fleet-wide cache wipe** |
| GET `/api/v2/onec/{id}/counts?scope=self|connection` (:288) | JWT | per-table counts: fast path = SUM of `EntityCount.counts` over scope; cold path = live count per stored oneCId form, self-warm only under scope=self. Union of provider tables (all aliases + Meta1C tables) ∪ stored tables. | UNUSED via v2 by connector (connector calls the **v1** twin) |
| GET `/api/v2/onec/{id}/org-bindings` (:515) | JWT | live bindings (`connectionId`=id, status≠deleting) | ACTIVE(C) `src/features/accounting/api/org-binding.service.ts:138` |
| PUT `/api/v2/onec/{id}/org-bindings` (:606) | JWT | whole-set replace; orgRef normalized lowercase GUID, rejects zero-GUID, dup org, 2 orgs→1 company; verifies each companyId (own connection or company access); create binding docs (status inactive, copies provider/odataName/userId/source); update moves companyId AND `update_many` re-stamps EntityData.companyId; remove → `_delete_binding` (status deleting, orgRef→orgRefRemoved, purge task); sets `isShared = bool(bindings)`; returns `warnings` for stranded movement rows in shared partition | ACTIVE(C) `org-binding.service.ts:169` |
| DELETE `/api/v2/onec/{id}/org-bindings/{bid}` (:735) | JWT | single unmap + purge | UNUSED(C) |

### 2.2 OneC — v1 (`app/api/v1/routes/onec.py`) — the connector's sync-diff surface

| Method Path | Auth | Does | Status / caller |
|---|---|---|---|
| POST `/api/v1/onec` (:78) | JWT/svc | legacy create (same checks) | UNUSED(C) |
| GET `/api/v1/onec` (:122) | JWT | list **filtered by userId** (v1 is stricter than v2!) | UNUSED(C) (e2e script only) |
| GET `/api/v1/onec/{id}` (:139) | JWT | ownership | UNUSED(C) |
| PATCH/DELETE `/api/v1/onec/connection/{id}` (:148/:166) | JWT | PATCH has deleting guard; DELETE is **inline** (gather delete EntityData/EntityCount/CachedMetric + OneC) — the old synchronous path | UNUSED(C) |
| GET `/api/v1/onec/{id}/counts` (:185) | JWT | identical to v2 counts | **ACTIVE(C)** `src/utils/backend-counts-fetcher.ts:32` (always `scope=connection`) |
| GET `/api/v1/onec/{id}/refids?table_name=` (:288) | JWT | every `rawData.id ?? rawData.Ref_Key ?? refId` for (scope, table) | **ACTIVE(C)** `src/utils/backend-refids-fetcher.ts:28` |
| GET `/api/v1/onec/{id}/refhashes?table_name=&digest=&buckets=` (:379) | JWT | `{rowKey: rowHash}` (+ `probeHashes` computed server-side from projected rawData fields; `digest=1` → `bucketDigests` only; `buckets=aa,bf` → subset). Newest-wins by (updatedAt,_id) in Python, no Mongo sort. `count` = full stored count even in digest mode. | **ACTIVE(C)** `backend-refids-fetcher.ts:60` (full), `:152` (digest), `:189` (buckets) |
| POST `/api/v1/onec/{id}/reconcile-recorder` (:553) | JWT | body `{tableName, recorderRef, liveKeys[]}`; deletes rows of ONE recorder whose rowKey/refId in range `[ref#, ref$)` not in liveKeys; empty liveKeys deletes all that recorder's rows | **ACTIVE(C)** `backend-refids-fetcher.ts:97` (from `register-reconcile.ts:23`) |
| POST `/api/v1/onec/{id}/prune-missing` (:694) | JWT | body `{tableName, missingKeys[], liveCount, dryRun}`; refuses 409 if stored=0 or keys > 5% (`PRUNE_MAX_FRACTION`) of **stored** count in scope; matches rowKey/refId/rawData.id | **ACTIVE(C)** `backend-refids-fetcher.ts:235` |
| GET `/api/v1/onec/{id}/sync-tables` (:860) | JWT/svc | `{oneCId, tables: syncTables or null}` | **ACTIVE(C)** `src/utils/sync-tables-config-fetcher.ts:46` |
| PUT `/api/v1/onec/{id}/sync-tables` (:885) | JWT/svc | full replace, `_clean_sync_tables` keeps only `{table, reports}` | **ACTIVE(C)** `sync-tables-config-fetcher.ts:81` |

`scope=connection` (`_scoped_onec_ids`, v1 :42): refused (400) on a binding; else `connection_partition_ids` = connection + all bindings (`app/utils/onec_scope.py:164`).

### 2.3 Entity upload & feature ingest — v2 (`app/api/v2/routes/entity.py`)

| Method Path | Auth | Does | Status / caller |
|---|---|---|---|
| GET `/api/v2/entity?oneCId=&entityType=` (:246) | JWT/svc | **unpaged `to_list()` then paginate in memory** | UNUSED(C) |
| GET `/api/v2/entity/{id}` (:279) | JWT/svc | | UNUSED(C) |
| POST `/api/v2/entity` (:291) | JWT | Celery `tasks.create_entities(oneCId, companyId, filename)` (MinIO file import) | UNUSED(C) |
| **POST `/api/v2/entity/upload`** (:308) | JWT | see §4 | **ACTIVE(C)** `src/features/accounting/lib/sync/chunk-uploader.ts:229` |
| POST `/api/v2/entity/stock-snapshot` (:565) | JWT | `{oneCId, rows[]}` → full replace of STOCK_BALANCE (tableName `stock`) behind a Mongo lease + fingerprint "unchanged" skip | ACTIVE(C) `src/features/accounting/lib/sync/stock-sync.ts:105` |
| POST `/api/v2/entity/data-coverage` (:614) | JWT | `{oneCId, tables:[{table,dataFrom,complete}]}` MERGED into `OneC.dataCoverage` | ACTIVE(C) `src/features/accounting/lib/sync/coverage-sync.ts:53` |
| DELETE `/api/v2/entity/oneC/{id}` (:653) | JWT | `entitiesPurging=True`, `tasks.purge_onec_entities` (keeps connection) | ACTIVE(C) `src/features/accounting/api/info-base.service.ts:44` |
| GET `/api/v2/entity/oneC/{id}/delete-status` (:686) | JWT | remaining + done | ACTIVE(C) `info-base.service.ts:70` |
| v1 `/api/v1/entities` GET/POST/upload/DELETE (`app/api/v1/routes/entity.py`) | JWT | legacy twins (v1 DELETE is inline) | UNUSED(C) |

### 2.4 Reports / statistics / reconciliation / QQS (read synced data)

| Path | Auth | Status |
|---|---|---|
| `/api/v2/statistics/*` (40+ GETs, `statistics.py`) | JWT + company ownership (via util) | UNUSED(C); cloud UI |
| `/api/v1/statistics/{main,sales,expenses,profit,cash}/*` | JWT | UNUSED(C); legacy |
| GET `/api/v1/statistics/cache/{company_id}` (`v1/routes/statistics/cache.py:55`) | JWT, **no ownership check** | UNUSED(C) |
| DELETE `/api/v1/statistics/cache/clear-all` (cache.py:157) | JWT, **no ownership/admin check** | UNUSED(C) — see Security |
| POST `/api/v1/statistics/cache/fix-ownership` (cache.py:271) | JWT, no admin check | fleet-wide `update_many` | DEAD/one-off |
| `/api/v2/reconciliation/{counterparties,contracts,document-contracts,summary,detail/{name},document/{no}}` | JWT | UNUSED(C); sverka |
| `/api/v2/qqs/{summary,outgoing,incoming,by-invoice}` | JWT | UNUSED(C) |
| GET `/api/v2/reports`, `/{key}`, POST `/{key}/refresh`, GET `/jobs/{id}` (`reports.py`) | JWT/svc (svc skips ownership) | UNUSED(C); refresh **causes a connector pull** (§6) |
| POST `/api/v2/reports/retail-rollup/ingest` (reports.py:326) | JWT/svc | replaces `[dateFrom,dateTo]` window of `retail_daily_rollup` for one oneCId | ACTIVE(C) `src/features/accounting/lib/sync/turnover-push.ts:141` |
| `/api/v2/cloud-onec/*` (`cloud_onec.py`: connections CRUD, clobus discover/connect, sync, SSE stream) | JWT | UNUSED(C) — direct OData pull path, no connector |

### 2.5 Internal admin (`/api/internal/admin/*`, all `verify_service_key`/`verify_write_caller`)

None are called by the connector (grep: only the e2e script). Callers are kansler/aiba-next, cloud-os avtoprovodka, backend/main, Odoo, MCP.

| Prefix / route | Does | Connector relevance |
|---|---|---|
| `/onecs` GET list (`internal/routes/onec.py:26`), `/by-inn`, `/resolve-by-inns`, GET/PATCH/DELETE `/{id}` | fleet admin. **`model_dump()` returns the full doc incl. `login`, `baseUrl`, `passwordEncrypted`, `syncTables`, `extraTables`, `watermarks`, `dataCoverage`** | reads connection records |
| PUT `/onecs/{id}/extra-tables` (:194) | replace `extraTables` | feeds `/manifest` only (dead for connector, §5) |
| `/connectors` GET (`connectors_admin.py:42`), GET `/infobase/{id}` | Redis presence listing | reads hub state |
| POST `/connectors/infobase/{id}/sync?mode=cursor|full` (:69) | `dispatch_to_infobase(..,"sync_now",{mode})` | **command → connector** (handled `src/providers/onec-sync-provider.tsx:14925`) |
| POST `/connectors/infobase/{id}/read-table` (:85) | `pull_now {tables, priority}` | **command → connector** |
| POST `.../document/post` (:168) | `onec_post.post_existing_document` → WS `post_1c_document` or OData for cloud | command |
| POST `.../document/unpost|update|delete`, `.../catalog/update` (CAS via capability `catalog_update_cas`), `.../documents/read`, `.../catalog/read` | WS `unpost_1c_document`, `update_1c_document`, `delete_1c_document`, `catalog_update`, `document_read`, `catalog_read`; capability gate via `connector_supports` | commands; connector advertises them in hello (`onec-sync-provider.tsx:1719-1745`) |
| `/entity-data/{employees,by-type,last-by-counterparty,stats,{id}}`, DELETE `/{id}` | read synced rows (by-type is the general catalog/document reader incl. STOCK_BALANCE) | reads connector data |
| `/entity-counts`, `/cached-metrics` (+ DELETE, fix-ownership), `/stats` | admin views | |
| `/resolve-refs`, `/build-write-payload`, `/cloud-write`, `/cloud-write-fact`, `/odoo-inbox` (POST, GET `/{odoo_doc_id}`) | write pipeline; resolves refs from synced catalogs; odoo-inbox dispatches `write_to_1c` via hub | **causes connector writes** |
| `/onec/{id}/match-index`, `/bank-index`, `/nomenclature-index(+/head)`, `/write-metadata/{operation-kinds,documents}` | gzip artifacts/metadata built from synced rows (Celery builders) | reads connector data (incl. `Meta1C_Write_*` tables the connector uploads) |
| `/reports/kirim-chiqim`, `/reports/retail-rollup/build(-from-turnover)`, `/webhooks/report-refresh`, `/audit/odoo` | reports/rollups/audit | |

`/api/ai/tool` (service key) — LangGraph tool over metrics/connections/nomenclature.

### 2.6 Unauthenticated connector-facing

| Path | Does | Status |
|---|---|---|
| GET `/api/internal/connector/manifest?onec_id=` (`internal/routes/connector.py:51`) | provider PROVIDER_MAPPINGS tables ∪ `extraTables` | **DEAD for connector** (consumer removed in connector `c3fc269`, 2026-08-09). ACTIVE(o): aiba-next `crates/accounting/src/modules/onec_meta.rs:319` |
| WS `/api/v2/connector/ws` (`app/api/connector_ws.py:99`) | presence + command relay + cloud-client commands on same socket | **ACTIVE(C)** `src/providers/onec-hub.tsx:47` (presence-only hello) and `src/providers/onec-sync-provider.tsx:1144` (bases + capabilities + command results) |
| WS `/ws/1c` (`app/api/cloud_router_ws.py:918`) | legacy cloud-client router | UNUSED(C); cloud-os clients |
| GET `/api/v2/connector/ping` (connector_ws.py:45) | "TEMP diagnostic", returns build tag | DEAD (should be removed per its own docstring) |

---

## 3. Connection state / presence

Source of truth for "can write": **Redis**, not Mongo (`app/services/connection_state.py:1-15`).

Redis keys/channels (all in `app/core/connector_hub.py` unless noted):

| Key / channel | Type | Writer | TTL |
|---|---|---|---|
| `connector:{connectorId}` | HASH (connectorId, deviceId, userId, appVersion, infobases JSON, commands JSON, ip, lastSeen) | hello/state_update/ready/agent_ready, heartbeat | 90s (`HEARTBEAT_TTL`) |
| `connectors:online` | ZSET score=lastSeen | same | pruned on read |
| `conn-of-infobase:{oneCId}` | STR → connectorId | only for bases whose advertised status ∈ {connected, ready, ok} (`_extract_infobase_ids` :211); disowned routes released (:241) | 90s |
| `connector-cmd:{connectorId}` | pub/sub | `dispatch_to_infobase`, cloud router | — |
| `connector-resp:{requestId}` | pub/sub | socket worker on `*_result/_ack/_progress` | — |
| `connector-presence:update` | pub/sub | every register/heartbeat/offline | — |
| `sync-of-infobase:{oneCId}` | STR | `/entity/upload` (`mark_sync_active`) | 120s |
| `accessible_companies:{userId}` | STR | company_access | 300s |
| `cache-refresh-armed:{oneCId}` (`app/utils/entity_counts.py:103`), `running:{oneCId}` (api.py:460), `lock:*` (cache/lock.py), `onec:heavy:*`/`rerun:*` (heavy.py), `builders:slot`, `builders:queued:{artifact}:{id}`, `matchindex|bankindex|nomindex:build:{id}`, `onec-sync-lock:{id}` + `onec-sync:{id}[:log]` (cloud sync SSE), `report:{v}*`, `reconciliation:{v}:*`, `register_lines:*`, `onec_write_metadata:v1:*` | various | Celery/cache | |

Connector identity is **self-declared**: `connectorId` from `msg.connectorId` or `userId:deviceId` (`connector_ws.py:88`). Two sockets share one id: onec-hub (presence-only, no `bases`) and onec-sync-provider (`bases` + capabilities). "Absent ≠ empty" rule preserves base list for the presence-only socket (`connector_hub.py:94-155`).

`connection_state` per OneC response (`compute_connection_state`, :48): deleting → `deleting`; no route key → `offline`; sync key → `syncing`; Mongo `running` → `syncing` (legacy); else `writable`.

Mongo `status` lifecycle: connector PATCH heartbeat (`connection-status.tsx:126`) sets `active`; `/entity/upload` sets `running` (entity.py:345); beat `tasks.check_onec_heartbeat` every 60s demotes non-inactive/non-deleting docs with `statusUpdatedAt` older than 3 min to `inactive` (skips synced cloud bases) (`app/core/celery/api.py:2465`).

**Connector never reads `connection_state`/`can_write`/`state_reason`** (grep zero hits in `src`) — they exist for the cloud UI.

---

## 4. Entity upload contract (`POST /api/v2/entity/upload`)

Request: `multipart/form-data` — `oneCId` (form), `file` (JSON). Connector sends one chunk per POST, Bearer JWT, 5-min per-attempt timeout, 413 → recursive split (`chunk-uploader.ts:180-300`).

JSON shape (`parse_entity_entries`, entity.py:89): `{ "<oneCId>": { "<TableName>": [rows] } }` or root-level `{ "<TableName>": [rows] }`.

Per row, reserved keys popped before hashing (`app/utils/entity_dedup.py::split_upload_items`):
- `__rowKey` — identity (GUID, `recorder#line`, account code…). Fallback: `extract_ref_id(rawData)` (id/Ref_Key GUID).
- `__rowHash` — connector content fingerprint, stored as `rowHash`.

Server steps (entity.py:308-450):
1. Ownership (JWT user only; **service secret is rejected** because `user.id` is None).
2. 409 if OneC `status == deleting` (does **not** check `entitiesPurging`).
3. `mark_sync_active` (Redis 120s) + Mongo `status=running`.
4. UTF-8 decode → JSON. **No gzip support on development** (see Hidden findings).
5. For each table: `entityType` = PROVIDER_MAPPINGS/aliases else **literal tableName** (unknown tables accepted).
6. Rows with key → `upsert_entities_by_ref` keyed on `(oneCId, tableName, rowKey)`: insert / update when `dataHash` differs / collapse duplicate docs for the same key / adopt a legacy hash-only row (rowKey None) with the same dataHash.
7. Rows without key → hash dedup: `dataHash = SHA256(row + companyId salt)`, unique index `(oneCId, tableName, dataHash)`; E11000 swallowed.
8. Optional projections (`register_lines`, `doc_rows`) when their WRITE flags are on.
9. `apply_upload_deltas` → `$inc` on `EntityCount.counts` (only the upload delta).
10. If anything changed: `schedule_cache_refresh` (debounced per connection) + `invalidate_reports_for_upload`.

Response: `{message, oneCId, results:[{tableName, entityType, total, inserted, updated, collapsed, skipped}], totalTables, totalItems, totalInserted, totalSkipped, processedAt}`.

**Partition / multi-org:** the backend does **no org routing**. `oneCId` IS the partition. The connector decides: movement rows (`Document_`, `AccountingRegister_`, `AccumulationRegister_`, `stock`) with an orgRef go to the binding's oneCId; everything else to the connection (shared partition) (`onec_scope.py:59-76` must match connector `org-router.ts::isMovementTable`). Bindings found via `connectionId` on the v2 list (`OneCResponse.connectionId/orgRef/isShared`).

**Dedup scope** is per `(oneCId, tableName)`; a document that moves partition (re-map) leaves a stale copy in the old partition that no prune removes (acknowledged in `_stranded_movement_scan` warning text, onec.py:594-603).

---

## 5. Phase 5 — the sync-table system, end to end

### 5.1 Three generations (git history)

| When | Backend | Connector | State today |
|---|---|---|---|
| ≤ 2026-05 | none — `TABLES_BY_PROVIDER` hard-coded in connector | `core/config.ts` | still the compiled fallback |
| 2026-05-28 | `8c980dc` "manifest endpoint + per-OneC extraTables": `OneC.extraTables`, `GET /api/internal/connector/manifest` (no auth), admin `PUT /onecs/{id}/extra-tables` | `bc55fc4` "backend-driven manifest": cron merged local ∪ manifest each tick | **Dead for connector**: manifest fetch deleted in connector `c3fc269` (2026-08-09, "removal of the dead pre-cloud layer"). Endpoint still live; aiba-next proxies it (`onec_meta.rs:319`). `extraTables` now only feeds the manifest response. |
| 2026-08-06 | `ad8dd70` "per-connection sync table list, one list with a reports flag": `OneC.syncTables` + v1 GET/PUT `/onec/{id}/sync-tables` | drawer + resolver (`sync-tables-config.ts`, `sync-tables-config-fetcher.ts`) | **Authoritative today.** Later touched by `99f09c9` (mapping comments) and `b4dca48` (multi-org). |

### 5.2 Backend model + endpoints

- Model: `OneC.syncTables: list[dict]` default `[]` (`app/models/mongodb_models.py:67-86`). Stored on the **connection** record (multi-org: connection owns syncTables; bindings have none).
- Entry schema accepted by backend: `SyncTableEntry {table: str, reports: bool=False}` (`app/api/v1/routes/onec.py:804-808`). Nothing else.
- GET (:860): `tables: stored if stored else None` → **both "never set" and `[]` return `null`**.
- PUT (:885): full replace; `_clean_sync_tables` trims, drops blanks, first-wins dedup, **emits only `{table, reports}`** (:815-830). No table-name validation, no provider/version check, no deleting-status guard, accepts a binding id, no revision/version/etag, no `updatedAt` bump beyond `OneCRepository.update`.
- Service-secret callers skip ownership (`_sync_tables_target`, :833).
- **Only v1 exposes it.** There is no `/api/v2/onec/{id}/sync-tables`.
- Backend defaults: **none materialised**. `PROVIDER_MAPPINGS` (`app/utils/onec.py:158-260`) is provider-keyed (not version-aware); it only BUCKETS uploads to `entityType` and fills `/counts` with zero-rows for expected tables. Comments in onec.py explicitly say adding a mapping does NOT start a sync.

### 5.3 Per-table policy fields — where each one actually lives

| Policy | Backend | Connector (code) |
|---|---|---|
| membership | `syncTables[].table` | `resolveSyncLanes` (`src/features/accounting/lib/sync/sync-tables-config.ts:174`); null → compiled defaults `resolveTablesForInfoBase(provider, version)` (`src/core/config.ts:592`, version-aware 1.3/3.0 for venkon, flat for unisoft/1uz) |
| lane (main vs reports) | `syncTables[].reports` | same; `DEFAULT_REPORT_TABLES=[AccumulationRegister_РозничнаяВыручка]` (config.ts:77); policy override `effectivePolicyLane` forces `reports` for tables whose policy lane is reports (`sync-policy.ts:82`) |
| schedule (active / night) | **not stored** (stripped) | `SyncTableEntry.schedule` parsed (`sync-tables-config.ts:31,137-145`), default by prefix: `AccountingRegister_*` → night (:43-50); night window 21:00–07:00 local (:59); stale fallback 36h (:76); `isNightOnlyPolicy` forces night for policy lane `night` |
| mandatory tables | none | `MANDATORY_MAIN_TABLES_BY_PROVIDER.venkon` (GTD + Склады + СтраныМира + Валюты + ТорговыеТочки) force-unioned onto every list (config.ts:90-124) |
| priority / order | none | `tableSyncPriority` (config.ts:330): ChartOfAccounts 10 → Контрагенты 20 → Номенклатура 25 → Банки 30 → Договоры 35 → Document 40 → InfoReg 45 → Catalog 50 → AccumReg 70 → AccountingRegister 999 |
| strategy / reconciliation / automatic | none | `POLICY` table (`sync-policy.ts:30-72`): priority critical/hot/warm/cold, strategy targeted/cursor/bounded_hash/rolling_window/…, `Document_ЧекККМ` automatic=false (manual only) |
| start date / backfill window | none (backend only records the RESULT in `dataCoverage`) | windowed history backfill; `RETAIL_TURNOVER_WINDOW_DAYS=14`, `RETAIL_TURNOVER_BACKFILL_DAYS=400` (config.ts:139-152) |
| frequency | none | poller ~60s per base; reports lane own timer; eventlog driver |
| row thresholds / paging | `PRUNE_MAX_FRACTION=0.05` (server-enforced) | `COLD_READ_PAGES_PER_CYCLE=20` (10k rows/cycle), `DOC_SWEEP_REREAD_PER_CYCLE=25`, `DOC_SWEEP_MIN_INTERVAL_MS=30min` (config.ts:368-397) |
| full vs incremental | admin `sync_now?mode=full|cursor`, `pull_now` always full | `syncMode` (`onec-sync-provider.tsx:2045-2049`); count gate vs `/counts`; refhashes digest/bucket diff |
| org scope | partitions via org-bindings; `scope=connection` on diff endpoints | connector routes per row |
| watermarks | `OneC.watermarks` — **cloud (OData) path only** | connector cursor is local |

### 5.4 How the connector fetches / caches / refreshes

- Fetch: `fetchSyncTablesConfig` GET v1 with Bearer, 10s timeout, **never throws**; non-2xx/timeout → `{tables:null, unset:false}` → compiled defaults (`sync-tables-config-fetcher.ts:32-62`).
- Parse: accepts strings or `{table, reports, schedule}`; non-array → null (`sync-tables-config.ts:119`).
- Callers: poller per run via `getSyncTables` (`src/features/accounting/providers/info-base-sync-provider.tsx:193-232`); cloud-triggered `sync_now/pull_now` re-fetches (`onec-sync-provider.tsx:2022-2031`); reports lane per run (`reports-lane-driver.ts:205`); eventlog driver with 60s TTL incl. negative caching (`eventlog-driver.ts:673,827-845`); eventlog-provider (`src/providers/eventlog-provider.tsx:188`); bulk-upload modal (`bulk-upload-modal.tsx:115`); drawer load (`sync-tables-drawer.tsx:87`).
- `resolveConfiguredSyncTables` (sync-tables-config.ts:257) — explicit list else fetch, so new drivers can't forget it.
- **Seeding:** when GET answers `tables:null` (unset=true), the poller writes the EFFECTIVE compiled list (lanes + mandatory, `reports` flags, **no schedule**) back via PUT, once per base per session (`info-base-sync-provider.tsx:179-226`). Reason in comment: kansler's GTD «Включить таблицы» can only merge into an existing list.
- Save: drawer PUT full list incl. `schedule` (`sync-tables-drawer.tsx:168-176`); throws on non-2xx.
- **Change propagation: polling only.** No push/invalidation, no revision field, no WS command for "sync tables changed". Edits apply on the next run (≤ ~60s main lane; eventlog ≤ 60s TTL). A cloud `sync_now/pull_now` re-reads immediately.

### 5.5 Add / remove behaviour

- Add: PUT full list with the new entry → next run includes it; count gate sees backend 0 vs 1C N → cold read. Backend buckets rows under literal tableName unless PROVIDER_MAPPINGS knows it.
- Remove: table drops from the run. **Stored rows are kept forever** — no backend cleanup, `/counts` still reports them (stored ∪ expected tables), and they stay readable by reports.
- Empty list: backend returns null → connector treats as unset → **re-seeds defaults** (see Hidden findings #2).

---

## 6. Endpoints that make the backend command a connector

All via `dispatch_to_infobase` (Redis pub/sub to the worker holding the socket; `COMMAND_TIMEOUT` 120s, writes `WRITE_COMMAND_TIMEOUT` 300s; `*_progress` frames non-terminal) or the cloud router relay (45s idle / 600s ceiling).

| Trigger | Action sent |
|---|---|
| `/api/internal/admin/connectors/infobase/{id}/sync` | `sync_now {mode}` |
| `.../read-table` | `pull_now {tables, priority}` |
| `POST /api/v2/reports/{key}/refresh` + daily `tasks.refresh_all_reports` (03:00) → `tasks.refresh_report` | pull of the report's source table via connector (`app/core/celery/reports.py:76`) |
| `.../document/post` → `onec_post` | `post_1c_document` |
| `.../document/unpost|update|delete`, `.../catalog/update|read`, `.../documents/read` | `unpost_1c_document`, `update_1c_document`, `delete_1c_document`, `catalog_update`, `catalog_read`, `document_read` |
| `/api/internal/admin/odoo-inbox` (+ backstop beat) | `write_to_1c` |
| Cloud clients on `/api/v2/connector/ws` or `/ws/1c` (**no auth**) | `write_to_1c`, `employee_*`, `check_exists`, `delete_from_1c`, `push_to_1c`, `delete_in_1c`, `holiday_*`, `list_resource`, `server_ping` (fan-out to all), `get_list`, `create_ref` (`cloud_router_ws.py:50-68`) |

Connector handles all of these (`onec-sync-provider.tsx:14910-15131`). There is **no command** for sync-tables changes, connection deletion, or binding changes.

---

## 7. Celery / background tasks touching connector data (beat in `app/core/celery/app.py`)

| Task | Schedule | Effect |
|---|---|---|
| `tasks.check_onec_heartbeat` | 60s | demote stale status → inactive |
| `tasks.auto_update_onec` | 60s | compare EntityCount vs `rebuiltCounts`, arm cache rebuild |
| `tasks.check_one_onec_cache` / `check_and_clear_cache` | on demand (debounced after upload) | 40 metrics + reconciliation caches |
| `tasks.retruth_entity_counts` → `retruth_one_onec_counts` | 6h | live recount (the only thing that subtracts prune/reconcile deletes in v2 mode) |
| `tasks.purge_onec` / `purge_onec_entities` / `purge_stuck_onec` | on demand / 120s backstop | chunked delete (5000) |
| `tasks.drain_builders` | 300s | match/bank/nomenclature index rebuilds |
| `tasks.refresh_all_reports` | 03:00 daily | connector pulls |
| `tasks.build_retail_rollups` | 30 min | rollup from synced rows |
| `tasks.sync_all_cloud_onec_connections` | 30 min | OData pull for `source=cloud` (no connector) |
| `tasks.process_odoo_inbox_backstop`, `odoo_inbox_health` | 60s | Odoo writes via connector |
| `tasks.api_request` (`create_entities`) | on demand | MinIO file import |
| projections (`backfill_register_lines`, …) | on demand | register_lines / doc_rows |

---

## 8. Where backend expects behaviour the Connector only partially implements

1. **`schedule` field**: connector reads/writes it, backend silently drops it → night/active overrides never persist; every base runs prefix defaults. (connector `sync-tables-drawer.tsx:64-70,172` vs backend `v1 onec.py:804-830`).
2. **"[] = sync nothing"**: backend docstring promises it; code returns null; connector then re-seeds → impossible to empty a list.
3. **gzip uploads**: connector comments say backend detects gzip by magic bytes (`config.ts:36-42`); only on unmerged branch `origin/perf/ingest` (`ba0ed43`). Enabling `GZIP_UPLOAD=1` would make every chunk 400 "File must be UTF-8 encoded".
4. **`connection_state`** is computed on every list/get/patch response but ignored by the connector.
5. **`delete-status` for connection deletes** exists; connector only polls the entity-purge status.
6. **`entitiesPurging`**: upload doesn't refuse during an entity purge; connector doesn't know to stop either → rows uploaded mid-purge may be deleted by the purge walk, or survive partially.
7. **Counts after deletes**: `EntityCount.counts` only `$inc`s upload deltas; prune-missing/reconcile-recorder/purge-by-table don't decrement (acknowledged `onec.py:577-582`). In v2 cache mode it is corrected only by the 6-hourly retruth → connector's count gate sees backend > 1C (surplus) for up to 6h after a prune.
8. **Removed tables**: no backend clean-up when a table leaves `syncTables`.
9. **Manifest/extraTables**: backend + aiba-next still treat it as the way to "flip on a table for one client"; the connector stopped reading it on 2026-08-09.

---

## Hidden / surprising findings

1. **aiba-next calls a route that does not exist.** `onec_meta.rs:301` GETs `/api/v2/onec/{id}/sync-tables`; backend/1c only has `/api/v1/...` → always 404, so the aiba-next "base sync tables" endpoint and `extapi` `1c.base_sync_tables` are broken.
2. **Emptied list resurrects itself.** `get_sync_tables` returns `stored if stored else None` (`v1 onec.py:881`), so `[]` → `null`; the connector's seeding path then PUTs the compiled defaults back (`info-base-sync-provider.tsx:204-216`). The model comment ("EMPTY MEANS UNCONFIGURED", `mongodb_models.py:77`) and route docstring ("[] — deliberately emptied", :864-872) contradict each other.
3. **`schedule` silently stripped** by `_clean_sync_tables` — the drawer's carefully preserved night/active choices never reach Mongo.
4. **Seed writes drop schedule and include mandatory tables** — after seeding, the "stored list" is no longer "what the user chose" but compiled defaults + mandatory, first-wins.
5. **Manifest is half-dead**: unauthenticated, still served, still proxied by aiba-next, but no connector consumes it since `c3fc269`. `extraTables` has no effect on what the connector syncs.
6. **Gzip upload support never merged** to development (`ba0ed43` only on `origin/perf/ingest*`).
7. **v1 vs v2 split is load-bearing**: all diff/prune/sync-tables endpoints exist only in v1; the connector rewrites its v2 base URL to v1 for them. `/counts` exists in both (duplicated code).
8. **Deleting a multi-org connection orphans its bindings**: `purge_onec` deletes only `oneCId == connection` rows and the connection doc (`api.py:2646-2700`); binding docs (`connectionId` = deleted id) and their partitions remain and still resolve by company.
9. **Purge misses legacy string-form oneCId rows**: purge and delete-status match only `ObjectId(onec_id)`, while code elsewhere says rows exist in both string and ObjectId forms.
10. **v2 list is broader than v1 list**: v1 filters by userId, v2 does not filter at all.
11. `/api/v2/connector/ping` "TEMP diagnostic" still mounted.
12. Local checkout is 58 commits behind `origin/development` — anyone reading `D:\aiba\backend\1c` directly sees stale code.

## Security concerns

(No secrets reproduced; a hard-coded key exists and is referenced only by location.)

1. **CRITICAL — any valid JWT can wipe the database.** `DELETE /api/v1/statistics/cache/clear-all?include_entities=true&include_onecs=true` with no `onec_id` deletes ALL `cached_metrics`, `entity_counts`, `entitydatas` and `onecs` fleet-wide; with `onec_id` it deletes any connection without ownership (`app/api/v1/routes/statistics/cache.py:157-268`). No admin/owner check.
2. **CRITICAL — unauthenticated WebSocket command bus.** `/api/v2/connector/ws` and `/ws/1c` accept any client: (a) `get_connector_bases` returns every online base of every user; (b) `write_to_1c`, `delete_from_1c`, `delete_in_1c`, `employee_fire_from_1c`, `push_to_1c`, `create_ref` etc. are routed to customer connectors with no auth (`cloud_router_ws.py:704-915`, `connector_ws.py:126-130`); (c) a fake connector can `hello` with any oneCId and status `connected` to grab `conn-of-infobase:{id}`, receiving write payloads and returning forged results. Identity is self-declared (`connector_ws.py:9-11,88`).
3. **HIGH — unauthenticated fleet cache wipe/rebuild**: `POST /api/v2/onec/connection/rebuild` (no oneCId) deletes all CachedMetric + EntityCount and triggers a fleet rebuild (`v2 onec.py:255`); `POST /api/v2/onec/connection/backfill-total-count` unauthenticated write (:234). Also `POST /api/v1/statistics/cache/fix-ownership` fleet-wide update, any JWT.
4. **HIGH — cross-tenant reads**: `GET /api/v2/onec` (no user filter; no companyId → entire fleet incl. names, odataName, INN, userIds) (`v2 onec.py:97`); `GET /api/v1/statistics/cache/{company_id}` no ownership check.
5. **HIGH — cloud credentials exposure**: internal admin `GET/PATCH /onecs[/{id}]` return `model_dump()` incl. `passwordEncrypted`, `login`, `baseUrl` to any of the four peer tokens (incl. external `ODOO_API_TOKEN`) (`internal/routes/onec.py:85,141,179`). If `ONEC_CRED_FERNET_KEY` is unset, `app/utils/crypto.py:15` falls back to a **hard-coded Fernet key in source** (value redacted) → anyone with repo access can decrypt.
6. **MEDIUM — create for any company**: `POST /api/v2/onec` checks only `userId == caller`, not company access → a user can attach a connection to someone else's companyId; `by-company` resolution then passes `verify_ownership` for that user.
7. **MEDIUM — all issued tokens are full peers** on the internal write pipeline (`verify_write_caller` == `verify_service_key`), so the external Odoo/BI tokens can post/unpost/delete documents in any customer's 1C via `connectors/infobase/*`.
8. **LOW** — unauthenticated `/api/internal/connector/manifest` leaks table lists per oneCId; `/api/v2/connector/ping` leaks build tag; CORS `allow_origins=["*"]` with `allow_credentials=True`; `GET /api/v2/onec/connection/{id}/delete-status` answers counts without auth once the doc is gone.
9. Service-secret callers can PUT `sync-tables` on any connection id with no validation (no audit row unless an issued token was used).

## Open questions

1. Is there edge (nginx/Cloudflare) auth in front of `/api/v2/connector/ws`, `/ws/1c`, `/api/v2/onec/connection/rebuild` in prod? Code has none.
2. Is `ONEC_CRED_FERNET_KEY` set in prod/dev swarm env? (Determines whether finding Security #5 is exploitable.)
3. Which `CACHE_PASS_MODE` runs in prod (`legacy` re-truths counts every 60s; `v2` only every 6h → affects how long post-prune surplus shows)?
4. Is `GZIP_UPLOAD=1` set on any connector install? If so those uploads fail against development.
5. Does anything (kansler GTD flow) still rely on `extraTables`/manifest, or can both be retired? aiba-next still proxies it.
6. Does kansler write `schedule` via the service-secret PUT expecting persistence? (Backend drops it.)
7. Is the 1uz clone backend at parity for `/api/v1/onec/{id}/sync-tables`, `refhashes?digest`, `prune-missing`? (Connector uses the same paths for it.)
8. Who is meant to clean partitions of tables removed from `syncTables`, and of bindings orphaned by a connection delete?
