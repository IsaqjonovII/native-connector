# E — Who else reads backend/1c (Python), and what Rust needs before Python can go

Read-only audit, 2026-10-08. No code edited, no server started, no remote API called.
(One slip: a `git fetch --dry-run` in `backend/1c` touched gitlab; it changes no refs.)

## Checkouts used

| Side | Path | Branch @ SHA | Note |
|---|---|---|---|
| Python (reference) | `D:\aiba\backend\1c` | **`origin/production` @ `9b4a50b`** | Local `development` @ `919479b` is **153 commits behind prod**. Prod has routes dev does not: `contract-acts`, `reports/kirim-chiqim`, `adapter/read`, `capabilities/monthly-existing-only`, `register-window`, `onecs/{id}/reconciliation-scope`, `cached-metrics/reconciliation-summary/*`. All Python line numbers below are from `origin/production` (`git show origin/production:<path>`). |
| Rust | `D:\aiba\next-modules\_wt-onec-sync-v1` | `feat/sync-api-v1` @ `4172845` | Router: `src/api.rs:470-547`. Same route set as `next-modules/onec` master; `_wt-onec-read-relay` adds only `catalog/read` + `documents/read`. |
| KANSLER | `D:\aiba\kansler\backend` | `main` @ `0ee273e` | `_wt-*` worktrees ignored. |
| Others | `backend/backend` (`feat/purchase`, call sites also on `origin/production`), `backend/report` (`development`), `backend/soliq` (`production`), `next-modules/report` (`master`), `aiba-next/backend-mcp` (`feat/mcp-onec-tools`), `web/dashboard` (`development`) | | |

Paths below: Python relative to `backend/1c/app/`, Rust relative to `_wt-onec-sync-v1/src/`, kansler relative to `kansler/backend/crates/api/src/modules/`.

**Ignored (not future dependencies):** `cloud-os` (32 files reference aiba-1c / `/ws/1c` / entity-data), `diet-cloud` (19 files). `aiba-cloud-os` is not checked out.

**Nobody reads the Python Mongo directly.** No code in any searched checkout opens `aiba_1c` / `entitydatas`. Only docs and `kansler/backend/scripts/dev/pull_batch.py:11` (a dev script against `localhost:8004`) mention it.

**Not backend/1c, despite the look-alike paths:** `reconciliation/ledger` and half of the `/api/v1/onec` probes in kansler go to `st.onec_1uz` (the 1UZ clone, `1uz.aiba.group`) — `avtoprovodka_1c.rs:1572`, `kansler_bank_1c.rs:1482`, `avtoprovodka.rs:3363`. `reconciliations/*` (plural) goes to soliq (`dashboard.rs:593`). `/api/ai/tool/create-document` goes to didox (`documents.rs:3003`).

---

## 0. Cross-cutting blockers (apply to almost every row)

| # | Problem | Evidence | Fix needed |
|---|---|---|---|
| X1 | **Company id type.** Python keys companies by the chat2 UUID string. Rust only accepts an `i32` and, when given a UUID, fails closed to an EMPTY list (lists) or `400` (reconciliation, retail-revenue). | Rust `api.rs:181-202` (`q_i32`, `company_filter_unrepresentable`), `recon.rs:848,909,976`, `reports.rs:~350` ("must be the tenant company id (int)"). Kansler sends chat2 when it has one: `avtoprovodka.rs:3418-3422`. backend/report sends the AIBA UUID: `services/sverka/onec_client.py:36`. web/dashboard sends `authData.companyId` (UUID): `useOneCConnection.ts:51`. | Callers must send the tenant int id (kansler already falls back to its local id when chat2 is NULL), or Rust must map a UUID alias. Decide before cutover. |
| X2 | **Connection id type.** Python ids are 24-hex Mongo ObjectIds. Rust takes `Path<i64>` / `parse::<i64>()`. | Rust `api.rs:333`, `match_index.rs:139`, `bank_index.rs:206`, `catalog.rs:537`, `entity.rs:878`. Kansler persists the aiba-1c id in its own tables (`api/src/sql/tenant_bootstrap.sql` `km.onec_catalog`, comments at 3490, 3591, 3662, 3769, 4406, 4500, 4676). | One-time remap of every stored `onec_id` in kansler (and report/aiba-next) — or Rust keeps a legacy-id alias column. |
| X3 | **Issued partner tokens + audit.** Python accepts `ODOO_API_TOKEN`, `MCP_API_TOKEN`, `AIBA_BI_API_TOKEN` and writes every issued-token call to `service_token_audits`. Rust accepts one secret or a JWT only. | Python `api/internal/dependencies.py:14-26`, `middleware/odoo_audit.py:1-62`. Rust `api.rs:177-179` (`secret_or_jwt`). | Needed only if Odoo / AIBA BI / MCP stay on this surface. |

---

## 1. KANSLER backend (`kansler/backend`) — the big consumer

Upstream: `st.onec` = `ONEC_API_URL`, default `https://1c.aiba.group` (`api-core/src/config.rs:296`, `upstream.rs:1003`). Per tenant it is overridden by `tenants.onec_module_url` → the Rust module (`api-core/src/tenant.rs:79-92, 726-735`); NULL = central Python. So the Rust module is already a drop-in target for kansler; every MISSING row below breaks the matching kansler feature on a tenant that is cut over.

All callers below are in mounted, live modules (`avtoprovodka`, `kansler_bank*`, `sotuv`, `ved*`, `debts`, `dashboard`, `onec`, `nomenclature`, `corporate_chek`, `ikpu_audit`, `onec_edo_import`, `retail_*`, `warehouse*`). `ikpu_audit/e2e.rs` and `package_audit/e2e.rs` are `#[cfg(test)]` harnesses (`ikpu_audit/mod.rs:12-13`) and are not counted.

| # | Call | Python handler (Mongo) | Kansler caller (feature) | Rust | Status | Must exist before removal? | Compat vs migrate |
|---|---|---|---|---|---|---|---|
| K1 | `GET /api/v1/onec?companyId=` | `api/v1/routes/onec.py:122` (`onecs`) | `avtoprovodka.rs:3358`, `avtoprovodka_1c.rs:614`, `kansler_bank.rs:3573`, `kansler_bank_1c.rs:660`, `ved_sync.rs:198` — base roster / provider probe on every 1C page | `api.rs:472` → `list_connections` `api.rs:226` | PARTIAL — int `companyId` only (X1) | **Yes** — every 1C screen starts here | Caller sends int (X1) |
| K2 | `GET /api/internal/admin/onecs/{id}` | `api/internal/routes/onec.py:129` (`onecs`) | `avtoprovodka.rs:3868`, `kansler_bank.rs:4012`, `nomenclature.rs:568`, `onec_edo_import/hub.rs:356`, `sotuv.rs:4620`, `ved_write.rs:4222` | `api.rs:482` → `api.rs:330` | PARTIAL — `i64` id (X2) | **Yes** | Id remap (X2) |
| K3 | `GET /api/internal/admin/entity-data/by-type` | `api/internal/routes/entity_data.py:450` (`entitydatas`) | ~40 sites: `sotuv.rs:3410…7273`, `ved.rs:163,798`, `ved_write.rs:848…4235`, `ved_order.rs:465`, `ved_receipt.rs:877`, `ved_contract.rs:588,624`, `ved_incoming.rs:236`, `warehouse.rs:4805`, `warehouse_stock.rs:1156`, `onec_stock.rs:315`, `onec_catalog.rs:1139,1213`, `nomenclature.rs:724,769`, `retail_onec_map/routes.rs:1472`, `retail_company_config/onec_refs.rs:36`, `corporate_chek.rs:520`, `kansler_bank_write.rs:1563,3141,3258`, `avtoprovodka_1c.rs:665,697,1477`, `onec_edo_import/hub.rs:390`, … | `entity.rs:870` | PARTIAL — Rust has no `ref_in`, `owner`, `number` filters (Python `entity_data.py:484,521,528`; Rust filter list `entity.rs:910-927`) | **Yes** | Add the 3 filters to Rust. **`ref_in` is silent data corruption:** `nomenclature.rs:761` and `onec_edo_import/hub.rs:381` send `ref_in` with `limit=len(refs)` and get the first N rows of the whole table back. `owner`+`number` (`kansler_bank_write.rs:3249-3255`) fail closed — the caller checks the echo (`:3266-3275`) and refuses. |
| K4 | `GET /api/internal/admin/entity-data` (list) | `entity_data.py:1560` | `onec_catalog.rs:1170` (newest `updatedAt` probe), `nomenclature.rs:825`, `ved_sync.rs:267` | `api.rs:532` → `entity.rs:1194` | PARTIAL — ignores `order_by`/`order_desc`, always `ORDER BY synced_at DESC` (`entity.rs:~1243`) | Yes | Close enough for the freshness probe; document it |
| K5 | `GET .../entity-data/last-by-counterparty` | `entity_data.py:1195` | `sotuv.rs:6039,6230` (sale defaults) | `entity.rs:1524` | DONE | Yes | — |
| K6 | `GET /api/internal/admin/onec/{id}/match-index` | `api/internal/routes/match_index.py:43` (`onec_match_index` + `_chunks`) | `avtoprovodka_1c.rs:1092`, `kansler_bank_1c.rs:1134` (check-1c scan) | `match_index.rs:136` (in-memory, 409/503 protocol) | DONE (X2 aside) | Yes | — |
| K7 | `GET .../onec/{id}/bank-index` | `match_index.py:86` | `kansler_bank_1c.rs:1233` | `bank_index.rs:203` | DONE | Yes | — |
| K8 | `GET .../onec/{id}/nomenclature-index` (+ `/head`) | `api/internal/routes/nomenclature_index.py:47,80` (chunked gzip artifact) | `nomenclature.rs:1023,1036` | none | MISSING | No (caller walks by-type instead, `nomenclature.rs:1045-1066`) — but that walk is the slow path, and it needs K3 `ref_in` | Port or accept the slow walk |
| K9 | `GET .../onec/{id}/write-metadata/operation-kinds` and `/documents` | `api/internal/routes/write_metadata.py:432,490` | `kansler_bank_optypes.rs:380`, `retail_onec_map/service.rs:564,572` (operation-type picker, retail 1C mapping) | none (Rust only *reads* the dump internally, `write.rs:34,659`) | MISSING | **Yes** | Port |
| K10 | `POST /api/internal/admin/resolve-refs` | `api/internal/routes/resolve_refs.py:1236` | `avtoprovodka.rs:10730`, `kansler_bank.rs:8834`, `sotuv.rs:7386`, `retail_onec_map/fetch.rs:152`, `service.rs:351` | `write.rs:835` | DONE (see C/B; folds whole tables, perf) | Yes | — |
| K11 | `POST .../connectors/infobase/{id}/document/post` | `api/internal/routes/connectors_admin.py:222` | `avtoprovodka.rs:10102`, `kansler_bank.rs:8250`, `onec_write.rs:730` | `write.rs:941` | DONE | Yes | — |
| K12 | `POST .../infobase/{id}/document/update` | `connectors_admin.py:293` | `onec_write.rs:817` ← `sotuv.rs:13056` (edit header of an existing 1C doc) | none | MISSING | **Yes** | Port |
| K13 | `POST .../infobase/{id}/catalog/update` | `connectors_admin.py:311` | `avtoprovodka.rs:8836`, `ikpu_audit/hub.rs:430` | `write.rs:1335` | DONE | Yes | — |
| K14 | `POST .../infobase/{id}/catalog/read`, `/documents/read` | `connectors_admin.py:429,412` | `ikpu_audit/hub.rs:442`, `onec_edo_import/hub.rs:233,294` (EDO draft import, IKPU audit) | only on branch `_wt-onec-read-relay` (`feat/onec-read-relay` @ `708d9bf`, `src/connector.rs`) | PARTIAL — unmerged | **Yes** | Merge the branch |
| K15 | `POST .../infobase/{id}/adapter/read` | `connectors_admin.py:519` | `retail_onec_map/relay_read.rs:99`, used by `retail_post/relay_reader.rs:40-91` (verify/recover of retail writes) | none | MISSING | **Yes** — without it every relay write lands `unverified` | Port |
| K16 | `GET .../infobase/{id}/capabilities/monthly-existing-only` | `connectors_admin.py:40` | `corporate_chek.rs:1998-2000` (fails closed) | none | MISSING | **Yes** for corporate-chek monthly writes | Port (tiny) |
| K17 | `POST .../infobase/{id}/sync`, `/read-table` | `connectors_admin.py:90,106` | `onec_pull.rs:167,178` (used by `corporate_chek`, `ikpu_audit/hub`, `retail_onec_map/routes`) | `connector.rs:2202,2234` | DONE | Yes | — |
| K18 | `GET/PUT /api/v1/onec/{id}/sync-tables` | `api/v1/routes/onec.py:860,885` | `onec_pull.rs:92,142`, `ved_sync.rs:583` | `entity.rs:2355,2380` | DONE (X2) | Yes | — |
| K19 | `GET /api/v2/reconciliation/summary` | `api/v2/routes/reconciliation.py:322` (`entitydatas` + Redis) | `dashboard.rs:970,1141`, `onec.rs:265,395,463,501` (Akt-sverka), `debts/onec_source.rs:1027` (qarzlar) | `recon.rs:968` | PARTIAL — int company (X1); known parity gaps listed in `recon.rs:30-45` (no chart-name fallback for pre-2026-06 rows) | **Yes** | X1 |
| K20 | `GET /api/v2/reconciliation/counterparties` | `reconciliation.py:142` | `dashboard.rs:1160` | `recon.rs:840` | PARTIAL (X1) | Yes | X1 |
| K21 | `GET /api/v2/reconciliation/detail/{cp}` | `reconciliation.py:495` | `onec.rs:603`, `debts/onec_source.rs:1137` | `recon.rs:1307` | PARTIAL (X1) | Yes | X1 |
| K22 | `GET /api/v2/reconciliation/contracts` | `reconciliation.py:194` | `debts/onec_source.rs:1319` | `recon.rs:901` | PARTIAL (X1) | Yes | X1 |
| K23 | `GET /api/v2/reconciliation/document-contracts` | `reconciliation.py:246` | `debts/onec_source.rs:837` | none | MISSING | **Yes** (qarzlar contract linkage) | Port |
| K24 | `GET /api/v2/reconciliation/register-contracts` | `reconciliation.py:296` | `debts/onec_source.rs:902` | none | MISSING | **Yes** | Port |
| K25 | `GET /api/v2/reconciliation/contract-acts` | `reconciliation.py:474` (prod only) | `debts/onec_source.rs:1378` | none | MISSING | **Yes** | Port |
| K26 | `GET /api/v2/reconciliation/document/{number}` | `reconciliation.py:529` | `avtoprovodka_1c.rs:1393`, `debts/onec_source.rs:1060`, `ved_write.rs:4285` | none | MISSING | **Yes** | Port (keep the `:path` converter — numbers contain `/`) |
| K27 | `GET /api/internal/admin/reports/kirim-chiqim` | `api/internal/routes/reports_kirim_chiqim.py:348` (prod only; reads `entitydatas` docs) | `onec.rs:1091` (VAT month / «1C/Soliq sverka») | none | MISSING | **Yes** | Port |
| K28 | WS `/api/v2/connector/ws` | `api/connector_ws.py:99` | `api-core/src/tenant.rs:763` (write channel), `onec_proxy.rs:210` (tunnel for connectors) | `connector.rs:1132` | DONE (see B) | Yes | — |
| K29 | `ANY /api/v2/1c/*` → `/api/v2/*` | — (pass-through) | `onec_proxy.rs:207` — connector REST through kansler | whatever Rust serves under `/api/v2` | n/a | — | Note: the proxy only reaches `/api/v2/*`; a connector routed through it cannot reach `/api/v1/onec/...` (sync-tables, prune-missing). See A. |

---

## 2. Other backend consumers

| # | Call | Python handler | Caller (feature, live?) | Rust | Status | Must? | Compat vs migrate |
|---|---|---|---|---|---|---|---|
| R1 | `GET /api/v2/reports/retail-revenue` | `api/v2/routes/reports.py:105` (`retail_daily_rollup`, raw fallback) | **backend/report** `services/sverka/onec_client.py:60` ← `service.py:161`, `store_alias.py:54` (central sverka, live) | `reports.rs:328` | PARTIAL — int `company_id` (X1); `interval=day` only; rollup-only (no raw fallback for uncovered days); `use_cache`/`cache_max_age_minutes` ignored | Yes while central backend/report serves tenants | Migrate tenants to **next-modules/report**, which already calls Rust with the int id (`next-modules/report/src/upstream.rs:77`, `docs/DIVERGENCES.md` D1) → DONE there |
| R2 | `GET /api/internal/admin/onecs?company_id=&limit=50` | `api/internal/routes/onec.py:26` | backend/report `onec_client.py:91` ← `marketplace.py:127,286` (marketplace leg of sverka) | `api.rs:345` | PARTIAL (X1 → empty) | Only if Python report stays | Rust report has **no** marketplace leg at all — feature gap in the report port, not in onec |
| R3 | `GET .../entity-data/by-type` (`COUNTERPARTIES`+`inn`; `SALES_OF_GOODS_AND_SERVICES`+`counterparty`+date range+`fields`, `limit` 5000) | `entity_data.py:450` | backend/report `onec_client.py:113,137,178` | `entity.rs:870` | DONE for these filters (X2 aside) | Same as R2 | — |
| S1 | `GET /api/v1/onec?companyId=&page&size` | `v1/routes/onec.py:122` | **backend/soliq** `services/v1/onec_ws_service.py:225` ← `routes/v1/mehnat.py:274` (`POST /employees/unified-list`, `mehnat.py:247`) | `api.rs:472` | PARTIAL (X1) | No — no caller of `unified-list` found in kansler / aiba-next / web / connector (likely dead UI path) | Drop or migrate |
| S2 | `GET /api/internal/admin/entity-data/employees` | `entity_data.py:343` | soliq `onec_ws_service.py:242` (same flow as S1) | none | MISSING | No (same reason) | Drop with S1 |
| S3 | `GET /api/v2/qqs/summary` | `api/v2/routes/qqs.py:44` | soliq `routes/ai/tool.py:652` (`qqs_calculation`, `:771`) ← backend/backend LangGraph `core/langgraph/tools/soliq.py:328` | none | MISSING | No — soliq falls back to local reconciliation data (`ai/tool.py:640-645`) | Accept fallback or port |
| B1 | `GET /api/ai/tool?action=metric\|daily_summary\|connections\|provodka` | `api/ai/tool.py:68` (`cached_metrics` via `get_metric_data`, `onecs`) | **backend/backend** `services/onec.py:92` ← `core/langgraph/tools/one_c.py:83` (OneCAgent, `agents/specialized.py:150-166`) and `tools/analytics.py:28` (daily summary). `provodka` is not a Python action → 400 today (`ai/tool.py:79-90`) | none; no cached-metric engine in Rust | MISSING | Only if the legacy AI agent is kept | Needs the statistics engine (W2) first |
| B2 | `GET /api/internal/admin/stats` | `api/internal/routes/stats.py:15` | backend/backend SQLAdmin dashboard `admin/views/dashboard.py:90` (best-effort, swallows errors) | none | MISSING | No | Drop |
| B3 | SQLAdmin remote views: `onecs` list/get/PATCH/DELETE, `entity-data` list/get/stats/DELETE, `cached-metrics` list/get/DELETE/DELETE-all, `entity-counts` list/get/DELETE | `onec.py:26,129,189,260`, `entity_data.py:1560,1639,1708,1737`, `cached_metrics.py:148,207,232,247`, `entity_counts.py:19,72,95` | backend/backend `admin/services/onec_client.py:204-357`, registered `admin/app.py:300` (superadmin ops panel, live) | only `onecs` list/get (`api.rs:481-482`) and `entity-data` list (`entity.rs:1194`) | MISSING (mostly) | No — ops tool, not product | Drop or rebuild against Rust later |
| B4 | `GET {AIBA_ONEC_API}/company/{id}/statistics` | **no such route** in Python | backend/backend `api/v1/company.py:1017` | — | DEAD (404 today) | No | Delete caller |
| W1 | `GET /api/v2/onec?companyId=` | `api/v2/routes/onec.py:97` | **web/dashboard** `shared/hooks/useOneCConnection.ts:51`, `useConnectionStatus.ts:56`, `contexts/SelectedConnectionContext.tsx:73` (base = `VITE_APP_DASHBOARD_BASE_URL`, `.env` → `http://localhost:8004/api/v2`; prod value lives in CI vars, not in repo) | `api.rs:473` | PARTIAL (X1) | Only if the legacy MFE dashboard stays live — **liveness unverified** | — |
| W2 | `GET /api/v2/statistics/{main,profit,sales,expenses,cash}/*` (~35 metrics) | `api/v2/routes/statistics.py:14-530` (`cached_metrics`) | web/dashboard `features/dashboard/hooks/useStatisticEndpoint.ts:116`, `DashboardLineChartWithEndpoint.tsx:151,184`, `DashboardBarChartWithEndpoint.tsx:132,165` | none | MISSING (whole metric engine) | Same as W1 | `/statistics/expenses/top-counterparty-expense` is already a 404 on Python |
| W3 | `/api/v2/cloud-onec/*` (connections, clobus discover/connect, sync, stream) | `api/v2/routes/cloud_onec.py:37-294` (+ beat `sync_all_cloud_onec`) | web/dashboard `features/cloud-onec/api/cloudOnecService.ts:40` | none | MISSING | Same as W1 | — |
| W4 | `/company/{id}/statistics` | none | web/dashboard `shared/api/company-statistics-service.ts:277` | — | DEAD | No | — |
| M1 | `GET .../connectors/infobase/{id}`, `onecs/{id}`, `GET /api/internal/connector/manifest`, by-type, `.../sync`, `POST /cloud-write`, `.../document/update`, `/unpost`, `/delete` | `connectors_admin.py:83,90,293,281,529`, `onec.py:129`, `internal/routes/connector.py:51`, `entity_data.py:450`, `cloud_write.py:233` | **aiba-next/backend-mcp** `mcp_ext/onec.rs:129,142,315,380,508,654,682,709,738`; uses `MCP_API_TOKEN` for audit (`api-core/src/upstream.rs:480-491`) | manifest `entity.rs:2075`, by-type, sync, onecs/{id}; the rest none; no token audit (X3) | PARTIAL | No — branch-only, local `:18011`, not deployed | Re-target when MCP ships; unpost/delete are excluded from minted grants anyway |
| O1 | External partners (Odoo `ODOO_API_TOKEN`, AIBA BI `AIBA_BI_API_TOKEN`): `odoo-inbox` POST/GET, `resolve-refs`, `build-write-payload`, `cloud-write`, `cloud-write-fact`, `onecs`, `onecs/by-inn`, `onecs/resolve-by-inns`, `onecs/{id}`, `entity-counts`, `entity-data/stats`, `entity-data/by-type`, `entity-data`, `entity-data/{id}`, `entity-data/employees`, `last-by-counterparty`, `read-table` | `odoo_inbox.py:206,269`, `build_write_payload.py:467`, `cloud_write.py:233`, `cloud_write_fact.py:524`, `onec.py:106,115`, … | Outside our repos. Contract: `docs/odoo-integration.md:51-54,214,376`, `docs/1c-read-api.md:103-232`. Live usage can only be proven from prod `service_token_audits` (not read). | `resolve-refs`, `onecs` list/get, by-type, `entity-data`, `last-by-counterparty`, `read-table` only; no `odoo-inbox`, no `build-write-payload`, no `cloud-write*`, no issued tokens (X3) | MISSING (write path + auth) | **Yes if Odoo still posts documents** (memory: KAN Odoo→1C path verified 2026-07-16) | Needs a decision with the partner; check `service_token_audits` first |

---

## 3. backend/1c background jobs that serve other services

Beat schedule: `core/celery/app.py:355-445`. Rust tickers: `tickers.rs:1-25` (T1 presence, T2 reaper, T3 orphans, T4 purges; explicitly NOT ported: `auto_update_onec`, `sync_all_cloud`, Odoo backstop/health, `refresh_all_reports`).

| Job | Python | Serves | Rust | Status |
|---|---|---|---|---|
| `refresh_all_reports` 03:00 + `build_retail_rollups` every 30 min | `app.py:~405,~416`; `core/celery/reports.py:166,205` | R1 retail-revenue (report sverka) | Rollup is filled only by the connector push `reports.rs:74` (`/retail-rollup/ingest`); no server-side builder; uncovered days are reported, not computed | PARTIAL — fine for tenants whose connector pushes; Python-only bases lose history |
| `check_and_clear_cache` → `check_one_onec_cache` (40 cached metrics) | `core/celery/api.py:345-394` | W2 statistics, B1 AI tool | none | MISSING (only matters if W2/B1 survive) |
| `drain_builders` (match / nomenclature index builds) every 5 min | `app.py:~433` | K6, K8 | match index built in-process on demand (`match_index.rs:1-15`); nomenclature none | PARTIAL |
| `sync_all_cloud_onec_connections` every 30 min | `app.py:~375` | W3 cloud-onec | none | MISSING (only if W3 survives) |
| `process_odoo_inbox_backstop`, `odoo_inbox_health` every 60 s | `app.py:~386,~394` | O1 Odoo | none | MISSING (with O1) |
| `check_onec_heartbeat`, `purge_stuck_onec`, `retruth_entity_counts`, `auto_update_onec` | `app.py:358-445` | internal / connector | T1/T2/T4; retruth + auto_update not ported | see B |

---

## 4. Python routes with NO active caller found → "do not port" candidates

Method: string search for each route across `aiba-next/backend`, `aiba-next/frontend/src`, `connector/apps/app/src{,-tauri/src}`, `1c-arch/src`, kansler backend+frontend, backend/{backend,report,soliq}, `web/dashboard/src`, `backend-mcp`, `next-modules/report` (tests, docs, `_wt-*` excluded). Zero hits everywhere unless noted. External partners (O1) can only be ruled out from `service_token_audits`.

| Route(s) | Python | Note |
|---|---|---|
| All `/api/v1/statistics/*` incl. `cache/{company_id}`, `clear-all`, `fix-ownership` | `api/v1/routes/statistics/*.py` | web/dashboard uses the **v2** base; v1 only appears in a docstring (`backend/backend/app/services/onec.py:127`) |
| `/api/v2/statistics/income/*` | `api/v2/routes/statistics.py:369,381` | not used even by web/dashboard |
| All `/api/v1/entities/*` | `api/v1/routes/entity.py:28-188` | connector uses v2 |
| `GET /api/v2/entity`, `GET /api/v2/entity/{id}`, `POST /api/v2/entity` | `api/v2/routes/entity.py:246,279,291` | only `/upload`, `stock-snapshot`, `data-coverage`, `oneC/*` are used |
| `POST /api/v2/onec/connection/backfill-total-count`, `/connection/rebuild` | `api/v2/routes/onec.py:234,255` | admin one-offs |
| `/api/v2/qqs/outgoing`, `/incoming`, `/by-invoice` | `qqs.py:58,72,86` | only `/summary` has a caller (S3) |
| `GET /api/v2/reports`, `GET /api/v2/reports/{key}` for keys other than retail-revenue, `POST /{key}/refresh`, `GET /jobs/{job_id}` | `reports.py:75,105,266,386` | |
| `POST /api/internal/admin/reports/retail-rollup/build`, `/build-from-turnover`; `POST /webhooks/report-refresh` | `reports_rollup.py:37,82`, `reports_webhook.py:50` | ops only |
| `cached-metrics/reconciliation-summary/*` (5 routes), `cached-metrics/fix-ownership` | `cached_metrics.py:21-130,296` | |
| `GET /onecs/{id}/reconciliation-scope`, `PUT /onecs/{id}/extra-tables` | `onec.py:156,227` | |
| `GET /onecs/by-inn`, `/onecs/resolve-by-inns` | `onec.py:106,115` | documented for partners only (O1) |
| `POST /cloud-write-fact`, `POST /build-write-payload` (backend side) | `cloud_write_fact.py:524`, `build_write_payload.py:467` | partners only (O1); the connector's `build-write-payload` string is its own feature name |
| `POST /odoo-inbox`, `GET /odoo-inbox/{id}`, `GET /audit/odoo` | `odoo_inbox.py:206,269`, `audit.py:25` | Odoo only (O1) |
| `GET /api/v2/connector/ping` | `connector_ws.py:45` | |
| WS `/ws/1c` (legacy cloud router) | `cloud_router_ws.py:918` | serves cloud-os (ignored); soliq's `ONEC_WS_URL` default points at `cloud.uic.aiba.uz`, not here (`backend/soliq/app/core/config.py:110`) |
| `/api/v2/cloud-onec/*` | `cloud_onec.py` | only web/dashboard (W3) — drop together with the legacy MFE |
| `GET /api/internal/admin/stats`, `entity-counts/*`, `entity-data/stats`, `entity-data/{id}`, `DELETE`s, `cached-metrics` CRUD | see B2/B3 | backend/backend SQLAdmin only |

---

## 5. Bottom line — what Rust must have before Python is switched off

**Must have (live KANSLER features break without them):**
1. X1 company-id and X2 connection-id compatibility (or a migration of every caller + stored id).
2. by-type `ref_in`, `owner`, `number` filters (K3) — `ref_in` today returns wrong rows silently.
3. Reconciliation: `document/{number}`, `document-contracts`, `register-contracts`, `contract-acts` (K23-K26).
4. `reports/kirim-chiqim` (K27).
5. Connector relay routes: `document/update` (K12), `adapter/read` (K15), `capabilities/monthly-existing-only` (K16), and merge `catalog/read` + `documents/read` from `feat/onec-read-relay` (K14).
6. `write-metadata/operation-kinds` + `/documents` (K9).

**Decide, then port or drop:**
- Odoo / AIBA BI partner surface (O1 + X3): `odoo-inbox`, `build-write-payload`, `cloud-write*`, issued tokens, audit. Check prod `service_token_audits` first.
- Central backend/report (R1-R3): either Rust accepts its UUID, or all tenants move to next-modules/report (already int-keyed; lacks the marketplace leg).
- Legacy web/dashboard MFE + backend/backend LangGraph 1C agent (W1-W3, B1): needs the whole cached-metric engine in Rust. Cheaper to retire them.

**Can drop:** soliq employees/QQS reads (S1-S3, caller has fallbacks or no UI caller), backend/backend SQLAdmin views (B2-B3), dead routes (B4, W4), everything in section 4.
