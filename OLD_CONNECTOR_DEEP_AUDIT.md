# Old AIBA Connector: deep audit (final, reviewed)

Read-only reverse-engineering of the old AIBA Connector and the cloud code that drives it,
compared with this rewrite. Nothing was run, called or changed in any audited repo. Secrets
are referenced by location only.

Companion files: `OLD_CONNECTOR_FEATURE_MAP.md` (one row per feature) and
`OLD_CONNECTOR_HIDDEN_FEATURES.md` (only the non-obvious items).

## Scope and method

### Checkouts

| Repo | Path | Ref read | Note |
|---|---|---|---|
| Connector (Tauri + React + oscript) | `D:\aiba\connector` | `release` @ `cfab3a1` (2026-09-30), app 1.3.109 | equals `origin/release`; working tree has one modified `1uz-adapter/adapter.version` and untracked `claudetest/` |
| backend/1c (control plane, sync backend) | `D:\aiba\backend\1c` | `origin/development` @ `b2e3590` (authoritative) | local `development` @ `8107bc0` is an ancestor, **58 commits behind**; line numbers in this file are from `origin/development` unless marked "local" |
| backend/backend (main API) | `D:\aiba\backend\backend` | `feat/purchase` @ `53b24b35` (2026-05-12), `origin/development` grepped | stale branch; has no connector control code |
| Rewrite | `D:\aiba\1c-arch` | working tree 2026-10-03 | read only (only these three files were added) |

Path prefixes used below: `connector:` = `D:\aiba\connector\apps\app\src\` (TS), `tauri:` =
`D:\aiba\connector\apps\app\src-tauri\src\`, `main.os:` =
`connector/apps/app/src-tauri/resources/1c-adapter/main.os`, `router:` =
`connector/adapter-router/src/main.rs`, `b1c:` = backend/1c `origin/development`, `arch:` =
`D:\aiba\1c-arch\`.

### Who did what

Six investigators wrote reports (A frontend, A2 frontend helpers, B Rust/Tauri, C adapter
routes, D backend/1c contract and sync tables, E control plane and presence, F cross-cutting
discovery). This reviewer read every report fully, cross-checked them, and re-opened the code
for every contradiction and for the 15 highest-stakes claims (section "Verification log"
below). Where a claim was not re-opened it is attributed to its investigator and marked
"(reported, not re-verified)".

### Verification log (top claims re-opened in code by the reviewer)

| # | Claim | Verdict | Evidence read |
|---|---|---|---|
| V1 | Connector forces `exchange=true` on 9 HR document types | VERIFIED | `connector:providers/onec-sync-provider.tsx:4688-4715` (set + `url.searchParams.set("exchange","true")`); callers `:6799`, `:7116`, `:12465-12917` |
| V2 | Both connector WebSockets open with no token; identity self-declared incl. `role:"admin"`, `tenant_id:"default"` | VERIFIED | `connector:providers/onec-hub.tsx:107`, `onec-sync-provider.tsx:15275`, `:1493-1505` |
| V3 | Hub socket has no auth; any socket that sends a cloud action is treated as a cloud client and routed to connectors | VERIFIED | `b1c:app/api/connector_ws.py:9-11,99-130`; action set `b1c:app/api/cloud_router_ws.py:50-68` (includes `write_to_1c`, `employee_fire_from_1c`, `delete_from_1c`, `create_ref`) |
| V4 | `get_connector_bases` returns `user_bases` and `all_bases` (whole fleet) | VERIFIED | `b1c:app/api/cloud_router_ws.py:491-531` |
| V5 | Hub drops connector `error` and `refdata_response` frames (only `result/ack/*_result/*_ack/*_progress` are relayed) | VERIFIED | `b1c:app/api/connector_ws.py:189-203`; connector sends `action:"error"` `onec-sync-provider.tsx:1761-1767`, `refdata_response` `:7520,7537,7613` |
| V6 | Socket close deletes the shared presence record and every route, although the comment says it only clears routes still pointing at this connector | VERIFIED (and sharpened: the comment/code mismatch is new) | `b1c:app/core/connector_hub.py:406-430` |
| V7 | Heartbeat re-registration after TTL loss checks `infobases`, connector sends `bases` | VERIFIED | `b1c:app/api/connector_ws.py:174`; register path accepts both via `_normalize_infobases` (`connector_hub.py:94`, `:276`) |
| V8 | IP stored is the raw socket peer, no `X-Forwarded-For` handling in the hub | VERIFIED | `connector_ws.py:102`, `connector_hub.py:314`; the only XFF read in backend/1c is `app/middleware/odoo_audit.py:94` |
| V9 | Adapter router binds `[::]` dual-stack (all interfaces), CORS `*`; Rust default config `host 0.0.0.0`, `enableAuth:false`; inbound firewall allow rule with no remote-IP scope | VERIFIED | `router:2717-2725`, `router:1878-1888`; `tauri:handlers/filesystem.rs:1093-1104`, `:1248-1253` |
| V10 | Adapter has no auth at all (F) vs optional bearer auth, off by default (C) | CORRECTED: C is right | `main.os:21360-21374` (`ПроверитьАвторизацию`), called at `main.os:22217`; off unless `api.enableAuth=true` |
| V11 | Cloud `create_ref` spreads `extras` raw into a catalog create; adapter honours `_postCreateMethod` (calls any common-module method, twice), and catalog creates always use `DataExchange.Load=True` | VERIFIED | `onec-sync-provider.tsx:7168,7220`; `main.os:7150-7180`, `:7136-7142` |
| V12 | Cloud can hard-delete a document (`delete_1c_document {hard:true}`) | VERIFIED | `onec-sync-provider.tsx:5845,5870`, `:5213-5219`; backend route `b1c:app/api/internal/routes/connectors_admin.py:384-393` |
| V13 | Relay tunnel forwards any method/path to the signer admin API `:7777` or `:6210` | VERIFIED | `tauri:handlers/tunnel_agent.rs:47-55`, `:404-439`; URL with token in query `:170-182` |
| V14 | Event-log sync hard-disabled | VERIFIED; A's line numbers CORRECTED | `connector:utils/dev-settings.ts:80-86` (A said 63-69) |
| V15 | Device id = Windows MachineGuid, cached in WebView localStorage and never re-read | VERIFIED | `tauri:utils/fingerprint.rs:1-7`; `connector:utils/device-id.ts:4-14` |
| V16 | `ensure_adapter_dir` kills the live router (LISTENING processes on 55899-55924 named oscript/adapter-router) and `get_adapter_pool_config` calls it | VERIFIED in code; runtime effect unverified | `tauri:handlers/filesystem.rs:86-119`, `:1022-1028`, `:2364-2369` |
| V17 | Router default mode | CORRECTED: ownership is the default; the file header comment saying shard is stale | `router:434-448` vs header `router:25-26` |
| V18 | Registered Tauri commands: 82 (B) vs 77 (F) | CORRECTED: 77 | `tauri:lib.rs:806-882` |
| V19 | `get_signer_health`, `scan_base_heal_status` are called from the UI (B) | CORRECTED: neither is invoked; only a type comment and a doc comment mention them | `connector:types/signer.ts:111`, `components/adapter/heal-panel.tsx:9` |
| V20 | Unauthenticated `POST /api/v2/onec/connection/rebuild` (fleet cache wipe) and `backfill-total-count` | VERIFIED | `b1c:app/api/v2/routes/onec.py:234-285`; router has no `dependencies`, `main.py` adds only CORS + Odoo audit middleware |
| V21 | Any JWT can `DELETE /api/v1/statistics/cache/clear-all` (incl. entities and connections) | PARTLY VERIFIED: route has only `get_current_user`; deletion body not re-read | `b1c:app/api/v1/routes/statistics/cache.py:157-168` |
| V22 | Sync tables: GET returns `null` for `[]`; PUT strips everything but `{table, reports}` | VERIFIED | `b1c:app/api/v1/routes/onec.py:804-830`, `:881` |
| V23 | `prune-missing` capped at 5 % of stored rows | VERIFIED | `b1c:app/api/v1/routes/onec.py:28`, `:755-761` |
| V24 | Whole-base `sync_now` has no fast ack (only targeted runs ack) | VERIFIED | `onec-sync-provider.tsx:2044-2068` |
| V25 | Partner tokens (Odoo, MCP, BI) are full peers of the service secret on admin routes incl. hard delete | VERIFIED | `b1c:app/api/internal/dependencies.py:10-50` |
| V26 | `employee_list_from_1c` is routed by the hub but has no connector handler | VERIFIED | hub set `cloud_router_ws.py:50-68`; grep in `connector:` = 0 hits |
| V27 | Release builds ship DevTools | VERIFIED | `connector/scripts/updater.ps1:277-284` (`--features devtools`), reached from `scripts/release.ps1:253` → `run-updater.ps1:493` |
| V28 | Bank reconnect deletes the cloud subscription first; NBU payments auto-sign on a timer | VERIFIED (one bank checked) | `connector:app/(dashboard)/banks/page.tsx:1463-1471`; `banks/payments/page.tsx:106-129, 374-400` |
| V29 | Heal panel runs Designer `/UpdateDBCfg` on the customer base; `list_infobase_users` reads `v8users` with trust auth | VERIFIED | `tauri:handlers/heal_extconn.rs:543`; `tauri:handlers/onec_cluster.rs:14-21, 298` |
| V30 | `/query` swallows errors (`200 success:true, data:[]`) | VERIFIED | `main.os:19311-19314` |

Claims not re-opened (attributed in place): the `activeChart` race, `postBankDocWithFallbacks`
duplicates, the hire result reporting success on failure, Sentry PII fields, the Kapital
webview scripts, `usb_test_disable_one`, the 1uz adapter internals, the aiba-next tunnel
findings (E §5b), and every runtime/prod-reachability question.

---

## 1. What the old Connector actually does (end-to-end flows)

The old Connector is three cooperating programs on the accountant's PC plus a control plane
in backend/1c.

1. **Tauri shell (Rust)** — starts and supervises a local 1C REST adapter, a bank signer
   sidecar, a Python SQB sidecar and an optional relay tunnel; owns the Windows side
   (firewall rule, COM registration, elevation, autostart, logs).
2. **React webview** — does almost all business logic: login, the sync engine (poller,
   orchestrator, uploader), the cloud command dispatcher (15k-line
   `onec-sync-provider.tsx`), 11 bank integrations and the HR/document write builders.
3. **oscript adapter** (`main.os`, 23,902 lines, one COM connection per process) behind a
   Rust `adapter-router.exe` on `:55899`. It exposes a generic REST API over every 1C object
   kind, plus write routes with find-or-create.

### Flow F1 — scheduled 1C → cloud sync (ACTIVE)
`InfoBaseSyncProvider` (`connector:features/accounting/providers/info-base-sync-provider.tsx:330,1002`)
→ `SyncPoller` (one base per tick, ~60 s, 3 failures → 5 min quarantine, skips bases with a
pending write) → `SyncOrchestrator.syncSingleInfoBase` (`lib/sync/sync-orchestrator.ts:386`)
→ table list `GET {1c v1}/onec/{id}/sync-tables` merged with compiled defaults and mandatory
tables → ordered by `tableSyncPriority` (`core/config.ts:330-361`) → per table: count gate
against `GET /counts?scope=connection`, refhash diff, probe scan, page reads from the adapter
(`utils/incremental-fetcher.ts`, 500/page, adaptive, poison-row skip) → upload
`POST {1c}/entity/upload` multipart (`lib/sync/chunk-uploader.ts:229`) → delete detection via
`POST /prune-missing` and register repair via `POST /reconcile-recorder`
(`utils/backend-refids-fetcher.ts:97,235`). Side lanes per cycle: stock snapshot, data
coverage, 1uz ledger, and the reports lane (separate oscript worker `:55949`, retail turnover
push). Watchdog aborts a run after 10 min without progress.

### Flow F2 — cloud write into 1C (ACTIVE)
Odoo inbox / cloud-os avtoprovodka / aiba-next → backend/1c `dispatch_to_infobase`
(`b1c:app/core/connector_hub.py:587-672`) or the cloud router (`cloud_router_ws.py:704-910`)
→ Redis pub/sub `connector-cmd:{connectorId}` → hub socket worker → connector
`dispatchControlCommand` (`onec-sync-provider.tsx:14905-15138`) → global write gate
(`utils/write-gate.ts`, 90 s) → builder (`handleWriteTo1CCommand :11718`) reads the chart of
accounts, configuration version, schema, learns department → `POST adapter
/api/db/{b}/documents/{type}[?post=false][&exchange=true]` → adapter `СоздатьДокумент`
(`main.os:15520-17538`) with idempotency marker lookup, find-or-create refs, autofill, post,
fallbacks → `write_progress` every 10 s → `write_to_1c_result` → Redis
`connector-resp:{requestId}` → caller.

### Flow F3 — admin actions on existing documents (ACTIVE)
`POST /api/internal/admin/connectors/infobase/{id}/document/{post|unpost|update|delete}`,
`/catalog/{update|read}`, `/documents/read` (`connectors_admin.py:168-393`, service secret or
any partner token) → same hub path → connector handlers `:5594` (post, 75 s), `:5697`
(unpost), `:5752` (update), `:5834` (delete, `hard` passed through), `:13180` (catalog update
with CAS), `:13378/:13489` (reads, ≤200 rows).

### Flow F4 — remote sync trigger (ACTIVE)
`POST …/infobase/{id}/sync?mode=cursor|full` → `sync_now`; `…/read-table` and the Celery
report refresh (daily 03:00 and on demand) → `pull_now {tables}` → `runSyncNowCommand`
(`onec-sync-provider.tsx:1896`). Targeted runs ack fast and preempt; whole-base runs only
answer at the end (usually after the 120 s dispatch timeout).

### Flow F5 — presence (ACTIVE, three signals)
(a) WebSocket `hello`/`heartbeat`/`state_update` every 30 s from two sockets sharing one
`connector_id = user_id:MachineGuid` → Redis `connector:{id}` hash + `conn-of-infobase:{oneCId}`
routes (90 s TTL); (b) HTTP `PATCH {1c}/onec/connection/{id} {status, totalCount}` every 60 s
(`connector:providers/connection-status.tsx:126,345`) → Mongo `OneC.status`, demoted after
3 min by Celery; (c) upload activity `sync-of-infobase:{id}` (120 s).

### Flow F6 — local setup (ACTIVE)
Startup: Rust silent cleanup, HKCU Run key, tray, global Alt+B, elevation check → UI
`get_adapter_config_status` → `start_adapter` → `ensure_adapter_dir` (copies resources,
backs up config) → firewall rule → COM registration → router + workers. DB config modal:
`discover_databases` (`main.os --discover` reads `ibases.v8i`), `test_adapter_connections`
(per-base connect test that can flip the machine-wide comcntr registration),
`list_infobase_users` (reads 1C user names without credentials), `add_adapter_database`
(plaintext password into `config.json`).

### Flow F7 — bank keys and signing (ACTIVE, outside 1C)
Signer sidecar `bank-connector.exe` on `:7777/:6210/:13589`, 11 bank pages, NBU payment
signing through Styx, and an outbound relay tunnel to `relay.aiba.uz` that lets the backend
call the signer admin API and send signing frames.

---

## 2. Feature inventory by subsystem

Full matrix in `OLD_CONNECTOR_FEATURE_MAP.md`. Summary:

- **Auth / account**: legacy `/api/v1` login (phone+password), refresh on 401, aiba-next
  `/api/v2` mode when the base URL contains `/api/v2` (`connector:services/api.ts:42-52`),
  companies, token keepalive that also pushes the bearer to the local signer every 60 s
  (`lib/aiba-token-keepalive.ts`).
- **Adapter lifecycle**: start/stop/restart, watchdogs (Rust 1 s, UI 15 s with 90 s
  unresponsive restart), pool config (ownership/shard, max workers), reports worker,
  partition workers (flagged), 1uz .NET adapter on `:55900`.
- **Infobase configuration**: discovery from `ibases.v8i`, connection tests with
  comVersion detection, users list, bulk-connect mode (hidden Ctrl+Shift+T), heal panel.
- **Sync**: poller, orchestrator, adaptive paging, hash diff, probe scan, prune and
  reconcile, stock snapshot, data coverage, retail turnover, 1uz ledger, sync-tables drawer
  with auto-seed, multi-org routing, entity purge, export to file.
- **Cloud command handling**: 25 action names plus aliases (section 3).
- **Banks**: 11 banks, keys/chip activation, NBU payments with periodic auto-sign, SQB
  Playwright sidecar, relay tunnel.
- **Diagnostics**: debug-logs window (Alt+B), Sentry (renderer + Rust), dev settings modal,
  devtools in release.
- **Platform**: autostart, tray, updater (GitHub latest.json, forced modal for minor/major).

---

## 3. Control plane command registry

Auth legend: **SS** = `X-Service-Secret` accepting any of `SERVICE_SECRET_KEY`,
`ODOO_API_TOKEN`, `MCP_API_TOKEN`, `AIBA_BI_API_TOKEN` (`b1c:app/api/internal/dependencies.py:10-50`);
**NONE** = unauthenticated hub socket. Connector handler lines are
`connector:providers/onec-sync-provider.tsx`. P1 = `dispatch_to_infobase` (120 s, writes 300 s,
post 90 s; first non-progress frame wins; no retry, no dedup). P2 = cloud router (client
`request_id`, per-socket in-flight dedup only; 120 s idle, +45 s per progress frame, 600 s
ceiling). Transport for both: Redis pub/sub, no persistence, no replay
(`connector_hub.py:539-640`).

| Command | Sender / backend route | Handler | Args | Reply | Timeout | Ack / retry / dedup | Auth | Status |
|---|---|---|---|---|---|---|---|---|
| `sync_now` | `POST /api/internal/admin/connectors/infobase/{id}/sync?mode=` (`connectors_admin.py:69-82`); aiba-next/kansler `onec_pull.rs`, runbooks | `:14925` → `:1896` | `mode` cursor/full, optional `tables` | `sync_now_ack` (targeted only), `sync_now_result` | 120 s P1 | whole-base run usually times out at 120 s while it keeps running; `sync_in_progress` refusal | SS | ACTIVE |
| `pull_now` | `…/read-table` (`:85-90`); Celery `tasks.refresh_report` (daily 03:00, `POST /api/v2/reports/{key}/refresh`) | same | `tables[]`, `priority` | `pull_now_ack` then orphaned `pull_now_result` | 120 s | ack terminal; preempts other lanes unless `priority:low` | SS / JWT / cron | ACTIVE |
| `write_to_1c` / `write_doc_to_1c` | Odoo inbox `services/odoo_write.py:702-728`; cloud-os avtoprovodka JS, aiba-next `onec_write.rs`, provodka-ai (P2) | `:14959` → `:11718` | `documents[]`, `doc_type`, `post_document`, base selector | `write_to_1c_progress`, `write_progress` (10 s), `write_to_1c_result` | 300 s P1 / 600 s P2 | Odoo retries up to 8; idempotency is the `Комментарий` marker in the adapter | SS / NONE | ACTIVE |
| `post_1c_document` / `post_doc_in_1c` | `…/document/post` (`:168`) → `services/onec_post.py` | `:14972` → `:5594` | `doc_type`, `ref_key` | `post_1c_document_result` | 90 s | 75 s connector deadline | SS | ACTIVE |
| `unpost_1c_document` | `…/document/unpost` (`:227`) | `:14980` → `:5697` | `doc_type`, `ref_key` | `*_result` | 120 s | — | SS | ACTIVE, destructive |
| `update_1c_document` / `edit_1c_document` | `…/document/update` (`:239`) | `:14985` → `:5752` | `fields`, `auto_unpost` | `*_result` | 120 s | — | SS | ACTIVE |
| `delete_1c_document` | `…/document/delete` (`:384-393`) | `:14990` → `:5834` | `hard` | `*_result` | 120 s | unposts first | SS | ACTIVE, destructive (hard = physical) |
| `catalog_update` | `…/catalog/update` (`:257`), capability-gated | `:15109` → `:13180` | `catalog`, `ref_key`, `fields`, `expected` (CAS) | `catalog_update_result` | 120 s | CAS against live card | SS | ACTIVE |
| `document_read` | `…/documents/read` (`:358-372`) | `:15114` → `:13378` | table, dates, number, org, ≤200 | `document_read_result` | 120 s | read only, no write gate | SS | ACTIVE |
| `catalog_read` | `…/catalog/read` (`:375-381`) | `:15119` → `:13489` | catalog, ids ≤200 | `catalog_read_result` | 120 s | read only | SS | ACTIVE |
| `employee_write_to_1c` | P2 (soliq Mehnat via legacy relay, NC HR) | `:14945` → `:6173` | person + hire | `*_progress`, `*_result` (reports success even if the hire document failed — A, not re-verified) | P2 | write gate | NONE | ACTIVE (reachability via relays doubtful, E §7.15) |
| `employee_fire_from_1c` | P2 | `:15124` → `:6846` | — | `*_result` | P2 | sent with `exchange=true` | NONE | ACTIVE, not advertised |
| `employee_list_from_1c` | P2 (soliq, aiba-next read lane allowlist) | **none** | — | never answered | P2 | buffered then times out | NONE | BROKEN |
| `check_exists` | P2 | `:14930` → `:4359` | department / schedule / employee / document | `check_exists_result` | P2 | — | NONE | ACTIVE |
| `delete_from_1c` / `delete_doc_from_1c` | P2 | `:14935` → `:5939` | only `AIBA_(LEAVE|PREMIUM|DEDUCTION)` markers | `delete_from_1c_result` | P2 | write gate | NONE | ACTIVE, destructive |
| `push_to_1c` / `create_in_1c` | P2 | `:14995` (departments `:14385`, schedules `:13743`) | resource_type | `push_to_1c_result` | P2 | no existence check; other types fall through to the hidden `/documents` queue | NONE | ACTIVE / PARTIAL |
| `delete_in_1c` / `department_delete_in_1c` | P2 | `:15060` → `:14310` | departments | `delete_in_1c_result` | P2 | — | NONE | ACTIVE, destructive |
| `holiday_push_to_1c` / `holiday_delete_in_1c` / `holiday_check_1c` | P2 | `:15047-15058` | production calendar rows | `*_result` | P2 | — | NONE | ACTIVE |
| `list_resource` | P2 (bases answered by the hub itself) | `:15020-15097` | schedules / departments / employees / refdata | `list_resource_result`; refdata branch replies `refdata_response` (dropped by hub) | P2 | — | NONE | ACTIVE; refdata BROKEN |
| `get_list` | P2 (aiba-next sotuv, NC HR) | `:15099` → `:7256` | enum / calcTypes / catalog with projection | `get_list_result` | P2 | — | NONE | ACTIVE, not advertised |
| `create_ref` | P2 | `:15104` → `:7159` | catalog, display, raw `extras` | `create_ref_result` | P2 | write gate; **extras reach `_postCreateMethod`** | NONE | ACTIVE, SECURITY RISK |
| `server_ping` / `ping` | P2 (fan-out to all connectors without a target) | `:14910` | — | `server_pong` | 120 s | — | NONE | ACTIVE |
| `get_bases` | hub answers locally | `:14920` | — | `get_bases_result` | — | — | NONE | connector branch UNUSED |
| `refdata_get` / `get_refdata` | not in hub action set | `:15090` → `:7500` | — | `refdata_response` (cache replays old `request_id`) | — | — | — | UNREACHABLE on the new hub |
| `department_write_to_1c` | not in hub action set | `:15015` | — | `push_to_1c_result` | — | — | — | UNREACHABLE (legacy relay only) |
| `connector_status_request` / `status_request` | no sender | `:15129` | — | forces `state_update` | — | — | — | DEAD (useful hook) |
| `read_table` | no sender | not handled | — | — | — | — | — | DEAD |
| `need_hello` (hub → connector) | `connector_ws.py:165,178` | only `onec-hub.tsx:156`; the command socket ignores it | — | `hello` | — | — | — | PARTIAL |
| relay tunnel `{http:{method,path,…}}`, `{frame}`, `refresh_inventory`, `activate_chip` | `relay.aiba.uz` (server not in these repos) | `tauri:handlers/tunnel_agent.rs:336-468, 586-622` | any method/path to `:7777` or `:6210`; Styx/iABS7 frames | `{request_id, response}`; inventory every 8 s | 25 s HTTP, 15 s Styx | token in URL query | per-PC token | ACTIVE when bank key manager is on; `activate_chip` emits an event nobody listens to (DEAD) |

Hello advertises 19 commands plus the CAS capability token
(`onec-sync-provider.tsx:1719-1745`). The hub stores them and uses them only for
`catalog/update`, `documents/read`, `catalog/read` gating (`connector_hub.py:481-501`).
Unknown frames go to a 500-entry store queue (`onec-sync-provider.tsx:15330`, `stores/onec-sync.ts:3`)
whose only consumer is the hidden `/documents` page.

---

## 4. Remote support and debug capabilities

What staff can do today without touching the customer PC:

| Capability | How | Live / async | Limits |
|---|---|---|---|
| See which connectors and bases are online | `GET /api/internal/admin/connectors`, `…/infobase/{id}` (SS); `get_connector_bases` (NONE) | live Redis snapshot | online only, 90 s TTL, forgotten when offline; fields limited (section 6) |
| Prove the socket is alive | `server_ping` | live | proves the webview socket only, not COM or 1C |
| Trigger a sync | `sync`, `read-table`, reports refresh | ack live, data async via upload | no per-request status; whole-base sync answers after 120 s timeout |
| Read live rows | `documents/read`, `catalog/read`, `get_list`, `check_exists` | live, 120 s | ≤200 rows, document/catalog only, queues behind sync on a single-threaded worker |
| Read stored rows / counts | `/api/internal/admin/entity-data/*`, `/onec/{id}/counts` | as fresh as last sync | — |
| Post / unpost / update / delete documents | admin routes | live | hard delete possible; no ownership guard on the connector side |
| Edit catalog item with CAS | `catalog/update` | live | — |
| Change table list | `PUT /api/v1/onec/{id}/sync-tables` (user or SS); `PUT /onecs/{id}/extra-tables` (no effect on connector since 2026-08-09) | applies on next run (≤ ~60 s) | `schedule` dropped by backend |
| Wipe / rebuild cached metrics | `POST /api/v2/onec/connection/rebuild` | async | unauthenticated (security) |
| Bank signer actions | relay tunnel | live | any path on `:7777` |

What is **not possible remotely**: logs, `debug.log`, adapter errors per base (the router
hides the worker `/api/status`), resource usage, adapter or app restart, app update, config
change, connection test, platform version, adapter version, event-log state. Sentry receives
errors only if the build had a DSN (unknown for production Rust builds, B §8).

---

## 5. Sync-table system end to end

1. **Source of truth**: `OneC.syncTables` on the connection record
   (`b1c:app/models/mongodb_models.py:67-86`), entries `{table, reports}` only. Exposed only
   on v1: `GET/PUT /api/v1/onec/{id}/sync-tables` (`b1c:app/api/v1/routes/onec.py:860-885`).
   Service callers skip ownership.
2. **Connector fetch**: every poller run, every cloud `sync_now/pull_now`, every reports-lane
   run (`connector:utils/sync-tables-config-fetcher.ts:46`, 10 s timeout, never throws; failure
   → compiled defaults).
3. **Resolution** (`lib/sync/sync-tables-config.ts:174`): stored list, else
   `resolveTablesForInfoBase(provider, version)` (`core/config.ts:592`), always unioned with
   `MANDATORY_MAIN_TABLES_BY_PROVIDER` (venkon GTD/import tables), lanes split by `reports`,
   policy overrides from `sync-policy.ts` (РозничнаяВыручка and ОтчетОРозничныхПродажах →
   reports lane; `AccountingRegister_*` → night 21:00-07:00 with a 36 h stale fallback;
   `Document_ЧекККМ` manual only).
4. **Auto-seed**: when the backend answers `null`, the poller PUTs the effective compiled list
   back once per base per session (`info-base-sync-provider.tsx:179-226`).
5. **Edits**: drawer `PUT` full replace including `schedule` (`sync-tables-drawer.tsx:168-176`)
   — the backend strips `schedule` (`_clean_sync_tables`, `onec.py:815-830`).
6. **Propagation**: polling only; no WS command, no revision field.
7. **Removal**: a removed table stops syncing; its stored rows stay forever and still appear
   in `/counts`.
8. **Older generation**: `GET /api/internal/connector/manifest` + `OneC.extraTables`
   (unauthenticated) — no longer read by the connector since `c3fc269` (2026-08-09) but still
   served and proxied by aiba-next (D §5.1).

Traps (all VERIFIED in V22 or reported by D): `[]` cannot be stored as "sync nothing" (it
reads back `null` and is re-seeded); `schedule` never persists; seeding writes compiled
defaults + mandatory tables as if the user chose them; aiba-next calls a non-existent
`/api/v2/onec/{id}/sync-tables` (D, not re-verified).

---

## 6. Presence and fleet

Per field: sent by the connector? observed by the backend? persisted? exposed to admins?

| Field | Sent | Observed / stored | Persisted | Exposed |
|---|---|---|---|---|
| connectorId (`user_id:device_id`) | yes, both sockets | `connector:{id}` | Redis 90 s | admin `/connectors` (SS) |
| deviceId (MachineGuid) | yes; also `X-Device-Id` on main API HTTP | Redis; main API audit log | Redis 90 s; backend/backend audit | admin list; main admin audit |
| userId | yes (self-declared) | Redis | 90 s | admin list; `get_connector_bases` (NONE) |
| company | per base `company_ids` | inside `infobases` JSON; Mongo `OneC.companyId` | Redis / Mongo | admin list |
| app version | `app_version` | `appVersion` | Redis 90 s | admin list; routing tie-break (highest wins) |
| capabilities | hello only | `commands` | Redis 90 s | admin list |
| bases + status | `bases[]` from adapter `/api/databases/status` | normalized `infobases` | Redis 90 s | admin list |
| 1C configuration version | HTTP PATCH `version` | `OneC.version` | Mongo | internal `/onecs` |
| session_id, app_mode, tenant_id, role, protocol_version, connected_base_count | yes | dropped | — | — |
| adapter version, platform version, OS, hostname, Windows user, connectedSince, reconnect counts, resource stats | no | — | — | — |
| last seen / offline history | implicit | `lastSeen` only while online; record deleted on close | none | online only |
| HTTP status heartbeat | `PATCH /onec/connection/{id}` 60 s | `OneC.status`, `statusUpdatedAt` | Mongo; demoted after 3 min | main admin `RemoteOneCAdmin`, cloud UIs |

**IP, five cases:**
1. *Sent by the connector?* No. No IP, hostname or interface list is in any frame.
2. *Observed by the backend?* Yes: `ws.client.host` of the hub socket (`connector_ws.py:102`).
3. *Real client IP?* Unverified. No `X-Forwarded-For` handling in the hub (V8); behind the
   edge it is the proxy address unless uvicorn runs with forwarded-allow-ips, and the swarm
   command is not in the repo. The aiba-next Rust hub does read `x-forwarded-for`
   (`next-modules/onec/src/connector.rs`, E §5b, not re-verified).
4. *Persisted?* Only in Redis `connector:{id}.ip` for 90 s; overwritten by whichever of the two
   sockets last said hello; deleted on close. Logged at INFO on every hello
   (`connector_ws.py:145-150`).
5. *Exposed?* Only through the SS admin `GET /connectors` list. No UI shows it.

There is **no fleet UI** anywhere: no cross-company list of installations, versions or last
seen (E §5). Presence forgets a machine the moment its socket closes.

---

## 7. Device identity

- **Source**: `machine_uid::get()` = `HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid`
  (`tauri:utils/fingerprint.rs:1-7`), no salt, no hash.
- **Cache**: zustand persist key `aiba-connector-store` in WebView2 localStorage
  (`%LOCALAPPDATA%\aiba.uz\EBWebView`), returned forever once set (`connector:utils/device-id.ts:4-14`).
- **Use**: `connector_id = user_id:device_id`; `X-Device-Id` on every main-API call
  (`services/api.ts:59-61`). No installation id exists.

| Question | Answer |
|---|---|
| Stable across restart / update? | Yes (same GUID, same WebView profile, identifier `aiba.uz`). |
| Stable across reinstall? | Yes in value (GUID re-read). |
| Stable across AIBA logout / other AIBA user? | device_id yes; connector_id changes with user_id. |
| Different Windows user on the same PC? | Same device_id; separate cache; same connector_id if the same AIBA user logs in → two sockets fight for one identity. |
| Machine migration / cloned VM? | New GUID on a new or sysprepped Windows; **cloned images without sysprep share one identity**; a copied user profile carries a stale cached GUID that is never re-checked. |
| Multiple instances? | Blocked per session by the single-instance plugin (mutex has no `Global\`), so two RDP sessions can each run one; ports are machine-wide and their sweeps kill each other's adapters (B §6, code inference). |
| Is identity proven to the backend? | No. Self-declared; any client can claim any `connectorId` (V3). |

---

## 8. Adapter as a data API (route summary)

Full table: investigator C. 57 routes plus CLI flags. All under `/api` and
`/api/db/{base}/…`; a request **without** a base prefix runs against the first active base
(`main.os:2948-2956`).

| Group | Routes | Callers |
|---|---|---|
| Service | `/`, `/api/status` (re-reads config each call; router answers it in pool mode), `/api/databases[/status]`, `/api/worker`, `/api/metadata`, `/api/counts`, `/api/healScan`, `/api/openapi` | status, databases/status, counts, metadata, healScan ACTIVE; others UNUSED |
| Catalogs | list, page (offset / UUID keyset / projection / XDTO bulk / filters), by id, POST create (find-or-create, Load mode, `_postCreateMethod`), PUT, DELETE (`hard`) | ACTIVE except hard delete |
| Documents | list, page (date keyset, window, filters), by id, batch, POST create (`post`, `exchange`, `_idempotencyMarker`, `_monthlyUpsert`), PUT (`autoUnpost`), DELETE (`hard`), `/post`, `/unpost` | ACTIVE |
| Enums, charts, calcTypes | list and values | values ACTIVE |
| Registers | accounting / accumulation / information pages (cursor, `to`, bulk), info-register POST/PUT/DELETE, accumulation `/turnover` | ACTIVE |
| Business | `/stock`, `/sotuv/istochnik-map` | stock ACTIVE; sotuv UNUSED here |
| Introspection | `/schema[/catalogs|documents|registers]`, `/constants[/{name}]`, processes, tasks, exchange plans, characteristics | full schema + document schema ACTIVE; rest UNUSED |
| Raw | `POST /query` (any 1C query text, errors swallowed), `/hs|/http` proxy, `/sql-dump` (bcp via `WScript.Shell`), `/db-structure`, `/changes` | `/query` used only with fixed internal texts; others UNUSED; `/changes` without base returns 501 |

---

## 9. Write and action capabilities (with callers)

| Capability | Adapter route | Cloud caller | Local caller |
|---|---|---|---|
| Create document (post by default) | `POST documents/{t}` | `write_to_1c` (Odoo, avtoprovodka, aiba-next, provodka-ai), HR hire/fire | `/documents` page (hidden), queue fallthrough |
| Update document | `PUT documents/{t}/{id}` | `update_1c_document` | — |
| Post / unpost | `/post`, `/unpost` | `post_1c_document`, `unpost_1c_document`, deletes | — |
| Delete (mark or hard) | `DELETE documents/{t}/{id}[?hard]` | `delete_1c_document`, `delete_from_1c` | — |
| Create catalog item | `POST catalogs/{n}` | `create_ref`, employee, department, schedule, find-or-create inside document writes | — |
| Update catalog item | `PUT catalogs/{n}/{id}` | `catalog_update` (CAS) | — |
| Delete catalog item | `DELETE catalogs/{n}/{id}` (soft) | departments/employees | — |
| Info-register write/delete | `POST|PUT|DELETE registers/info/{n}` | holidays, departments | — |
| Bank account auto-create | inside document create | bank `write_to_1c` (`postBankDocWithFallbacks`) | — |
| Configuration change | Designer `/LoadConfigFromFiles /UpdateDBCfg` | none | Heal panel |
| 1C users read | `1Cv8.1CD` parse / `psql -w` / `sqlcmd -E` | none | DB config modal |

Write-mode facts: HR documents always get `exchange=true` (V1); incoming bank documents whose
post fails with an accounting-policy error are force-flagged posted in Load mode
(`main.os:17194-17252`, so likely posted with no movements); catalog creates always run in
Load mode (V11); a failed create+post leaves a draft (C, `main.os:17051-17062`); the marker
lookup matches deletion-marked documents and fails open (C, `main.os:12716-12766`).

---

## 10. Backend / admin features and their consumers

| Feature | Route | Consumers |
|---|---|---|
| Connection CRUD | `/api/v2/onec` POST/GET/PATCH/DELETE (async purge) | connector forms, heartbeat, delete menu |
| Entity upload and side ingests | `/api/v2/entity/upload`, `stock-snapshot`, `data-coverage`, `/reports/retail-rollup/ingest` | connector sync |
| Diff surface (v1 only) | `/counts`, `/refids`, `/refhashes[?digest|buckets]`, `/reconcile-recorder`, `/prune-missing`, `/sync-tables` | connector sync |
| Multi-org | `/api/v2/onec/{id}/org-bindings` GET/PUT/DELETE | connector org-mapping modal |
| Entity purge | `DELETE /api/v2/entity/oneC/{id}` + `delete-status` | connector row menu |
| Connector admin | `/api/internal/admin/connectors*` | aiba-next, kansler, provodka-ai, MCP, runbooks |
| Write pipeline | `/resolve-refs`, `/build-write-payload`, `/cloud-write`, `/odoo-inbox` | Odoo, cloud-os, aiba-next |
| Indexes built from synced rows | `/onec/{id}/match-index`, `bank-index`, `nomenclature-index`, `write-metadata/*` | write pipeline |
| Reports / statistics / reconciliation / QQS | `/api/v2/statistics/*`, `/reconciliation/*`, `/qqs/*`, `/reports/*` | cloud UIs (data comes from connector sync) |
| Celery | heartbeat demotion 60 s, auto cache 60 s, retruth 6 h, purge, report refresh 03:00 (causes connector pulls), Odoo backstop 60 s, cloud OData sync 30 min | — |
| Clobus cloud 1C (no connector) | `/api/v2/cloud-onec/*` | cloud UIs |
| backend/backend | `/connections/ws` (connector never connects; `bank.credentials` relay dead); `X-User-Agent: AIBA-Connector` device naming dead because the connector header is commented out (`connector:services/api.ts:60`) | — |

---

## 11. Local features

Tray (Show/Hide/Quit, close hides), HKCU Run autostart (`--autostart` written, never read),
updater (30 min, forced modal for non-patch), debug-logs window (Alt+B global), dev settings
(Ctrl+Shift+B, `?dev=true`, `window.__openDevSettings`), prod/dev switch (Ctrl+Shift+D), devtools
(Ctrl+D then Ctrl+I), theme (Ctrl+Shift+X), sync all (Ctrl+Shift+S), start adapter (Alt+C on
Settings), bulk connect (Ctrl+Shift+T), export data to file, heal panel, pool settings,
FastSync flags, bank key manager switch, chip activation, cert pairing, NBU payments.

---

## 12. Active vs dead classification

| Item | Class | Evidence |
|---|---|---|
| Event-log change feed (~4,600 Rust + ~1,800 TS lines) | DEAD (hard-disabled) | `connector:utils/dev-settings.ts:80-86` |
| Adapter `/changes` | UNUSED (not a feed) | `main.os:19668`; `/changes` without base 501 |
| Legacy `/connections/ws` socket | DEAD | `app/layout.tsx:45-49` `autoConnect={false}` |
| `unauthorized` event listener (would `localStorage.clear()`) | DEAD | `providers/auth.tsx:212-222`, no Rust emitter |
| `signer-status`, `tunnel-activate-request` events | DEAD (emitted, no listener) | F §1.2 |
| 22 registered Tauri commands with no UI caller | UNUSED | V18/V19; list in feature map |
| Kapital webview login/sync | UNUSED (kept registered) | `tauri:lib.rs:67-480` |
| `install_adapter` | DEAD (`install.ps1` missing) | B §1a |
| `targeted-refresh.ts`, `handleEmployeeWriteCommand`, `buildEmployeeWriteDocument` | DEAD | A §4.3, A2 |
| Unisoft OData dashboard + `onec_*` commands | UNUSED | A §6 |
| Manifest / `extraTables` for the connector | DEAD (consumer removed `c3fc269`) | D §5.1 |
| `connector_status_request`, `read_table` | DEAD (no sender) | E §2 |
| `refdata_get`, `department_write_to_1c` | UNREACHABLE on the new hub | V3 action set |
| `employee_list_from_1c` | BROKEN | V26 |
| Legacy Node `onec-ws` relay | RETIRED but still built `|| true`; diverged copy runs in cloud-os | E §1.1, F §2.19 |
| `backend/1c/app/routes/**` | DEAD (not imported) | F §1.1 |
| `/api/v2/connector/ping` | DEAD temp diagnostic | `connector_ws.py:45-53` |
| `sql-dump`, `db-structure`, `hs` proxy, `openapi`, processes/tasks/exchange/characteristics routes | UNUSED but reachable | C §3 |
| GZIP upload flag | FLAGGED, would 400 on development | D §8.3 |
| Cold-read partition workers, XDTO bulk | FLAGGED | B §1a |
| Everything in sections 1-3 not listed above | ACTIVE | — |

---

## 13. Value classification

| Capability | Class | Why |
|---|---|---|
| Generic read API (catalogs, documents, registers, charts, enums) | PRESERVE+IMPROVE | backbone of sync and live reads; already re-done with parity in the rewrite |
| Document create/update/post/unpost/delete with `AIBA_` markers and idempotency | PRESERVE+IMPROVE | core product; remove Load-mode posting and fail-open lookup |
| Cloud command channel (presence + commands) | PRESERVE+IMPROVE | every cloud write path depends on it; needs auth and durable commands |
| Remote `sync_now` / `pull_now` / `read-table` | PRESERVE | used by aiba-next, provodka-ai, report refresh |
| `document_read` / `catalog_read` live reads | EXPAND | the only remote live-read surface; bounded and safe |
| Catalog update with CAS | PRESERVE | safe edit pattern |
| HR writes (hire, fire, departments, schedules, holidays) | PRESERVE+IMPROVE | real product, but exchange mode and success-on-failure must go |
| Sync-tables per connection | PRESERVE+IMPROVE | product-level table control; fix `[]`, `schedule`, revision push |
| Multi-org routing and org bindings | PRESERVE | needed for 19-org bases |
| Stock snapshot, data coverage, retail turnover | UNKNOWN | keep only with a proven consumer (rewrite D-7) |
| Presence fields | EXPAND | add adapter/platform version, last seen history, hostname, real IP |
| Heal «Внешнее соединение» | PRESERVE+IMPROVE | solves a real blocker; needs dry run, undo and consent |
| 1C users list without credentials | PRESERVE | good onboarding UX |
| Discovery from `ibases.v8i`, connection test with version detection | PRESERVE | already ported |
| Event-log feed | REPLACE | the rewrite's reader is the working version |
| Count gate, probe scan, hash buckets, two-strike delete | REPLACE | workarounds for no feed |
| Router + oscript workers + regsvr32 flipping | REPLACE | supervisor + per-version hosts, registry-free |
| Arbitrary `/query`, `hs` proxy, `sql-dump`, `_postCreateMethod`, `constants` | SECURITY RISK / REMOVE | open attack surface, no product caller |
| Relay tunnel to signer admin | SECURITY RISK | arbitrary path into a signing service |
| LAN-bound adapter with firewall rule | SECURITY RISK | full 1C read/write for any LAN host |
| Unauthenticated hub | SECURITY RISK | remote writes and route hijack |
| `exchange=true` HR writes, FORCE bank posting, catalog Load mode | SECURITY RISK (data integrity) | posted documents without movements, skipped config checks |
| Bank stack, NBU auto-sign | out of rewrite scope (D40); auto-sign is SECURITY RISK | — |
| Kapital webview scraper, `usb_test_disable_one`, `install_adapter`, `write_file` | REMOVE | dead or dangerous |
| Hidden hotkeys (dev mode, devtools in release) | REMOVE / gate | support convenience that any user can trigger |

---

## 14. Comparison with the new Connector

Status: DONE / PARTIAL / MISSING / SUPERSEDED / INTENTIONALLY DROPPED. Verified by reading the
rewrite code and docs, not assumed.

| Old capability | New status | Evidence in `D:\aiba\1c-arch` |
|---|---|---|
| Catalog / document / register / chart reads with old row shape | DONE (parity sweeps 682 catalogs, 525 doc types, 1,149 registers) | `MIGRATION_STATUS.md` 5.2-5.4; `src/OneC.Supervisor/EdgeServer.cs:155-209` |
| Local API exposure | SUPERSEDED (loopback only + `X-AIBA-Token`) | `EdgeServer.cs` `Listen(IPAddress.Loopback…)`, token check |
| Document create / update (PUT, PATCH) / post / unpost / mark-deleted / delete `?hard` | DONE | `EdgeServer.cs:220-250`; `src/OneC.Host/DocumentWriter.cs`; D38 |
| Ownership guard (only `AIBA_` documents are modified) | DONE (new rule, old had none) | D18; `WriteService.cs:30-33` |
| `exchange=true`, forced Load-mode posting | INTENTIONALLY DROPPED (400) | D38 table; `EdgeServer.cs:219-244` |
| `_monthlyUpsert`, subconto/VAT prediction, default warehouse/department, auto-created refs from name search | INTENTIONALLY DROPPED | D38 "not ported" row |
| Idempotency marker | DONE (active documents only, serialised) | D38 |
| Catalog create / update / delete (create_ref, catalog_update, employees, departments, schedules) | MISSING | no catalog write op in `src/OneC.Ipc/Envelope.cs:9-38`; `RefResolver.cs:216-217` creates only on explicit flags inside a document |
| Information-register write / delete (holidays, departments) | MISSING | no op |
| HR flows (hire, fire, departments, schedules, holidays, check_exists) | MISSING | no op, no channel |
| Arbitrary `/query` | INTENTIONALLY DROPPED in spirit (D15 identifier-whitelisted reads); no raw query op | D15; `Ops.Read` |
| `/counts` batch | SUPERSEDED by version walks and the event feed | S3 `versions` op, S11 |
| `/metadata` configuration version | PARTIAL; platform version now exposed (old never had it) | `Operations.cs:107-114` |
| Schema routes | PARTIAL (`tables` op with details) | `EdgeServer.cs:151` |
| `/stock`, data coverage | INTENTIONALLY DEFERRED (keep only with a consumer) | D42 D-7 |
| Retail turnover / reports lane | MISSING | — |
| `sotuv/istochnik-map`, `hs` proxy, `sql-dump`, `db-structure`, `constants`, processes/tasks | INTENTIONALLY DROPPED / not ported | — |
| XDTO bulk path | INTENTIONALLY DROPPED (≤7 % gain) | D35 |
| Parallel cold read | DONE (slices, K=4) | 5.5; S5 |
| Event-log change feed | DONE (old one was dead) | `src/OneC.EventLog`, D36, S6 |
| Sync engine | DONE locally against stub / isolated backend; **no production upload** | `MIGRATION_STATUS.md` S0-S15; D41, D42 D-2 |
| Prune / reconcile / multi-org routing | DONE in the Python target | S8, S10, S12 |
| Sync-tables from backend | PARTIAL (read; "Add table" UI writes local `sync.json`) | S12, S14 |
| Org bindings editing UI | MISSING (engine reads bindings) | `PythonMongoSyncTarget.cs` |
| Night-only accounting lane | INTENTIONALLY DROPPED | D42 D-8 |
| WebSocket presence and cloud commands (sync_now, write_to_1c, document_read, …) | MISSING (deliberately not built until approved) | D41 "Not built, on purpose"; no WebSocket client in `src/` (only a comment in `OneC.Cloud/StatusHeartbeat.cs:9`) |
| HTTP status heartbeat | DONE, only for records created by the new app | `src/OneC.Cloud/StatusHeartbeat.cs`, `CloudClient.cs:228-230` |
| Login, refresh, companies | DONE; tokens DPAPI-protected (old: plaintext localStorage) | D41 |
| Device id | DONE (MachineGuid, used only for `X-Device-Id`) | `src/OneC.Cloud/DeviceId.cs` |
| Add / delete cloud connection record | DONE (delete purges cloud data, confirmation) | D41 amended |
| Entity purge (`DELETE /entity/oneC/{id}`) | MISSING | — |
| Discovery from `ibases.v8i` | DONE | `src/OneC.Desktop.Core/LauncherBases.cs` |
| Connection test with version retry | DONE | MIGRATION_STATUS "1C connections = the 1C launcher's list" |
| 1C users list (V8USERS / cluster DB) | MISSING (explicitly not ported) | same row |
| Old config import | DONE | 5.1 |
| comcntr regsvr32 flipping, router, ownership pool | SUPERSEDED (registry-free, per-version hosts, lazy pools) | D02, D04, D29, D08 |
| Lazy connect, machine-wide file connect lock, circuit breaker | DONE | 5.1 |
| Heal «Внешнее соединение» | MISSING | — |
| Bank stack, signer, tunnel, NBU | INTENTIONALLY DROPPED (D40) | D40 |
| Updater, tray, autostart, Sentry | MISSING (no code found in `src/`) | grep of `src/` |
| Debug logs window / export | PARTIAL (Activity and Resources pages, `/v1/events`, `/v1/activity`) | 4.1 |
| Hidden dev/prod switch | DONE (Ctrl+Shift+D + badge off production) | MIGRATION_STATUS cloud account row |
| 1uz (.NET MSSQL) adapter | MISSING | only a mention in `InfobasesPage.xaml.cs` |
| Export data to file | MISSING | — |

---

## 15. Optimization opportunities

Format: OLD BEHAVIOR / WHY USEFUL / CURRENT COST / POSSIBLE BETTER DIRECTION. No
implementation.

1. **Remote sync trigger.** OLD: `sync_now` returns only at the end of a whole-base run. WHY:
   staff and aiba-next can force freshness. COST: HTTP caller times out at 120 s while the run
   continues; result orphaned. DIRECTION: always ack immediately with a job id and expose job
   status.
2. **Command transport.** OLD: Redis pub/sub, no persistence. WHY: simple fan-out. COST:
   commands during a reconnect are lost, late replies orphaned, no audit of what ran.
   DIRECTION: a durable per-connector command log with ids, status and expiry.
3. **Presence.** OLD: two sockets, one identity, record deleted on either close. WHY: presence
   decides write routing. COST: periodic write outages after hub reconnect; offline machines
   vanish. DIRECTION: one socket; presence history kept separately from the live route.
4. **Live reads.** OLD: `document_read`/`catalog_read` capped at 200 rows, documents/catalogs
   only. WHY: EDO draft import and support without the PC. COST: no registers, no counts, no
   schema, single-threaded queue behind sync. DIRECTION: a bounded, allow-listed read command
   family with its own lane.
5. **Sync-tables.** OLD: polling, `[]` impossible, `schedule` dropped. WHY: per-client table
   control without a release. COST: confusing state, silent loss of user choices.
   DIRECTION: store the full policy with a revision and push changes.
6. **Count drift.** OLD: `EntityCount` only `$inc`s uploads; prune/reconcile don't decrement
   until the 6 h retruth. COST: connector sees surplus and re-reads. DIRECTION: count from
   the operations that delete.
7. **Error replies.** OLD: `error`, `refdata_response`, `route_not_found` dropped by the hub.
   COST: callers wait for the full timeout. DIRECTION: one reply envelope for every outcome.
8. **Adapter restarts from config commands.** OLD: opening Settings may kill the live router.
   COST: cold COM reconnects (6-8 s per base). DIRECTION: never sweep a supervised child.
9. **Catalog full re-upload on any count change** (non-hash catalogs, e.g. 116k contracts).
   DIRECTION: feed-driven per-object updates (already in the rewrite).
10. **Diagnostics.** OLD: logs only on the PC, router hides per-base COM errors. DIRECTION:
    a remote diagnostics command returning per-base status, versions and recent errors.

---

## 16. Security findings (severity-ranked)

| # | Sev | Finding | Status |
|---|---|---|---|
| S1 | CRITICAL | Unauthenticated hub socket accepts cloud write/delete/HR commands and routes them to any online connector by base id; self-declared connector identity lets a fake client hijack a base's route (last writer wins, highest `appVersion` wins in P2) and receive write payloads or forge results | verified in code (V3, V4); public reachability needs prod check (cloud-os gives this URL to browsers, so likely public) |
| S2 | CRITICAL | `create_ref` extras → `_postCreateMethod` → any 0/1-arg common-module procedure in the customer's 1C, reachable through S1 | verified in code (V11) |
| S3 | HIGH | Adapter on `[::]:55899`, inbound firewall allow, CORS `*`, auth off: any LAN host or any web page in the user's browser can query, write, post, unpost, hard-delete | verified in code (V9, V10); client network ACLs need field check |
| S4 | HIGH | Relay tunnel forwards any method/path to the signer admin API and signing frames; token in URL query; auto-connects after one Settings visit | verified in code (V13); relay server behaviour unknown |
| S5 | HIGH | Fleet enumeration (`all_bases`) on the unauthenticated socket | verified in code (V4) |
| S6 | HIGH | Unauthenticated `rebuild` (fleet cache wipe) and `backfill-total-count` | verified (V20); already in `BACKEND_SECURITY_FINDINGS.md` |
| S7 | HIGH | Any JWT can call `clear-all` (cache, entities, connections) | route auth verified (V21); body behaviour reported by D |
| S8 | HIGH | Partner tokens (Odoo, MCP, BI) can unpost/hard-delete via admin routes; MCP exclusion is aiba-next-side only | verified (V25) |
| S9 | HIGH (integrity) | `exchange=true` HR writes; forced Load-mode posting of incoming bank docs; catalog creates in Load mode | verified (V1, V11; FORCE path read at `main.os:17194-17200`) |
| S10 | MEDIUM | Plaintext secrets: 1C passwords in `config.json` + up to 21 backups (Roaming), localStorage (`aiba-infobases`, tokens), `tunnel.json`, `aiba_token.json`, `sql-auth.json`, `%TEMP%` test input, Designer `/P` on command line | reported by A/B; partly re-read |
| S11 | MEDIUM | Release ships DevTools; Ctrl+Shift+D flips any user to dev backends; CSP null | verified (V27); `tauri.conf.json` CSP reported |
| S12 | MEDIUM | `write_file` writes anywhere except a small blocklist, often elevated | blocklist verified `tauri:handlers/filesystem.rs:605-614` |
| S13 | MEDIUM | NBU payments auto-signed on a timer without review | verified (V28) |
| S14 | MEDIUM | Heal writes the customer's 1C configuration (`/UpdateDBCfg`) with no undo command | verified (V29) |
| S15 | MEDIUM | `/query`, `hs` proxy, `sql-dump` (`WScript.Shell` + bcp) compiled in and reachable through S3 | verified routes; impact by inference |
| S16 | LOW | Hard-coded Fernet fallback key in backend/1c when `ONEC_CRED_FERNET_KEY` is unset; internal `/onecs` returns `passwordEncrypted` | reported by D (`app/utils/crypto.py:15`); prod env needs check |
| S17 | LOW | `usb_test_disable_one`, Kapital credential webview, `install_adapter` registered and callable from any webview context (no CSP) | reported by B |
| S18 | LOW | Unauthenticated manifest, `/connector/ping`, CORS `*` with credentials on backend/1c | reported by D |

---

## 17. Contradictions between investigators and how they were settled

| Topic | Conflict | Settled | How |
|---|---|---|---|
| Adapter auth | F: "no auth header check anywhere in main.os"; C: optional bearer, off by default | C correct | read `main.os:21360-21374, 22217` |
| Event-log gate line | A `dev-settings.ts:63-69`; B `80-86`; F `79-86` | `80-86` | read file |
| Tauri command count | B 82; F 77 | 77 | counted `lib.rs:806-882` |
| `get_signer_health`, `scan_base_heal_status` usage | B: ACTIVE / RARE; A, F: unused | unused | grep: only comments reference them |
| Router default mode | router header comment: shard; `get_adapter_pool_config` and B: ownership | ownership | `router:434-448` |
| Pool size | C "defaults to 2"; B "clamp(enabledBases,2,8)" | both true | `filesystem.rs:569-582` |
| backend/1c line numbers | E used local `8107bc0`; D and F used `origin/development` | `origin/development` authoritative | `git rev-list` shows local 58 commits behind, ancestor |
| Which backend is the control plane | `connectors_admin.py` docstring says backend/main calls it | backend/1c only; backend/main has no connector control code (only `AIBA Connector` device naming, dead) | git grep of backend/backend `origin/development` |
| `/ws/1c` on the 1c host | E: probably unused; F: depends on edge | likely not reachable at the edge for WS (hub docstring says only `/api/v1|v2` upgrades pass), unverified in prod | `connector_ws.py:13-16` |
| `main.os` size | brief ~21.5k; memory ~10.8k; C 23,902 | 23,902 | `wc -l` |
| Capabilities count | A "19" | 19 commands + 1 capability token | read `:1719-1745` |
| Unregister semantics | E: close wipes shared record | confirmed, plus the in-code comment claims a guard that does not exist | `connector_hub.py:418-422` |
| Which socket carries commands | all agree: `onec-sync-provider` (root layout); `onec-hub` presence only, ignores commands | consistent | — |

---

## 18. Open questions for a second, deeper investigation

1. Is `wss://1c.aiba.group/api/v2/connector/ws` publicly reachable without edge auth? (S1)
2. Does any production cloud sender ever put `_postCreateMethod` in `create_ref.extras` or
   `hard:true` in deletes? Server logs needed.
3. Are HR documents written with `exchange=true` in production, and do they have register
   movements? Same for `[CREATE][FORCE]` bank documents (`adapter.log`).
4. What does `relay.aiba.uz` forward, how is a token bound to a tenant, can it create payments
   without operator action?
5. Real client topology: does anything rely on LAN access to `:55899`? Do client firewalls
   block it?
6. Swarm command for `aiba_1c`: does `ip` hold the real client address?
7. Field frequency of the two-socket unregister outage (periodic `connector_offline`).
8. Field evidence of `ensure_adapter_dir` killing the live router ("Killing orphaned adapter
   process" right after Settings visits).
9. Which connector versions are still in the field (no inventory exists)?
10. `relay` split brain: prod values of `ONEC_WS_URL` for cloud-os HR/payroll, AI chat, soliq
    Mehnat, provodka-ai — are those write paths dead?
11. Who consumes stock snapshot, data coverage and retail rollup today (rewrite D-7)?
12. Old connector reconcile/prune use `scope=connection` (`connector:utils/backend-refids-fetcher.ts:97,235`).
    The rewrite found that this scope wiped multi-org movements for its own calls
    (MIGRATION_STATUS "Bugs found 2026-10-01"). Does the old connector's usage cause the same
    loss on multi-org bases?
13. Is `28e98b1` (main.os change without `adapter.version` bump) shipped to installs?
14. Updater signing key recovery history and the unmerged `origin/security/fixes-1.3.51`.
15. The aiba-next per-tenant hub and tunnel (E §5b) were not re-verified line by line.
