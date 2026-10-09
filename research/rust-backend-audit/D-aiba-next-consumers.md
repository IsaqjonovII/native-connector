# D — aiba-next as a consumer of backend/1c (Python) vs the Rust onec module

Read-only audit, 2026-10-08. aiba-next backend `D:\aiba\aiba-next\backend` (HEAD of the checkout,
branch `feat/soliq-turnover-limit`; `onec_meta.rs` is untracked work in progress). Python reference:
`backend/1c` `development` @ 919479b, and `origin/production` @ 9b4a50b where prod differs. Rust:
`next-modules/_wt-onec-sync-v1` (`feat/sync-api-v1`). cloud-os ignored. KANSLER is in file E.

Status: **DONE** = Rust answers the same contract today · **PARTIAL** = works, with a named
difference · **MISSING** = no Rust route · **FIXED 10-08** = gap closed in this phase (C).
"Before removal" = must exist before Python is removed. "Migrate" = change the caller instead of
keeping compatibility.

## 0. How aiba-next reaches the 1C backend

- Request handlers use `scoped_for` → the tenant's own `onec_module_url` (Rust). A tenant without one
  gets a sealed upstream (502), never Python (`tenant.rs:766-776, 870-888`).
- **Three background jobs bypass that and still call central Python** (`ONEC_API_URL`, default
  `aiba-1c.aiba.uz`, chat2 company key): `avtoprovodka_check_scheduler.rs:104-105` (every 10 min),
  `nomenclature.rs:2854-2855`, `kansler_bank_write.rs:906-907` (also writes over the global
  `ONEC_WS_URL` = `wss://1c.aiba.group`). **Before removal: caller fix (`scoped_for`, ~3 lines each).**
  On module tenants they already hit the wrong service today.
- `st.onec_1uz` (1UZ ledger/stock) is a different service — out of scope.

## 1. Connection records, status, coverage

| Call (caller) | Python | Rust | Status | Before removal / compat |
|---|---|---|---|---|
| `GET /api/v1/onec?companyId=` (`onec_link.rs:88` base picker, `kansler_bank.rs:3672`) | `v1/routes/onec.py:122`; hides shared + deleting (`NOT_SHARED`) | `api.rs:226`, `store.rs:841`; int company id; lists shared multi-org rows and `deleting` rows (on purpose, for the Connector) | PARTIAL | Yes. Migrate caller: `normalize_onec_bases` drops `isShared`/`deleting` (~3 lines). `kansler_bank.rs:3713` must use `onec_company_key`, not chat2 |
| `GET /api/internal/admin/onecs/{id}` (sotuv `mirror_reach`, onec_meta, override path) | `internal/routes/onec.py:129` (full model dump, leaks `passwordEncrypted`) | `api.rs:330`, `store.rs:728` (flat + raw, secrets stripped) | PARTIAL | `dataCoverage`, `status`, `version`, `syncTables` match. `lastSyncStartedAt/FinishedAt`, real `percentage` missing — only backend-mcp (not deployed) reads them |
| **Base online (`status`)** — read by `normalize_onec_bases` → `is_online` (picker auto-select, warehouse base choice) | hub socket presence | only the OLD Connector's hub socket set `status`; `/api/sync/v1/…/status` never did, and the new Connector never called it | **FIXED 10-08** | Rust status route now sets `active` / `inactive` on the connection and its bindings (never `deleting`); the Supervisor reports `active` every 60 s and `stopped` on a clean stop; T1 demotes after 180 s silence. Live test `TheStatusHeartbeatPutsTheBaseAndItsBindingsOnline…` |
| `GET /api/internal/admin/connectors`, `/api/v2/onec/{id}/connectors` (roster, picker `write_ready`) | Redis presence | hub sockets only | PARTIAL | New Connector not listed → `write_ready=false` → picker falls back to `is_online` (now correct). Listing the new Connector is a managed-Connector feature (later) |
| `dataCoverage` (sotuv `mirror_reach_from_onec`, match-index `orderCoverage`) | `onecs.dataCoverage.<table>` | `onec.connection.raw.dataCoverage` (sync v1 coverage route + legacy route) | DONE | Multi-org: the partition the Connector reports on must be the id aiba-next reads; sotuv treats a missing entry as Whole (fail-open), match-index as incomplete. The Connector reports to every partition of a movement table (R6 proven on KAN) |
| `GET /api/v2/onec/{id}/counts` | 403 for a service caller (ownership dep) | `entity.rs:2218` | DONE (Python broken) | — |
| `sync-tables`, `refids`, `refhashes`, `preferred-connector`, `connector/manifest` | 404 on v2 (v1 only) / exists | Rust | DONE (Rust-only) | callers already on Rust |

## 2. Documents / entities / counts

| Call (caller) | Python | Rust | Status | Before removal / compat |
|---|---|---|---|---|
| `GET …/entity-data/by-type` (avtoprovodka source-counts + onec_txn list + check-1c; nomenclature sweep; onec_catalog ticker; sotuv check-zakaz, catalog mirror, kontragent search; kansler_bank clones; warehouse catalog core) | `internal/routes/entity_data.py:450` | `entity.rs:875` | DONE for every site except below | Callers read only `items[].rawData`, `total`, and the echoes |
| by-type for `customer-orders` / `trading-points` / `inquiry-sources` (`onec_entities` slugs, type only) | venkon map has `CUSTOMER_ORDER`, `TRADING_POINTS`, `INQUIRY_SOURCES` | missing in the venkon map → rows stored with the table name as type → 0 rows; `is_reference_type("CUSTOMER_ORDER")` mis-widened | **FIXED 10-08** | Mappings added; one-time relabel of stored rows (`relabel_venkon_types_20261008` in `onec.sync_meta`) |
| by-type `ref_in`, `owner`, `number` (+ `owner`/`number` echoes) | prod: `ref_in` (≤500, 400 on bad), `rawData.Владелец`, `rawData.Номер`, echoes | ignored → whole partition returned (silent wrong data) | **FIXED 10-08** | Same semantics + echoes; unit test `ref_in_parses_like_the_python`. Callers are KANSLER (file E) |
| by-type `counterparty=` | index seek | `raw->>'Контрагент'` scan (cp_key index unused) | PARTIAL (perf) | `sotuv.rs:3432` on a 124k-row ЗаказПокупателя partition risks the 15 s default timeout. Not a correctness gap; tune before a big tenant cuts over |
| `GET /api/internal/admin/entity-data` (nomenclature + onec_catalog freshness, `onec_meta` freshness) | `total = count()` | `total = items.len()` | **FIXED 10-08** | Real count over the same filters. `order_by` still ignored (always `synced_at DESC` = what callers ask) |
| `…/entity-data/last-by-counterparty` | `entity_data.py:1203` | `entity.rs:1524` | DONE | — |
| `match-index`, `bank-index` | `match_index.py` | `match_index.rs`, `bank_index.rs` | DONE | — |
| `catalog/search`, `by-inn`, `by-name` | 404 (caller falls back) | `catalog.rs` | DONE (Rust-only) | — |
| Stock (`STOCK_BALANCE`) | — | — | N/A | read from `onec_1uz` only |
| Ids | ObjectId (24 hex) connection ids, chat2 UUID company ids | `i64` connection ids, int company ids | — | **Cut-over data migration**, not code: any id aiba-next stored (`km.nomenclature_sync`, `km.onec_catalog_sync`, local overrides, didox `provodka_result` infobase ids) must be remapped to the Rust id. See the retirement plan |

## 3. Reconciliation / dashboard (avtoprovodka-adjacent reads)

| Call (caller) | Python | Rust | Status | Before removal / compat |
|---|---|---|---|---|
| `/api/v2/reconciliation/summary` (Akt-sverka list + table, MCP, dashboard debtors) | computed per request, cached; `lastSyncedAt`/`syncStale` fresh on every read | materialized `onec.recon_summary`; sync fields frozen at build time | **FIXED 10-08** (sync fields) | `lastSyncedAt` = max(stored, connections' `recon_dirty_at`) per request, `syncStale` vs now. Cold miss still folds live (>15 s dashboard timeout on a big base) — PARTIAL perf |
| `/reconciliation/detail/{cp}` | cached | uncached fold | PARTIAL (perf) | — |
| `/reconciliation/contracts` (onec_meta, untracked WIP) | `byCounterparty` variant | flat only | PARTIAL | caller not shipped |
| `/api/internal/admin/reconciliation/ledger` (onec_meta, untracked WIP) | none | Rust: sale/purchase only | contract mismatch | settle before onec_meta ships |
| Dashboard pending-provodka widget | keys never existed | same | BROKEN in both | migrate caller |

## 4. Writes (avtoprovodka, sotuv, kansler_bank, chek, payroll)

| Flow (caller) | Python | Rust | With the OLD Connector | With the NEW Connector |
|---|---|---|---|---|
| `resolve-refs` | Mongo reads | `write.rs:835` | DONE | DONE (reads `entity_data` only) |
| `…/document/post` (avtoprovodka/kansler post-1c, onec_write post-existing; 6 callers) | hub WS `post_1c_document` (+ OData for cloud bases) | hub WS only | DONE (no OData path for cloud bases) | **does not reach it** — the new Connector holds no hub socket; returns `connector_offline` |
| WS relay `write_to_1c`, `check_exists`, `get_list`, `create_ref`, `get_connector_bases` (sotuv, avtoprovodka, corporate/kansler chek, payroll, kansler bank) | Python relay | Rust relay `connector.rs:1743` | DONE (frames pass through) | **does not reach it**; the command queue has no such kinds |
| `read-table`, `sync` triggers (onec_pull) | hub | hub | DONE | not applicable (the new Connector syncs on its own) |

**Write-path conclusion.** Python removal does NOT depend on the new Connector: the Rust module's
hub already relays every aiba-next write for the OLD Connector. What the NEW Connector cannot yet do
is serve aiba-next's existing write flows: they speak the old Connector's protocol (`write_to_1c`
builds the 1C document from `provodka_result` inside the old Connector's oscript). The R9 command
queue carries ready 1C bodies only. Bridging is a product decision (see the retirement plan, item W).

## 5. Security findings (not caused by this phase)

- **aiba-next `/api/v2/1c/*` proxy** (`onec_proxy.rs:144-179`): any logged-in tenant user, any company,
  is forwarded with `X-Service-Secret` — full service access to the tenant's onec module (e.g.
  `DELETE /api/v2/1c/entity/oneC/{id}`). Possible `%2e%2e` escape to `/api/internal/admin/*` (unverified).
  `connector_ws` checks only the JWT signature. Committed in HEAD. Raised as a separate task.
- **Rust command queue**: lease/report used to accept any caller with access to the connection.
  **FIXED 10-08**: every lease hands out a per-command token (sha256 of an OS-random secret and the id);
  a report must quote the current one (`lease_not_held` otherwise). A user with access to the company
  can still LEASE (the desktop Connector signs in with the user's own JWT); separating the Connector's
  credential from the user's is managed-Connector work (later).

## 6. Must-have list for Python removal (aiba-next side)

| # | Item | Where | State |
|---|---|---|---|
| 1 | Three background jobs on `scoped_for` | aiba-next | open (caller) |
| 2 | Base online from the new Connector | Rust + Connector | FIXED 10-08 |
| 3 | entity-data `total` | Rust | FIXED 10-08 |
| 4 | venkon types CUSTOMER_ORDER / TRADING_POINTS / INQUIRY_SOURCES | Rust | FIXED 10-08 |
| 5 | Recon `syncStale` / `lastSyncedAt` fresh | Rust | FIXED 10-08 |
| 6 | `/api/v1/onec` hides shared/deleting for the picker | aiba-next caller | open (caller, ~3 lines) |
| 7 | `kansler_bank.rs:3713` on `onec_company_key` | aiba-next | open (1 line) |
| 8 | Id remap (connection ObjectId → i64, chat2 → int company) | data migration | open (cut-over step) |
| 9 | Proxy auth hole | aiba-next | open (separate task) |
