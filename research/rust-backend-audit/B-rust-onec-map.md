# B — Rust onec backend map (next/modules/onec)

Read-only audit, 2026-10-07. Checkout: `D:\aiba\next-modules\onec`, branch `master`,
HEAD `6c46165` ("fix(relay): a pipe goes quiet, not stale"). All paths below are
relative to that checkout's `src/` unless stated. Line numbers are from HEAD.

`cargo check --offline` was run against the existing `target/`: **Finished, 0 errors,
1 warning (unused import `State`)**, ~1m55s. Tests were not run.

---

## 1. Stack, boot, config

**Crates** (`Cargo.toml`): axum 0.7 (`ws`, `multipart`), tokio, **sqlx 0.8 (postgres, rustls,
json, chrono; runtime queries, no compile-time macros used for DDL)**, reqwest (rustls),
serde/serde_json, sha2, flate2 (gzip), dashmap 6, jsonwebtoken 9, tower-http (cors),
tracing. Deliberately no `api-core` dependency (Cargo.toml comment). No Redis, no queue.

**Image** (`Dockerfile`): rust:1.88-slim builder → debian:bookworm-slim, user `aiba`,
`EXPOSE 8012`, HEALTHCHECK `curl /health`. `deploy/docker-compose.yml` is reference
only; binds `127.0.0.1:8012:8012` and expects the tenant reverse proxy in front.

**Boot** (`main.rs:57-123`):
1. dotenv, rustls ring provider, tracing (no ANSI).
2. `Config::from_env()` (`config.rs:122`).
3. `Tenants::single` or `Tenants::multi` (`main.rs:74-77`). Each tenant DB goes through
   `tenants::boot` (`tenants.rs:204`): pool → `store::migrate` (fail-loud DDL) →
   background `store::ensure_indexes` (CONCURRENTLY) → background `catalog::seed_dirty`.
4. Restore `preferred_connector_id` per base into each tenant's hub (`main.rs:101-111`).
5. `tickers::spawn` (`main.rs:114`), then `axum::serve` with a request-log middleware.

**TENANT_MODE** (`config.rs:7-11`, `tenants.rs`):
- `single` (default): one `DATABASE_URL`, slug `"default"` (`tenants.rs:26`), `X-Tenant`
  is ignored.
- `multi`: one container per server; tenant list fetched from
  `{ESKEY_CENTRAL_URL}/api/v2/internal/modules/tenants?server_id=` with `X-Service-Secret`
  (`tenants.rs:115-135`), refreshed every `TENANTS_REFRESH_SECS` and on a miss (gated 5s,
  `tenants.rs:103`). Every request is routed by the `X-Tenant` header
  (`api.rs:107`, extractor `api.rs:114-125`); unknown slug → 404. One hub per tenant
  (`api.rs:43-47`).

**Env vars** (`config.rs:122-152`): `BIND_ADDR` (0.0.0.0:8012), `TENANT_MODE`, `SERVER_ID`,
`TENANTS_REFRESH_SECS`, `DB_POOL_MAX` (10), `DATABASE_URL`, `SERVICE_SECRET` (required),
`ESKEY_CENTRAL_URL` (https://next.aiba.uz), `ESKEY_SERVICE_SECRET`, `AIBA_JWT_SECRET`,
`CONNECTOR_WS_REQUIRE_SECRET` (default **false**), `TICKERS_ENABLED`, `STATUS_SWEEP_SECS`,
`STATUS_STALE_SECS`, `REQUEST_REAP_SECS`, `ORPHAN_SWEEP_SECS`, `PURGE_REDRIVE_SECS`,
`PURGE_GRACE_SECS`, `RECON_REFRESH_SECS`, `CATALOG_REFRESH_SECS`, `MATCH_INDEX_TTL_SECS`,
`MATCH_INDEX_RETRY_BACKOFF_SECS`, `BANK_INDEX_TTL_SECS`, `BANK_INDEX_RETRY_BACKOFF_SECS`.
Required: `SERVICE_SECRET`; `DATABASE_URL` (single) or `SERVER_ID` (multi) (`config.rs:156-171`).

---

## 2. Auth and tenant isolation

- **One guard for everything**: `secret_or_jwt` (`api.rs:136-162`). Accepts either
  `X-Service-Secret == SERVICE_SECRET` (plain string equality, **not HMAC, not
  constant-time**, `api.rs:138`) or any HS256 Bearer JWT signed with `AIBA_JWT_SECRET`
  (signature + exp only; `validate_aud=false`, `api.rs:148`; claims parsed only for `sub`,
  unused).
- **No authorization**: a valid JWT is accepted for every route, every `onec_id`, every
  company. No user→company or company→connection check anywhere.
- **Multi-tenant hole**: in multi mode the tenant is chosen by the caller-supplied
  `X-Tenant` header and the JWT carries no tenant claim that is checked
  (`api.rs:114-121` comment: "the connector's JWT path has no tenant claim to check here").
  Any JWT valid for one tenant on the server can read/write every other tenant by
  changing `X-Tenant`.
- **Unauthenticated endpoint**: `GET /api/internal/connector/manifest` (`entity.rs:2075`)
  takes no headers at all.
- **WebSocket**: unauthenticated by design unless `CONNECTOR_WS_REQUIRE_SECRET=true`
  (`connector.rs:1114-1156`, doc `connector.rs:88-90`). Identity is self-declared in
  `hello`. Cloud commands (`write_to_1c`, `delete_from_1c`, `create_ref`, …,
  `connector.rs:832-846`) are classified by action name only (`connector.rs:1269`), so
  **any client that can open the socket can issue writes/deletes to any online base**, and
  any client can `hello` claiming any base and receive commands meant for it.
  `docs/CONNECTOR-THROUGH-NEXT.md` §1 acknowledges this; the fix (route via next, flip
  the flag) is per-tenant and default OFF.
- CORS is fully permissive (`api.rs:536`).

---

## 3. Postgres schema

All DDL is one inline array `STMTS` in `store.rs:27-367`, applied by `migrate`
(`store.rs:374`). Non-UNIQUE `CREATE INDEX` statements are deferred and rebuilt
`CONCURRENTLY` in the background (`is_deferrable_index` `store.rs:415`, `ensure_indexes`
`store.rs:447`, which first drops INVALID indexes). No migration table/versioning; it is
idempotent `IF NOT EXISTS` / `ADD COLUMN IF NOT EXISTS` only. Schema `onec`, co-tenant
in the tenant's main DB (`config.rs:29-32`).

Storage model: **one generic JSONB table** (`onec.entity_data`) for every synced 1C row,
partitioned logically by `onec_id` (= `onec.connection.id`, bigint). No per-entity tables.
Organisation is not a column on entity rows: multi-org is modelled by extra
`onec.connection` rows (bindings) whose id is used as `onec_id`.

### Tables

| Table | Key / columns | Indexes / constraints |
|---|---|---|
| `onec.connection` (`store.rs:34-54` + ALTERs 58-99) | `id bigserial PK`, `company_id int NOT NULL`, `company_inn`, `provider` ('venkon'), `name`, `odata_name`, `source` ('connector'), `status` ('inactive'), `version`, `config_version`, `base_key`, `total_count`, `fail_count`, `last_error`, `last_sync_at`, `status_updated_at`, `raw jsonb` (syncTables, extraTables, dataCoverage, creds, watermarks…), `created_at`, `updated_at`; ALTERs: `deletion_started_at`, `recon_dirty_at`, `catalog_dirty_at`, `preferred_connector_id`, `connection_id` (parent), `org_ref`, `org_ref_removed`, `org_name`, `org_code`, `is_shared bool` | `onec_connection_org_uq UNIQUE (connection_id, org_ref) WHERE org_ref IS NOT NULL`; idx on `(connection_id)` partial, `(company_id)`, `(company_inn)`, `(source,status)` |
| `onec.entity_data` (`store.rs:110-122`) | `id bigserial PK`, `onec_id bigint`, `company_id int`, `entity_type`, `table_name`, `raw jsonb`, `data_hash` (sha256 of canonical row + companyId), `ref_id` (GUID from `id`/`Ref_Key`), `row_key` (connector `__rowKey` or GUID), `row_hash` (connector `__rowHash`), `synced_at`; generated stored `cp_key` (`store.rs:169`) | `onec_entity_rowkey_uq UNIQUE (onec_id,table_name,row_key) WHERE row_key IS NOT NULL`; `onec_entity_hash_uq UNIQUE (onec_id,table_name,data_hash) WHERE row_key IS NULL`; idx `(onec_id,table_name,ref_id)` partial, `(onec_id,entity_type)`, `(onec_id,table_name)`, `(onec_id,entity_type,id)`, `(onec_id,entity_type,cp_key)`, `(onec_id,table_name,cp_key)`, expr `raw->>'ИНН'`, `raw->>'ПИНФЛ'`, `raw->>'СчетДтКод'`, `raw->>'СчетКтКод'` (all with `onec_id,table_name`) |
| `onec.entity_count` (`store.rs:262-268`) | `onec_id PK`, `company_id`, `counts jsonb` {table: n}, `rebuilt_counts jsonb` (never written by any code), `updated_at` | PK only |
| `onec.retail_daily_rollup` (`store.rs:219-229`) | `id PK`, `onec_id`, `company_id`, `day`, `retail_point`, `payment_type`, `sum numeric(20,2)`, `qty numeric(20,3)` | idx `(onec_id, day)`; no unique |
| `onec.retail_rollup_coverage` (`store.rs:235-240`) | PK `(onec_id, day)` | — |
| `onec.recon_summary` (`store.rs:255-261`) | `company_id PK`, `provider`, `payload jsonb`, `source_dirty_at`, `computed_at` | — |
| `onec.catalog_entry` (`store.rs:293-313`) | PK `(onec_id, catalog, onec_ref)`; `name`, `norm_name`, `code`, `inn`, `deletion_mark`, `synced_at bigint` | idx `(onec_id,catalog,norm_name)`, partial `(onec_id,catalog,inn)`, GIN `(onec_id,catalog,norm_name gin_trgm_ops)` (needs pg_trgm + btree_gin, `store.rs:381`) |
| `onec.catalog_sync` (`store.rs:333-344`) | PK `(onec_id, catalog)`; `company_id`, `row_count`, `upstream_total`, `complete_at`, `source_dirty_at`, `attempted_at`, `last_error` | — |
| `onec.table_purge_job` (`store.rs:353-362`) | `id PK`, `onec_id`, `table_name`, `state` ('running'), `deleted`, `error`, `started_at`, `finished_at` | idx `(onec_id, started_at DESC)`, `(state, started_at)` |

No foreign keys anywhere. No tables for writes/outbox, idempotency ledger, sync runs,
or an audit log.

**Connection shapes** (`store.rs:85-89`): legacy (`connection_id NULL, is_shared false`),
shared connection (`connection_id NULL, is_shared true` — holds shared/reference rows and
sync state), org binding (`connection_id=parent, org_ref=GUID` — one org's rows for one
company, never synced itself). IDs are emitted as strings on the wire (`store.rs:592-638`).

---

## 4. Routes

Router: `api.rs:454-545` + merged `recon::routes` (`recon.rs:1792`), `connector::routes`
(`connector.rs:2299`), `write::routes` (`write.rs:1250`), `catalog::routes`
(`catalog.rs:676`), `reports::routes` (`reports.rs:519`). Body cap 128 MB
(`api.rs:540`). Auth column: S/J = `secret_or_jwt`; none = no check.

### Connections
| Method | Path (v1 and v2 unless noted) | Auth | Handler | Shape |
|---|---|---|---|---|
| GET | `/health` | none | `api.rs:191` | `{status,service}` no I/O |
| POST | `/api/v{1,2}/onec` | S/J | `api.rs:199` → `store.rs:699` | body → row; full body stored as `raw`; 201 conn JSON |
| GET | `/api/v{1,2}/onec?companyId=` | S/J | `api.rs:213` | `{items,total,page,size,pages}`; LIMIT 500; fail-closed on non-int companyId (`api.rs:178`) |
| GET | `/api/v{1,2}/onec/:id` | S/J | `api.rs:254` | slim conn JSON |
| GET | `/api/v2/onec/by-company/:company_id` | S/J | `api.rs:243` | `{items,total}`; excludes shared + deleting (`store.rs:774`) |
| GET | `/api/v2/onec/:id/connectors` | S/J | `api.rs:272` | live hub instances + preferred |
| PUT | `/api/v2/onec/:id/preferred-connector` | S/J | `api.rs:291` | `{connector_id}` → DB then hub |
| GET | `/api/internal/admin/onecs` | S/J | `api.rs:332` | `{items,total,skip,limit}` OFFSET paging |
| GET | `/api/internal/admin/onecs/:id` | S/J | `api.rs:317` | full view = conn + raw minus secret-looking keys (`store.rs:647-680`) |
| PATCH | `/api/v{1,2}/onec/connection/:id` | S/J | `api.rs:367` → `store.rs:872` | `raw = raw || body` + flat columns; status limited to active/inactive/running |
| DELETE | `/api/v{1,2}/onec/connection/:id` | S/J | `api.rs:410` | 202; status→deleting (cascades to bindings), detached chunked purge |
| GET | `/api/v{1,2}/onec/connection/:id/delete-status` | S/J | `api.rs:442` | `{status,done,remaining}` |

### Org bindings, sync config, change detection
| Method | Path | Auth | Handler |
|---|---|---|---|
| GET / PUT | `/api/v{1,2}/onec/:id/org-bindings` | S/J | `bindings.rs:229` / `bindings.rs:267` |
| DELETE | `/api/v{1,2}/onec/:id/org-bindings/:binding_id` | S/J | `bindings.rs:621` |
| GET | `/api/v{1,2}/onec/:id/counts[?scope=]` | S/J | `entity.rs:2218` → `[{tableName,quantity}]` |
| GET | `/api/v{1,2}/onec/:id/refids?table_name=` | S/J | `entity.rs:2308` → `{count,refIds}` |
| GET / PUT | `/api/v{1,2}/onec/:id/sync-tables` | S/J | `entity.rs:2355` / `2380` — `raw.syncTables` `[{table,reports}]`, null = unset |
| GET | `/api/v{1,2}/onec/:id/refhashes?table_name=&digest=&buckets=&scope=` | S/J | `entity.rs:2442` |
| POST | `/api/v{1,2}/onec/:id/reconcile-recorder[?scope=]` | S/J | `entity.rs:2590` — `{tableName,recorderRef,liveKeys}` |
| POST | `/api/v{1,2}/onec/:id/prune-missing[?scope=]` | S/J | `entity.rs:2663` — `{tableName,missingKeys,liveCount,dryRun}` |

### Entity data plane
| Method | Path | Auth | Handler |
|---|---|---|---|
| POST | `/api/v2/entity/upload` (multipart `oneCId` + `file`, gzip sniffed) | S/J | `entity.rs:512` |
| POST | `/api/v2/entity/stock-snapshot` `{oneCId,rows}` | S/J | `entity.rs:700` |
| POST | `/api/v2/entity/data-coverage` `{oneCId,tables:[{table,dataFrom,complete}]}` | S/J | `entity.rs:787` |
| DELETE | `/api/v2/entity/oneC/:id` | S/J | `entity.rs:2814` (sync, one DELETE) |
| GET | `/api/v2/entity/oneC/:id/delete-status` | S/J | `entity.rs:2849` (always done) |
| POST | `/api/v2/entity/oneC/:id/purge-tables` `{tables,dryRun}` | S/J | `entity.rs:2873` (202 + jobs) |
| GET | `/api/v2/entity/oneC/:id/purge-tables/status` | S/J | `entity.rs:2969` |
| GET | `/api/internal/admin/entity-data/by-type` | S/J | `entity.rs:870` (keyset `cursor`, or skip/limit) |
| GET | `/api/internal/admin/entity-data/last-by-counterparty` | S/J | `entity.rs:1524` |
| GET | `/api/internal/admin/entity-data` | S/J | `entity.rs:1194` |
| GET | `/api/internal/admin/onec/:onec_id/match-index` | S/J | `match_index.rs:136` (gzip artifact, in-memory cache) |
| GET | `/api/internal/admin/onec/:onec_id/bank-index` | S/J | `bank_index.rs:203` (`reg: []` still) |
| GET | `/api/internal/connector/manifest?onec_id=` | **none** | `entity.rs:2075` |

### Reconciliation, catalog, reports
| Method | Path | Auth | Handler |
|---|---|---|---|
| GET | `/api/v2/reconciliation/counterparties` | S/J | `recon.rs:840` |
| GET | `/api/v2/reconciliation/contracts` | S/J | `recon.rs:901` |
| GET | `/api/v2/reconciliation/summary` | S/J | `recon.rs:968` (materialized row for default view; live fold otherwise or on miss, `recon.rs:997-1004`) |
| GET | `/api/v2/reconciliation/detail/:counterparty` | S/J | `recon.rs:1307` |
| GET | `/api/internal/admin/reconciliation/ledger` | S/J | `recon.rs:1701` |
| GET | `/api/internal/admin/onec/:onec_id/catalog/{search,by-inn,by-name}` | S/J | `catalog.rs:534/585/608` |
| POST | `/api/internal/admin/onec/:onec_id/catalog/refresh` | S/J | `catalog.rs:664` (runs inline, returns 202) |
| POST | `/api/v2/reports/retail-rollup/ingest` | S/J | `reports.rs:74` (txn: delete range + insert + coverage) |
| GET | `/api/v2/reports/retail-revenue` | S/J | `reports.rs:328` |

### Connector hub + writes
| Method | Path | Auth | Handler |
|---|---|---|---|
| GET (WS) | `/api/v2/connector/ws` | none (optional secret gate) | `connector.rs:1136` |
| GET | `/api/internal/admin/connectors` | S/J | `connector.rs:2261` |
| POST | `/api/internal/admin/connectors/infobase/:onec_id/sync?mode=` | S/J | `connector.rs:2206` → `sync_now` |
| POST | `/api/internal/admin/connectors/infobase/:onec_id/read-table` | S/J | `connector.rs:2238` → `pull_now` |
| POST | `/api/internal/admin/resolve-refs` | S/J | `write.rs:835` |
| POST | `/api/internal/admin/connectors/infobase/:onec_id/document/post` | S/J | `write.rs:941` → `post_1c_document`, HTTP 200 always |
| POST | `/api/internal/admin/connectors/infobase/:onec_id/catalog/update` | S/J | `write.rs:1335` → `catalog_update` (+CAS via `expected`) |

---

## 5. Entity upload / sync

`POST /api/v2/entity/upload` (`entity.rs:512-695`):
- Input: multipart `oneCId` + `file` (JSON, optionally gzip). Body `{ "<oneCId>": {table:[rows]} }`
  or root-level `{table:[rows]}` (`entity.rs:479-509`).
- Connection must exist (`conn_provider_company`, `entity.rs:2133`), **status not checked**
  — uploads into a `deleting` connection are accepted while the purge is running.
- Per row: strip `__rowKey`/`__rowHash`, compute `data_hash` = sha256(canonical sorted JSON +
  companyId) (`entity.rs:65-107`), `ref_id` = GUID in `id`/`Ref_Key` (`entity.rs:120`).
  Rows with a key (rowKey or GUID) → `upsert_id_items`; rows without → `insert_plain_items`.
  In-batch dedup: last wins per key, first wins per hash (`entity.rs:367-399`).
- Upsert: multi-row `INSERT … ON CONFLICT (onec_id,table_name,row_key) WHERE row_key IS NOT NULL
  DO UPDATE SET raw,data_hash,ref_id,row_hash,entity_type,synced_at` in chunks of 500
  (`entity.rs:403-443`). No `WHERE data_hash IS DISTINCT FROM` → unchanged rows are
  rewritten every time (dead tuples, WAL). Keyless: `ON CONFLICT (…data_hash) DO NOTHING`
  (`entity.rs:446-476`). No COPY.
- **No transaction**: each 500-row chunk commits on its own; on an error mid-table it
  returns 400 with earlier tables/chunks already committed (`entity.rs:612-621`).
- Ack: `201 {message, oneCId, results:[{tableName,entityType,total,inserted,updated,collapsed:0,skipped}], totalTables, totalItems, totalInserted, totalSkipped, processedAt}`
  (`entity.rs:680-694`). `updated` = changed − inserted (not a real "changed" count); no
  per-row keys acked, no batch id, no server-side idempotency token.
- Side effects: `apply_upload_deltas` read-modify-writes `entity_count.counts` adding only
  inserts (`entity.rs:2174-2216`, best-effort, errors swallowed); stamps `recon_dirty_at`
  on any rows and `catalog_dirty_at` if a Sotuv catalog table was present (`entity.rs:660-675`).
- Versioning: none beyond `synced_at`; change detection is the connector-supplied
  `row_hash` exposed back through `/refhashes`.

`stock-snapshot` (`entity.rs:700-781`): one txn, delete all `STOCK_BALANCE` rows for the
base, insert fresh rows (table_name `stock`, hash dedup). Proper replace semantics.

`data-coverage` (`entity.rs:787-866`): read-modify-write of `connection.raw.dataCoverage`
with no lock/CAS (comment assumes uploads are serialized upstream); races with PATCH
`raw || body` and with `put_sync_tables`.

Sync-table config: `raw.syncTables` via GET/PUT sync-tables (`entity.rs:2355-2425`);
`raw.extraTables` + provider canonical mappings via the unauthenticated manifest
(`entity.rs:2075-2119`). Table→EntityType mapping is static in `entity.rs:150-330`
(venkon/unisoft); unknown tables are stored under the literal table name.

---

## 6. Counts, refhashes, prune, recorder reconcile, purge

- **counts** (`entity.rs:2218-2303`): sum of `entity_count.counts` over the scoped ids +
  `SELECT DISTINCT table_name` over entity_data + a live `count(*)` per table missing from the
  map (per-table round trips). The map is only ever **incremented** on upload; prune,
  reconcile-recorder, purge_entities and stock replace never decrement it (only
  `purge_one_table` removes a key, `store.rs:1163`; `purge_entities` zeroes it). The
  comments promise a "periodic recount" (`entity.rs:645`, `2171`) — **no such ticker
  exists** (`tickers.rs` has T1–T6, none recounts; `rebuilt_counts` is never written).
  Counts therefore drift high after any deletion.
- **refhashes** (`entity.rs:2442-2563`): `SELECT row_key,row_hash,raw … ORDER BY synced_at DESC, id DESC`
  over the whole (onec_id,table) partition (`entity.rs:2489`) — it pulls full `raw` jsonb for
  every row even in `digest=1` mode and when no projection exists; bucket filtering is in Rust.
  Memory/time heavy on register-sized tables.
- **reconcile-recorder** (`entity.rs:2590-2661`): deletes rows where
  `(row_key LIKE '<rec>#%' OR ref_id LIKE '<rec>#%') AND NOT in liveKeys`, one statement per
  scoped onec_id. Empty liveKeys deletes the recorder's rows. No count decrement. The
  `LIKE` prefix on a default-collation text index is only index-usable with `C` collation /
  `text_pattern_ops`; otherwise it scans the (onec_id, table_name) partition.
- **prune-missing** (`entity.rs:2663-2728`): refuses if stored count is 0 or keys >
  5% of stored (`PRUNE_MAX_FRACTION`, `entity.rs:2428`); `dryRun` supported; match on
  `row_key = ANY OR ref_id = ANY OR raw->>'id' = ANY` (the last arm is unindexed). No count
  decrement. Not transactional across scoped ids.
- **purge_entities** (`entity.rs:2814-2847`): one unbatched `DELETE … WHERE onec_id=$1`
  synchronously in the request, does not touch catalog_entry/catalog_sync/rollups, does
  not cascade to bindings; `delete-status` always says done (`entity.rs:2849`).
- **purge-tables** (`entity.rs:2873-2966`, `store.rs:1099-1246`): durable job rows, detached
  5000-row batches, T4 re-drive. Correct shape.
- **Connection delete** (`api.rs:410`, `store.rs:935-1042`): flag `deleting` (+ children),
  detached recursive purge in 5000-row batches, then side tables, connection row,
  recon_summary. T4 re-drives stuck ones.
- **Orphans**: T3 (`tickers.rs:254-305`) deletes entity_data/entity_count for onec_ids with no
  connection row, unbatched per base.

---

## 7. Multi-org scope (`scope.rs`, `bindings.rs`)

- `Scope::SelfOnly` (default) vs `Scope::Connection` via `?scope=` (`scope.rs`
  `scope_from_query`/`scoped_ids`). Connection scope = parent + all non-deleting children
  (`partition_ids`); asking it on a binding is a 400. Used by counts, refids, refhashes,
  reconcile-recorder, prune-missing (`entity.rs:2161`).
- Reads: reference types widen binding → binding+parent (`scope_shared`,
  `widen_to_shared`); `is_reference_table` = anything not `Document_`/`*Register_`/`stock`.
  resolve-refs uses `widen_to_shared` (`write.rs:275`). Company resolution excludes shared
  and deleting rows (`store.rs:774`, `recon.rs:281-296`) and tests pin that by string match
  (`scope.rs` tests).
- Bindings PUT (`bindings.rs:267-500`): whole-set replace, validates 1 org↔1 company both
  ways (`bindings.rs:95-137`), runs in **one transaction** with `SELECT … FOR UPDATE` on the
  connection; creates binding rows inheriting descriptor fields, vacates stale `deleting`
  slots, flags removed ones for purge, flips `is_shared`. After commit: spawns purges and
  runs `restamp_company` **inline in the request** (20k-row batches, `bindings.rs:194-214`)
  — a large partition can make the PUT exceed client timeouts.
- **Upload does not validate scope**: the server trusts the connector to send each org's rows
  to that org's binding id and shared rows to the parent. A movement row uploaded to the
  shared connection, or a binding receiving another org's rows, is accepted silently.
  `data_hash` folds `companyId`, so a keyless row's hash differs between partitions.
- `reconcile-recorder` / `prune-missing` under `scope=connection` loop per partition with
  exact `onec_id = $1` (deliberate, `entity.rs:2621-2627`, `2751`).

---

## 8. Connector WebSocket hub (`connector.rs`)

- In-memory per tenant: presence `DashMap<connKey, ConnEntry>` (key per socket), routing
  index `oneCId → [connKey]`, `pending` request→oneshot, `pipes` request→cloud socket
  (`connector.rs:1-90`, `Hub` at `188`). Single-process by design — no horizontal scale,
  all state lost on restart.
- Desktop frames: `hello`/`state_update`/`ready`/`agent_ready` → `register` (`connector.rs:1427`),
  reply `hello_ack`; `heartbeat`/`ping` → refresh, `heartbeat_ack` or `need_hello`; DB
  `status_updated_at` throttled to 1 write/60s (`connector.rs:185`). Replies: `*_ack`,
  `*_result`, `error` terminal; `*_progress` ignored (`connector.rs:1298-1312`, `1536`).
- Disconnect: `cleanup` drops presence and demotes bases whose last connection left
  (`connector.rs:1543`).
- Admin dispatch `dispatch_to_infobase` (`connector.rs:443`): choose candidate
  (`choose_candidate` `connector.rs:732`: preferred connector, refuse on ambiguity / duplicate
  identity, prefer a connection advertising the command), insert pending, send, await with
  timeout (sync/read-table 120s, post 90s, write 300s).
- Cloud relay (aiba-next short-lived sockets): `get_connector_bases` answered from presence;
  13 relay actions forwarded verbatim and every reply frame streamed back until the cloud
  socket closes (`connector.rs:1747`). Pipes are reaped after 120s idle / 660s hard
  (`connector.rs:123-130`). Route errors: `requires_base_selection`, `route_not_found`,
  `ambiguous_route`.
- Read relay: on master there is only `pull_now` (connector re-syncs tables) and cloud
  `get_list`/`check_exists`. Live `document_read`/`catalog_read` exist only uncommitted in
  `_wt-onec-read-relay` (see §13).
- Outbound queue per socket is `mpsc::unbounded_channel` (`connector.rs:1194`) — no
  backpressure.

---

## 9. Write support (`write.rs`)

- **resolve-refs** (`write.rs:835`): resolves counterparty, contract, accounts, employee,
  organization, nomenclature from `entity_data` (Postgres cache). Several resolvers pull
  the full entity_type into Rust (`load_raw` `write.rs:282`; contracts ~116k rows on KAN —
  `docs/MIRROR-BACKLOG.md` "Deferred" §1).
- **document/post** (`write.rs:941-1013`): dispatch `post_1c_document {ref_key, doc_id, doc_type}`
  via hub; HTTP 200 with `{ok, posted, number, movements, alreadyPosted, error, retryable, warnings}`.
  No idempotency ledger; retry classification by error string (`write.rs:930-939`).
- **catalog/update** (`write.rs:1264-1376`): `{catalog, refKey, fields, expected?}` →
  `catalog_update`; refuses up front if the picked connector does not advertise
  `catalog_update` or (when `expected` is sent) `catalog_update_cas`. Reply passed through.
  (Merged from `_wt-onec-catupd`.)
- **write_to_1c**: only a `#[allow(dead_code)]` helper (`write.rs:1106-1127`); no REST route.
  Document creation from the cloud goes **only** through the WS cloud relay (aiba-next
  sends `write_to_1c` frames, the hub forwards verbatim). The odoo 4-layer idempotency
  (lease, blind-retry marker, dead-row backstop) is explicitly a TODO scaffold
  (`write.rs:1-60`, marker helpers dead code `write.rs:1136-1215`).
- **Not present as Rust routes**: create document (REST), update document, unpost,
  mark-for-deletion, catalog create. Some exist only as relay action names
  (`create_ref`, `delete_from_1c`, `delete_in_1c`, `push_to_1c`, `connector.rs:832-846`),
  not interpreted by the module.
- **eskey signing**: not implemented. `ESKEY_*` env is read but used only as the tenant
  registry URL; `onec.rs:30` `TODO(eskey)`; no call site signs anything.

---

## 10. Reads served from Postgres

by-type (keyset), entity-data list, last-by-counterparty, match-index and bank-index
artifacts (in-memory gzip cache, TTL + backoff), reconciliation summary/detail/
counterparties/contracts/ledger (summary materialized by T5 into `recon_summary`;
other views fold live), Sotuv catalog search (`catalog_entry`, gated by `catalog_sync`),
retail revenue (`retail_daily_rollup` + coverage), resolve-refs. Known slow:
`/reconciliation/summary` live fold 63s vs 15s upstream timeout on KAN
(`docs/DIVERGENCES.md` U2, `docs/MIRROR-BACKLOG.md` §3).

---

## 11. Jobs / tickers (`tickers.rs:33-213`)

T1 presence sweep (demote stale bases, 60s), T2 pending-request reaper (in-memory, 30s),
T3 orphan entity sweep (600s), T4 re-drive stuck connection purges and table-purge jobs
(120s), T5 recon summary materializer (dirty companies only, 120s), T6 catalog
materializer (dirty bases only, 120s). Plus boot-time background index builder and
catalog seed. No message bus, no outbox, no event emission to other services, no
count recount.

---

## 12. Tests

91 unit tests in-module (`#[test]` counts: connector 45, scope 11, bindings 8, tickers 7,
entity 5, store 5, recon 4, probe 3, write 2, bank_index 1). No `tests/` integration dir,
no DB-backed tests; several "tests" are string-matches over `include_str!` source
(`store.rs:541-546`, `scope.rs` tests). Not run in this audit.

---

## 13. Worktrees

- `next-modules/_wt-onec-catupd` (branch `feat/catalog-update`, HEAD `4c03a4e`):
  **fully merged into master** (`git merge-base --is-ancestor 4c03a4e master` true;
  `master..HEAD` empty). Added `catalog/update` route. Nothing extra.
- `next-modules/_wt-onec-read-relay` (branch `feat/onec-read-relay`, HEAD `708d9bf` =
  master~1, missing `6c46165`). Has **uncommitted** +259 lines in `src/connector.rs`:
  `Hub::chosen_advertises`, `POST …/infobase/:onec_id/documents/read` (`document_read`,
  Document_* table, date range, number, posted_only, limit ≤200, offset) and
  `POST …/infobase/:onec_id/catalog/read` (`catalog_read`, ≤200 ids), 409
  `connector_too_old` when the chosen connector lacks the command, 120s timeout. Not on
  master.

---

## 14. Capability matrix

| Capability | Status | Evidence |
|---|---|---|
| Sync-table discovery | DONE IN RUST | `GET/PUT …/sync-tables` `entity.rs:2355/2380`; manifest `entity.rs:2075` (unauthenticated) |
| Entity batch upload | DONE IN RUST (weak) | `entity.rs:512`; ON CONFLICT upsert by row_key / hash; no txn, no per-row ack, no status check |
| Entity counts | PARTIAL | `entity.rs:2218`; map only grows, no recount despite comments |
| Prune | DONE IN RUST | `entity.rs:2663` with 5% cap + dryRun; no count decrement |
| Recorder reconcile | DONE IN RUST | `entity.rs:2590`; per-recorder delete; LIKE-prefix index risk; no count decrement |
| Org bindings | DONE IN RUST | `bindings.rs:229/267/621`, txn + FOR UPDATE; restamp inline in request |
| Data coverage | DONE IN RUST (racy) | `entity.rs:787`, read-modify-write of `raw` without lock |
| Stock | DONE IN RUST | `entity.rs:700`, txn delete+insert |
| Status | DONE IN RUST | presence via hub + T1 sweep; PATCH status; `GET …/connectors`, `/admin/connectors` |
| Connection lookup | DONE IN RUST | `GET /onec?companyId`, `/onec/:id`, `/by-company/:id`, admin list |
| Rebuild/reset | PARTIAL | full purge `entity.rs:2814` (sync, no side-table cleanup), per-table purge jobs, catalog refresh `catalog.rs:664`; no count rebuild, no recon rebuild endpoint (T5 only), `sync?mode=full` trigger `connector.rs:2206` |
| Write: create document | IMPLEMENTED DIFFERENTLY | only WS cloud relay of `write_to_1c` (`connector.rs:832`, `1747`); REST helper dead code `write.rs:1106`; no idempotency ledger |
| Write: update document | MISSING | no route/action |
| Write: post | DONE IN RUST | `write.rs:941` (`post_1c_document`) |
| Write: unpost | MISSING | no route/action |
| Write: mark-delete | MISSING (relay only) | `delete_from_1c`/`delete_in_1c` relayed verbatim, no module logic |
| Catalog create | IMPLEMENTED DIFFERENTLY | `create_ref` relayed verbatim over WS; no REST |
| Catalog CAS update | DONE IN RUST | `write.rs:1335` with `catalog_update_cas` capability gate |

---

## 15. Weaknesses

Security
1. WS hub unauthenticated by default; any socket can relay `write_to_1c`/`delete_from_1c`
   and can `hello` as any base (`connector.rs:88`, `1136`, `1269`; flag default false `config.rs:138`).
2. No authorization: any valid JWT touches any onec_id/company (`api.rs:136-162`).
3. Multi mode: tenant from `X-Tenant`, JWT not bound to tenant (`api.rs:114-121`).
4. Shared-secret compared with `==` (not constant-time, not HMAC) (`api.rs:138`, `connector.rs:1125`).
5. Manifest endpoint has no auth (`entity.rs:2075`). CORS permissive (`api.rs:536`).
6. Full create body stored in `connection.raw` incl. credentials (`store.rs:718`); only the
   admin view filters by key-name heuristics (`store.rs:647`).

Data integrity
7. Upload not transactional; partial commit then 400 (`entity.rs:612-621`).
8. Uploads accepted into `deleting` connections (`entity.rs:2133` has no status filter) —
   can race the purge and leave rows behind (T3 then catches them only after the row is gone).
9. Count map increment-only, read-modify-write without lock (`entity.rs:2174-2216`); no
   decrement on prune/recorder/stock; no recount ticker; `rebuilt_counts` unused.
10. `data-coverage` and PATCH both rewrite `raw` with no CAS (`entity.rs:787`, `store.rs:880`).
11. Upload does not validate multi-org routing; misfiled rows are silently accepted.
12. Weak ack: per-table counts only, `updated` is not real, `collapsed` hard-coded 0, no
    per-row/batch id echo, no idempotency key.

Performance
13. Upsert rewrites unchanged rows (no `WHERE … IS DISTINCT FROM`) (`entity.rs:432-437`).
14. refhashes loads full `raw` for every row of a table (`entity.rs:2489`).
15. counts does per-table live `count(*)` round trips (`entity.rs:2283-2293`).
16. purge_entities and T3 orphan sweep run one unbatched DELETE (`entity.rs:2818`,
    `tickers.rs:285-296`); purge_entities blocks the request.
17. reconcile-recorder `LIKE` prefix and prune `raw->>'id'` arm not index-backed
    (`entity.rs:2627-2632`, `2751`).
18. resolve-refs folds whole entity types in Rust (`write.rs:282`, MIRROR-BACKLOG Deferred §1).
19. Recon summary live fold 63s on KAN (DIVERGENCES U2).
20. Bindings PUT runs the company restamp inline before responding (`bindings.rs:456-464`).
21. 128 MB body limit and full in-memory gzip decode + `serde_json::Value` parse of the
    whole upload (`api.rs:540`, `entity.rs:556-575`) — memory spike per request, no streaming.
22. Unbounded per-socket mpsc (`connector.rs:1194`); in-memory hub = single instance,
    state lost on restart.

Missing
23. No eskey signing (`onec.rs:30` TODO). No write idempotency/outbox (`write.rs:1-60`).
24. No update/unpost/mark-delete/catalog-create REST endpoints.
25. No DB-backed tests; no schema versioning; no FKs.
