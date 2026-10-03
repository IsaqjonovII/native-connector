# F — Cross-cutting discovery audit (OLD AIBA Connector ecosystem)

Read-only. Code over docs. Secrets redacted. Line numbers are from the working trees on 2026-10-03.

## Repos audited

| Repo | Branch | HEAD | Dirty |
|---|---|---|---|
| `D:\aiba\connector` | `release` | `cfab3a1` 2026-09-30 "ci: let the release guard pass for a version that is not published yet" | `M 1uz-adapter/adapter.version`, `?? claudetest/` |
| `D:\aiba\backend\1c` | `development` | `8107bc0` 2026-10-02 "internal: serve bank accounts, operation kinds and document metadata over HTTPS" | `M reconciliation.py`, `M probe_hash.py`, vectors, 2 untracked docs |
| `D:\aiba\backend\backend` | `feat/purchase` (not `development`) | `53b24b35` 2026-05-12 | several admin files modified |

Connector version in `tauri.conf.json:5` = **1.3.109**. Remotes: `origin` = gitlab `aiba/tools/connector-app`, plus a **`github` remote = `github.com/IsaqjonovII/aiba-connector`** (personal-account mirror). Updater endpoint = `github.com/AIBAUZ/aiba-connector/releases/latest/download/latest.json` (`tauri.conf.json:42-44`).

---

## 1. Raw inventories

### 1.1 Backend routes mentioning onec/connector/device/sync/socket/ws/command/adapter/presence/agent

**backend/1c** (327 decorators total; filtered list). Mounts in `main.py:70-117`.

WebSockets / connector surface:
- `app/api/connector_ws.py:99` `WS /api/v2/connector/ws` — connector hub (presence + command relay) **and** cloud-client commands on the same socket. **No auth** (docstring `:9-11`, `main.py:93-97`).
- `app/api/connector_ws.py:45` `GET /api/v2/connector/ping` — "TEMP diagnostic", still live.
- `app/api/cloud_router_ws.py:918` `WS /ws/1c` — legacy cloud-router endpoint (mounted with no prefix, `main.py:107`). No auth.
- `app/api/internal/routes/connector.py:51` `GET /api/internal/connector/manifest?onec_id=` — **no auth by design** (`:18-22`, `main.py:86-89`).

Service-secret admin (`/api/internal/admin/connectors`, `connectors_admin.py:25-29`):
- `:42 GET ""` online connectors; `:62 GET /infobase/{id}`; `:69 POST /infobase/{id}/sync` → `sync_now`; `:85 POST /read-table` → `pull_now`; `:168 POST /document/post`; `:227 /document/unpost`; `:239 /document/update`; `:257 /catalog/update`; `:358 /documents/read`; `:375 /catalog/read`; `:384 /document/delete` (`hard` flag).

v2 user routes (`app/api/v2/routes/onec.py`): `:77 POST ""`, `:97 GET ""`, `:106 /by-company/{id}`, `:127 /{id}`, `:138 PATCH /connection/{id}`, `:171 DELETE /connection/{id}` (202 async purge), `:204 /delete-status`, `:234 POST /connection/backfill-total-count`, `:255 POST /connection/rebuild`, `:288 /counts`, `:515/606/735 org-bindings` (multi-org).
Cloud OneC (`cloud_onec.py`): `:37 POST /connections`, `:93 POST /clobus/discover`, `:113 POST /clobus/connect`, `:208 GET /connections`, `:228 DELETE`, `:265 POST /{id}/sync`, `:294 GET /{id}/sync/stream` (SSE).
v1 (`app/api/v1/routes/onec.py`): `:288 /refids`, `:357 /refhashes`, `:497 POST /reconcile-recorder` (deletes register rows), `:638 POST /prune-missing` (deletes rows; "most destructive endpoint"), `:804/829 sync-tables`.
Internal indexes: `match_index.py:43,86`, `nomenclature_index.py:47,80`, `write_metadata.py:432,490`, `internal/routes/onec.py:26..227` (admin CRUD incl. `PUT /{id}/extra-tables`).
Other internal: `odoo_inbox.py:206,269`, `reports_webhook.py:50`, `audit.py:25`, `ai/tool.py:68 GET /api/ai/tool` (LangGraph agent tool, service key).

Dead route tree: **`app/routes/**`** (`onec.py`, `entity.py`, `statistics/*`, `venkon/*` — ~80 decorators) is **not imported anywhere** (`rg "from app.routes"` = 0). Legacy duplicate of `app/api/*`.

**backend/backend** (control plane):
- `app/api/v1/connection.py:72` `WS /connections/ws` — generic event socket (`auth.*`, `bank.*`, `company.*` plugins).
- `connection.py:17` `PATCH /connections/{token}` — pairs a socket to a user and pushes a fresh **token pair** down the socket (`auth.connection_verified`).
- `connection.py:~100` `GET /connections/status`.
- `app/api/v1/auth.py:920` `WS /qr/ws/{session_token}` (QR login).
- `notification.py:97,135` `device-token`.
- No backend/backend code calls backend/1c `/connectors*` admin routes (only onec CRUD/entity/metrics via `app/admin/services/onec_client.py:104-360`).

### 1.2 WebSocket message types

Connector → backend/1c hub (`onec-sync-provider.tsx`, `onec-hub.tsx`): `hello`, `state_update`, `heartbeat`, `server_pong`, `write_progress`, `*_result` (e.g. `write_to_1c_result`, `check_exists_result`, `get_bases_result`, `list_resource_result`), `error`.
Hub → connector: `need_hello` (`onec-hub.tsx:156`), `connector-presence:update` relay, and commands.

**Commands the connector executes** (`onec-sync-provider.tsx:14905-15138` `dispatchControlCommand`):
`ping`/`server_ping`, `get_bases`, `sync_now`/`pull_now`, `check_exists`, `delete_from_1c`/`delete_doc_from_1c`, `employee_write_to_1c`, `write_to_1c`/`write_doc_to_1c`, `post_1c_document`/`post_doc_in_1c`, `unpost_1c_document`/`unpost_doc_in_1c`, `update_1c_document`/`edit_1c_document`, `delete_1c_document`, `push_to_1c`/`create_in_1c` (departments, schedules), `department_write_to_1c`, `list_resource` (schedules/departments/employees/bases/refdata), `holiday_push_to_1c`, `holiday_delete_in_1c`, `holiday_check_1c`, `department_delete_in_1c`/`delete_in_1c`, `refdata_get`/`get_refdata`/`refdata/get`, `get_list`, `create_ref`, `catalog_update`, `document_read`, `catalog_read`, `employee_fire_from_1c`, `connector_status_request`/`status_request`.
Write-gated set: `onec-sync-provider.tsx:137-169`.

backend/1c cloud-client actions (`cloud_router_ws.py:50-68`): `connect, get_bases, list_resource, write_to_1c, employee_write_to_1c, employee_list_from_1c, employee_fire_from_1c, check_exists, delete_from_1c, push_to_1c, delete_in_1c, holiday_push_to_1c, holiday_delete_in_1c, holiday_check_1c, server_ping, get_list, create_ref` + `get_connector_bases` (`:701`).

Diff:
- `employee_list_from_1c` is advertised/dispatched by backend but **has no handler in the connector** (rg = 0) → always `unsupported`.
- `post/unpost/update/delete_1c_document`, `catalog_update`, `document_read`, `catalog_read`, `sync_now`, `pull_now` are reachable only through service-secret `connectors_admin` (+ `onec_post.py:229`, `odoo_write.py:726`, `celery/reports.py:134`), not through the open cloud WS action set.

backend/backend `/connections/ws` events: `auth.new_connection`, `auth.check_connection`, `auth.connect`, `auth.logout` (`plugins/auth.py:33-84`), `bank.credentials`, `bank.healthStatus`, `bank.signed_certificate` (`plugins/bank.py:12,42,73`), `company.get_statistics` (`plugins/company.py:7`); server-push `auth.connection_verified` (`connection.py:56`), `auth.logout` (`auth.py:605`).
Connector side listens for `bank.credentials` (no-op) and `auth.logout` and sends `auth.connect` (`providers/seed.tsx:220-236`).

Tunnel (relay.aiba.uz, `tunnel_agent.rs`): inbound `{command: refresh_inventory|activate_chip}` (`:591-622`), `{request_id, http:{method,path,query,body,content_type,base}}` (`:406-439`), `{request_id, frame}` Styx/iABS7 RPC (`:336-398`); outbound `{event: cert_list}` / `{event: keys}` (`:502,529`).

Tauri events (Rust→UI): `adapter:test-progress`, `app-quit-requested`, `bank-key-manager-status`, `eventlog:changed`, `log-entry`, `signer-status`, `sqb-status`, `tunnel-status`, `chip-activation-progress`, `tunnel-activate-request`, `com-admin-required`.
- Emitted, **nobody listens**: `signer-status` (4 emits, 0 TS refs), `tunnel-activate-request` (`tunnel_agent.rs:608`).
- Listened, **nobody emits**: `unauthorized` (`providers/auth.tsx:212`).

### 1.3 Tauri commands: defined vs invoked

77 `#[tauri::command]` fns, 54 distinct invoked names (multiline-aware scan, plus constant `EVENTLOG_START_COMMAND` in `utils/eventlog-arm.ts:24`). Invoked-but-undefined: **none**.

Defined + registered (`lib.rs:806-882`) but **never invoked from the UI**:
`check_network_status`, `ensure_adapter_firewall_rule` (but called internally from `start_adapter`, see 3.3), `get_log_file_path`, `get_runtime_environment_status`, `get_signer_health`, `get_sqb_health`, `restart_sqb_sidecar`, `install_adapter` (runs `install.ps1`), `list_com_connector_versions`, `set_com_connector_version`, `list_readers`, `usb_test_disable_one`, **`open_kapital_auth_window`**, **`sync_kapital_via_webview`**, `parse_valuetable_xml`, **`scan_base_heal_status`** (heal-panel docstring claims it, code uses the adapter's `/healScan` route instead), `set_adapter_kind`, `set_adapter_port`, `set_backend_locale`, `stop_reports_worker`, `tunnel_get_config`, `tunnel_set_config` (replaced by `tunnel_ensure_config`).

### 1.4 main.os routes vs callers

Router: `ОбработатьЗапрос` `main.os:22204`. All under `/api` and `/api/db/{base}/…` (`:22228-22258`). No auth. Resources:
`worker` 22358 · `db-structure` 22382 · `sql-dump` 22404 · `databases|bases` 22472 · `status` 22499 · `counts` 22578 · `changes` 22587 · `metadata` 22637 · `catalogs` 22641 (GET/POST/PUT/DELETE `?hard`) · `documents` 22698 (+ `/batch` 22837, `/{id}/post` 22900, `/{id}/unpost` 22908, DELETE `?hard`, PUT `?autoUnpost`, `?exchange`) · `enums` 22914 · `charts` 22922 · `calcTypes|chartsOfCalculationTypes` 22937 · `registers` 22949 (info POST/PUT/DELETE writes; `/turnover` 23023) · `stock` 23061 · `sotuv/istochnik-map` 23087 · `constants` 23101 · **`query` POST (arbitrary 1C query)** 23109 · **`hs|http` (proxy to 1C HTTP services)** 23138 · `schema` 23180 · `healScan` 23195 · `openapi|swagger` 23199 · `processes|businessProcesses` 23203 · `tasks` 23211 · `exchangePlans|exchange` 23219 · `characteristics` 23227.
CLI: `--discover`, `--status`, `--non-interactive`, `--help`, `--configure`, `--port`, `--shard-of`, `--worker-id`, `--own-bases-enc`, `--own-bases`, `--add-db`, `--test-connections` (`main.os:23460-23711`). Listener `Новый TCPСервер(port)` `:23272`.

Callers (TS/Rust) found for: databases/status, api/status, counts, catalogs, documents, batch, unpost, enums, charts, calcTypes, registers, turnover, stock, query (only `onec-sync-provider.tsx:4791` bank suggestion, `:12159` доверенность heuristics), schema, healScan (`heal-panel.tsx:188`), worker (adapter-router `main.rs:1531`), metadata.
**Adapter routes with no caller**: `db-structure`, `sql-dump` (explicitly abandoned, `sync-orchestrator.ts:1695-1699`), `changes` (event-log feed, disabled — see 2.6), `constants`, `hs|http`, `openapi|swagger`, `processes`, `tasks`, `exchangePlans`, `characteristics`, `sotuv/istochnik-map` (only a comment in `config.ts:295`). These are still live attack surface (see 3.3).

1uz-adapter (C#, `resources/1uz-adapter/src/Program.cs`): binds `localhost` only (`:44`). Routes: `/`, `/api/status`, `/api/databases[/status]`, `/api/db/{db}/stock`, `/api/db/{db}/reconciliation/{kind}`, `/api/db/{db}/{**path}`, **`POST /api/db/{db}/documents/{**path}`** (`:546`) → direct SQL inserts into BePro (ServiceForThirdOrganization, EsfAuto, InvoiceGoods, BankIssue, manual Операция).

### 1.5 Feature flags / env / config keys

Connector build env (`process.env`): `NEXT_PUBLIC_{API_URL, API_DEV_URL, API_SOCKET, API_DEV_SOCKET, 1C_API, 1C_API_DEV, 1UZ_API, 1UZ_API_DEV, ADAPTER_API_URL, ADAPTER_API_DEV_URL, 1UZ_ADAPTER_API_URL, REPORTS_ADAPTER_API_URL, ONEC_SYNC_SOCKET_URL, ONEC_SYNC_HEARTBEAT_SECONDS, ONEC_SYNC_ADAPTER_REFRESH_SECONDS, LOCAL_ONEC_REGEX_URL, EPASS_API_URL, BANK_API_URL, BANK_API_DEV_URL, DIDOX_API_URL, DIDOX_API_DEV_URL, TUNNEL_BACKEND, TUNNEL_BACKEND_DEV, SIGNER_API_URL, SQB_API_URL, SENTRY_DSN, SENTRY_ENV, APP_VERSION, BULK_REGISTERS, GZIP_UPLOAD, RETAIL_TURNOVER_WINDOW_DAYS, RETAIL_TURNOVER_BACKFILL_DAYS}`, `NODE_ENV`. `.env*` also carry `NEXT_PUBLIC_MINIO_*` (incl. a 32-char secret) — **not referenced in src and not present in `out/`**, so not shipped today.
Rust compile/runtime env: `SENTRY_DSN`, `SENTRY_ENV` (`lib.rs:713,722`), `AIBA_SKIP_ELEVATION` (`main.rs:37`), `AIBA_ADAPTER_PORT`, `AIBA_UZ_ADAPTER_PORT` (`filesystem.rs:63,69`), `AIBA_KEEP_PIDS` (`filesystem.rs:4303`), `SIGNER_CONFIG` (`sidecar.rs:520,712`), `SQB_ADMIN_PORT`, `SQB_BROWSER`, `SQB_PYTHON_EXE` (`sqb_sidecar.rs:198,224,390`).
Runtime overrides (localStorage `dev_settings`, `utils/dev-settings.ts:5-68`): all URLs above + `SYNC_INTERVAL_MIN`, `BULK_REGISTERS`, `GZIP_UPLOAD`, `COLD_READ_WORKERS` (2..8), `EVENTLOG_SYNC`, `RETAIL_TURNOVER_*`, `TUNNEL_BACKEND(_DEV)_URL` (auto-stripped on load, `dev-settings-provider.tsx:21-25`). The Dev Settings modal only exposes URLs + sync interval; `BULK_REGISTERS/GZIP_UPLOAD/COLD_READ_WORKERS/RETAIL_*` are reachable only by hand-editing localStorage.
Hard-coded off: `isEventLogSyncEnabled()` returns `false` unconditionally (`dev-settings.ts:79-86`).
Adapter `config.json` keys: `server, databases, lazyConnectThreshold, api` (+ `httpRead`, `connectLock`, `comVersion`, `defaultComVersion`). File is gitignored (`.gitignore:6`).
backend/1c env: `SERVICE_SECRET_KEY, ODOO_API_TOKEN, MCP_API_TOKEN, AIBA_BI_API_TOKEN, JWT_SECRET_KEY, ONEC_CRED_FERNET_KEY, MINIO_*, REDIS/RABBITMQ/CELERY_*, CONNECTOR_COMMAND_TIMEOUT, CONNECTOR_WRITE_COMMAND_TIMEOUT, ODOO_INSTANT_{ENABLED,MAX_CONCURRENCY,SILENCE_SECONDS,DRAIN_SECONDS,BUDGET_POST_SECONDS,BUDGET_BUILD_SECONDS}, RETAIL_ROLLUP_READ, REGISTER_LINES_WRITE, DOC_ROWS_WRITE, RECONCILIATION_SOURCE, METRICS_SOURCE, CACHE_PASS_MODE, CACHE_DISPATCH_SPREAD_SECONDS, CELERY_QUEUE_MODE, CELERY_BUILDERS_QUEUE, BUILDERS_SLOT_TTL, BUILDERS_QUEUED_GUARD, RETRUTH_SPREAD_SECONDS`.
backend/backend: `AIBA_ONEC_API` (`core/config.py:153`), `core/feature_flags.py` (AI_MEMORY, TOOL_CACHE, ASYNC_REDIS, PARALLEL_TOOLS, MULTI_AGENT, RAG — not connector related).

### 1.6 localStorage / sessionStorage / store keys (connector)

`dev_settings`; zustand persist: `aiba-connector-store` (`stores/app.ts:84`), `aiba-companies`, `aiba-infobases`, `aiba-auth` (tokens); `aiba.connector.fallback-id` (retired, delete-only, `onec-sync-provider.tsx:183`); `adapter_page_limits` (`incremental-fetcher.ts:178`); `adapter_poison_rows` (`:275`); `sync-interval-v1:{bank}:{company}` (`lib/sync-interval.ts:28`); `turon-filters-v1:{scope}`; `sync_source_capability:{key}`; table metadata / infobase sync keys (`table-metadata-storage.ts`, `infobase-sync-storage.ts`, `metadata-preload.ts`); **`sync.debug.fullPayload`** = "1" dumps full chunk payloads (`chunk-uploader.ts:147`).
On disk: `tunnel.json` (relay URL + connector token), `aiba_token.json` (user AIBA bearer for the signer sidecar, `lib/aiba-token-keepalive.ts`), `%APPDATA%\aiba.uz\chip_cert_map.json` (`pairing.rs`), HKCU Run key autostart (`utils/startup.rs:2-40`).

### 1.7 Timers / schedulers

Connector TS: adapter health check (`providers/adapter.tsx:335,467`), connection-status heartbeat (`connection-status.tsx:345`), hub heartbeat 30s (`onec-hub.tsx:127`), sync-socket heartbeat (`onec-sync-provider.tsx:15202`), adapter DB refresh (`:15372`), write-progress heartbeat 10s capped 20 min (`:1633`, `:130`), adapter-active wait (`:1180`), sync-orchestrator watchdog (`sync-orchestrator.ts:289`), update check (`update.tsx:269`), AIBA token re-push + freshness ping (`aiba-token-keepalive.ts:86,112`), Kapital auto-sync (`banks/kapital/page.tsx:132`), JWT countdown (`:810`), bank pages polling (ubank/turon/ipoteka/payments), settings tick 4s (`settings/page.tsx:244`), DB modal refresh 8s (`database-config-modal.tsx:399`).
Connector Rust: tunnel keepalive 15s / liveness 45s / cert push 8s / inventory poll 2s (`tunnel_agent.rs:29-44`), adapter watchdog (`filesystem.rs:1305+`), signer health probe + restart (`sidecar.rs`), SQB sidecar, chip activation polling (16 sleeps), eventlog poll thread, bank-key-manager.
backend/1c celery beat (`core/celery/app.py:275-366`): `check_onec_heartbeat` 60s, `auto_update_onec` 60s, `sync_all_cloud_onec_connections` 30 min, `process_odoo_inbox_backstop` 60s (no expiry), `odoo_inbox_health` 60s, `refresh_all_reports` 03:00 Tashkent, `build_retail_rollups` 30 min, `purge_stuck_onec` 120s, `drain_builders` 300s, `retruth_entity_counts` 6h.
backend/backend beat: itpark checks + calendar reminders only (`core/celery.py:477-487`).

### 1.8 Command enums / action unions

`ONEC_WRITE_ACTIONS` (`onec-sync-provider.tsx:137`), `SyncPolicyLane = interactive|active|reports|night|manual` (`sync-policy.ts:18`), `SyncLane` (`abort-registry.ts:43`), `AppModeType = prod|dev` (`stores/app.ts:15`), `AdapterRuntimeStatus` (`adapter-runtime.ts:3`), `UbankSyncMode`, `TuronSyncMode`; backend `CLOUD_COMMAND_ACTIONS`, `ROUTE_DISPATCH_ACTIONS`, `REQUIRES_BASE_SELECTION_ACTIONS` (`cloud_router_ws.py:50-112`); `CATALOG_UPDATE_CAS_CAPABILITY` (`connectors_admin.py:33`).

### 1.9 Redis keys / channels (backend/1c)

`connector:{id}` hash, `connectors:online` zset, `conn-of-infobase:{onec_id}`, `connector-cmd:{id}` (pub/sub), `connector-resp:{request_id}`, `connector-presence:update`, `sync-of-infobase:{onec_id}`, `onec-sync:{conn}`, `onec-sync-lock:{conn}`, `lock:{name}`, `running:{onec_id}`, `builders:slot`, `builders:dirty`, `matchindex:build:{id}`, `bankindex:build:{id}`, `nomindex:build:{id}`, `cache-refresh-armed:{id}`, `onec_write_metadata:v1:*`, `reconciliation:v5:counterparties`, `accessible_companies:*` (`core/connector_hub.py:54-560` + grep).
backend/backend: `connection:{token}`, `user:{id}:connection`, `bank_health:{user}`, `{conn}:transactions:last_sync_date`, `{conn}:accounts:last_sync_date`, `otp_plain:{phone}`.

### 1.10 Shortcuts, deep links, hidden pages, debug menus

| Trigger | Effect | Where |
|---|---|---|
| **Alt+B (OS-global)** | toggle Debug Logs window (`/debug-logs`) — registered with `tauri_plugin_global_shortcut`, fires even when app unfocused and steals Alt+B system-wide | `lib.rs:926-958` |
| Ctrl+Shift+B | Dev Settings modal (comment says Ctrl+Shift+D — stale) | `dev-settings-provider.tsx:28-36` |
| `?dev=true` URL param / `window.__openDevSettings()` | Dev Settings modal | `dev-settings-provider.tsx:44-58` |
| Mod+Shift+D | toggle prod/dev backend mode (logs out, reloads to /login) | `mode-switcher.tsx:63` |
| Mod+D then Mod+I within 2s | open WebView devtools in prod | `mode-switcher.tsx:75-110` |
| Mod+Shift+X | theme toggle | `theme.tsx:8` |
| Ctrl+Shift+S | sync every credentialed infobase across all companies | `info-base-sync-provider.tsx:845` |
| Alt+C (on /settings) | manual `start_adapter` | `settings/page.tsx:394` |
| Ctrl+Shift+T (DB config modal) | hidden "Bulk ulash" — connect many bases with one 1C login | `database-config-modal.tsx:408, 1203` |
| Mod+B | sidebar | `ui/sidebar.tsx:33` |

No deep-link scheme (no `deep-link` plugin in `tauri.conf.json`). Routes: `/debug-logs` (separate window), `/instructions/*`, 11 bank pages (`aab, hamkor, ipoteka, kapital, mikrokreditbank, ofb, sqb, trust, turon, ubank, xalq`), `/banks/keys`, `/banks/payments`. CSP is `null` (`tauri.conf.json:36`). Tray menu: show/hide/quit only.

---

## 2. Investigations (non-mainstream items)

### 2.1 Bank-signing tunnel to `relay.aiba.uz` — remote HTTP into the local signer admin API (ACTIVE)
`handlers/tunnel_agent.rs`. Outbound WS to `<relay>/ws/connector/?token=` (`:168-181`), default relay `https://relay.aiba.uz` for both prod and dev (`core/config.ts:223-234`). Triggered by visiting **/settings**, which calls `tunnel_ensure_config` and generates a 40-hex token (`settings/page.tsx:50`, `tunnel_agent.rs:836-852`); after that it auto-connects on every app start while the Bank-key-manager switch is on (`:776-817`).
What the relay can do:
- send arbitrary Styx/iABS7 signing frames to local `:13589` / `:8088` (`:336-398`), routed to a specific chip by `cert_thumb`/`cert_sn` (`:297`);
- send `{http:{method,path,query,body,base}}` — forwarded **with any method and any path** to `http://127.0.0.1:6210` or, with `base=signer|admin`, to the **signer admin API `:7777`** ("bank actions: kapital create-payment, sync", `:50-52, 406-439`). No path allow-list;
- `refresh_inventory`; `activate_chip` (emits `tunnel-activate-request` — **no UI listener**, so dead).
It pushes the cert/key inventory every 8s (`:40`). Relay server is not in these three repos ("Qo'lda backend", `:3-4`). This is a full remote-control channel over the operator's bank keys and payment actions, protected only by the connector-generated token.

### 2.2 Unauthenticated connector hub that also accepts cloud write commands (ACTIVE)
`backend/1c/app/api/connector_ws.py:99-140`. Same socket serves connectors (`hello`, self-declared identity, `_derive_connector_id` `:84-95`) **and** cloud clients: any socket that sends a `CLOUD_COMMAND_ACTIONS` action (`write_to_1c`, `delete_from_1c`, `employee_fire_from_1c`, `push_to_1c`, `holiday_*`, `create_ref`…) is treated as a cloud client and routed to connectors (`:117-125`, `cloud_router_ws.py:704-860`). No token, no tenant check in the router; target picked from payload `base_key/infobase_id/base_name`. The cloud client also gets a live `presence_update_relay` of all connectors (`:121`). Connector side confirms "No auth on this socket by design" (`onec-hub.tsx:24`). Also `/ws/1c` (`cloud_router_ws.py:918`) exposes the same handler at root (reachability depends on the edge, which per `main.py:91-96` forwards upgrades only under `/api/v1|v2`).
Impact if reachable: impersonate a connector (`hello` with someone's `user_id:device_id` → receive their command payloads), or issue writes/deletes/HR firing into any online base. Most surprising item in this audit.

### 2.3 Adapter exposed on the LAN, no auth, inbound firewall allow (ACTIVE)
`start_adapter` calls `ensure_adapter_firewall_rule_internal` (`filesystem.rs:1595, 1783`) which runs `netsh advfirewall firewall add rule … dir=in action=allow protocol=TCP localport=<port>` with no `remoteip` restriction (`:1227-1260`). The adapter-router listens dual-stack on **`[::]:55899`** (`adapter-router/src/main.rs:2717-2725`); oscript workers use `Новый TCPСервер(port)` (`main.os:23272`). No auth header check anywhere in `main.os` (only CORS allows `Authorization`, `:21400`). So any host on the client LAN can `POST /api/db/{base}/query`, write/post/unpost/hard-delete documents, write info registers, and proxy to 1C HTTP services. Elevation docs call the firewall rule "localhost-irrelevant" (`utils/privileges.rs:56-71`) — the rule is not needed for localhost and only widens exposure. (Bind behaviour of oscript `TCPСервер` = all interfaces is my reading; router `[::]` is certain.)

### 2.4 Service-secret peers include partner tokens → can unpost/delete 1C documents (ACTIVE)
`app/api/internal/dependencies.py:10-57`: `ODOO_API_TOKEN` (external partner), `MCP_API_TOKEN`, `AIBA_BI_API_TOKEN` are "full peers" of the master `SERVICE_SECRET_KEY`. `connectors_admin.py` uses that same gate (`:28`), so the Odoo or BI token can call `/document/unpost`, `/document/update`, `/catalog/update`, `/document/delete` (`hard`) on any `onec_id` (`:227-395`). Audited by `middleware/odoo_audit.py` but not restricted. Note: the memory says MCP grants exclude `onec_unpost/delete_document`; that exclusion is on the aiba-next side only — backend/1c itself does not enforce it.

### 2.5 Remote 1C mutation commands beyond sync (ACTIVE)
Connector executes: post/unpost/update/delete existing documents by ref (`onec-sync-provider.tsx:4ef5461`-era handlers, dispatch `:14965-14988`), `catalog_update` with CAS (`catalog-update-cas.ts`), HR: hire (`employee_write_to_1c` creates ФизЛицо+Сотрудник+hire doc), `employee_fire_from_1c`, departments, work schedules, holidays (production calendar), `create_ref`. HR docs are written with **`exchange=true` forced** for payroll/vacation/sick/firing types (`DOCS_REQUIRING_EXCHANGE_MODE`, `onec-sync-provider.tsx:4688-4714`) — this contradicts the workspace hard rule "never use exchange=true". Callers: backend `connectors_admin`, `onec_post.py`, `odoo_write.py`, cloud-os over the WS.

### 2.6 Event-log change feed (DEAD, but large code remains)
Rust `handlers/eventlog.rs` (4588 lines) + `eventlog_discovery.rs` (1191) tail `1Cv8Log`, registered commands `eventlog_start/ack/status` (`lib.rs:880-882`); TS `eventlog-provider.tsx`, `eventlog-driver.ts`, `eventlog-reduce.ts`, `eventlog-arm.ts` mounted in root layout (`app/layout.tsx:43`). Gated by `isEventLogSyncEnabled()` which is **hard-coded `false`** because adapter `/changes` is "a recent-document-date query, not a modification feed" (`dev-settings.ts:79-86`). Turning it off needs app restart (no `eventlog_stop`). Field-validation branch `lab/eventlog-e2e` / `connector-e2e/` clone.

### 2.7 Heal «Внешнее соединение» — self-repair that edits the customer's 1C configuration (ACTIVE, manual)
`handlers/heal_extconn.rs`: runs 1C DESIGNER `/DumpConfigToFiles`, ticks ExternalConnection on common modules hosting event-subscription handlers, `/LoadConfigFromFiles` + **`/UpdateDBCfg`**, keeps a `_backup` dir for revert (`:217, 409-559`). Triggered from Settings → HealPanel (`heal-panel.tsx:228` `heal_base`, then `restart_adapter`). Scan uses adapter `/healScan` (`heal-panel.tsx:188`); the Rust `scan_base_heal_status` is dead. Standalone twin: `scripts/heal-external-connection.ps1`.

### 2.8 Reading 1C logins without credentials (ACTIVE)
`list_infobase_users` (used by `database-config-modal.tsx:905`): file bases parsed straight from `1Cv8.1CD` (`onec_1cd.rs`), server bases read `1CV8Clst.lst` then run `psql -w` / `sqlcmd` against the DBMS using **trust/Windows auth** to `select name, descr, show, admrole from v8users` (`onec_cluster.rs:1-20, 280-298`). Names only, no hashes; refuses non-colocated DBMS (`:289`).

### 2.9 Bank stack inside the connector (ACTIVE, large)
11 bank pages + keys + payments; bundled `bank-connector` signer sidecar (`binaries/bank-connector-x86_64-pc-windows-msvc.exe`, committed binary) owning ports 6210/13589/8181/7777; **kills vendor `StyxTokenManager.exe` and clears kernel port exclusions on every launch** (`utils/port_clearance.rs`, `lib.rs:960-975`) unless the "Bank key manager" switch is off (`bank_key_manager.rs`); chip activation via vendor handover (`chip_activation.rs`), USB reader isolation with `CM_Disable_DevNode` / `Disable-PnpDevice` (`usb_iso.rs`, `usb_control.rs`); SQB Python browser-automation sidecar (`sqb_sidecar.rs`). The user AIBA bearer is written to disk for the sidecar and re-pushed on a timer (`aiba-token-keepalive.ts`). Unmerged bank branches: `feat/asaka`, `feat/cib-payroll-sidecar` (AAB + Hamkorbank **payroll readers**), `fix/dbo-dead-session-all-banks`, `fix/remote-only-blocks-local-connect`.
Dead-but-present: `open_kapital_auth_window` / `sync_kapital_via_webview` (`lib.rs:67-480`) — hidden WebView2 to `b2b.kapitalbank.uz` to pass Cloudflare, injects login+password into an init script, scrapes JWT from page storage. Not invoked.

### 2.10 Legacy backend/backend socket (DEAD in connector)
`WebSocketWrapper path="/connections/ws" autoConnect={false}` (`app/layout.tsx:45-49`); only `seed.tsx` uses `useWS("ws")` and nothing calls `reconnect()`, so it never connects. Backend still asks connectors for **bank username/password over this socket** (`bank.credentials`, `plugins/bank.py:12-39`) and for signed certs (`bank.signed_certificate`). The connector's `X-User-Agent: AIBA-Connector` header is commented out (`services/api.ts:60`), so backend's "AIBA Connector" device naming (`utils/auth.py:159, 260-301`) is dead too.

### 2.11 Multi-org (ACTIVE)
One 1C base → many companies by Организация: `org-bindings` routes (`v2/routes/onec.py:515-735`), connector `org-router.ts`, `org-binding.service.ts`, `connector-advertise.ts`, pinned-GUID Организация resolve (commit `4805f96`). `scripts`-level helper `tools/pull-table-direct.ps1` has an MIRL org map "baked in".

### 2.12 Stock / warehouse, reports, sverka, 1UZ ledger (ACTIVE, best-effort side lanes)
- Stock snapshot: `GET /api/db/{db}/stock` → `POST /api/v2/entity/stock-snapshot` replace-whole (`stock-sync.ts`).
- Reports lane: own oscript worker on `adapter_port+50` (:55949), retail turnover push (`turnover-push.ts`, `reports-lane-driver.ts`), window 14d / backfill 400d.
- Coverage push (`coverage-sync.ts`): tells cloud how far history backfill reached so ОСВ can warn.
- 1UZ Акт сверки ledger: `reconciliation-sync.ts` → `{1uz api}/reconciliation/ledger/upload`.
- Register reconcile / prune (deletes cloud rows): `register-reconcile.ts` → `v1 /reconcile-recorder`, `/prune-missing`.
- `targeted-refresh.ts` (`refreshTargetedDocument`) — **no importer**, dead.

### 2.13 Clobus cloud 1C (backend/1c, ACTIVE, connector-less)
`/clobus/discover` logs into `clobus.uz` portal with user email+password via HTML form scrape, lists bases; `/clobus/connect` stores encrypted creds and syncs via OData Basic auth every 30 min (`cloud_onec.py:93-200`, `utils/clobus_portal.py`). A 1C path that needs no desktop connector. Service callers without user identity fall back to a sentinel owner `00000000-…-0001` (`cloud_onec.py:34`).

### 2.14 Odoo → 1C (ACTIVE)
`/api/internal/admin/odoo-inbox` (`odoo_inbox.py:206`) with instant path gated by `ODOO_INSTANT_ENABLED`, backstop + health beat tasks, `odoo_write.py:726` dispatches `write_to_1c` to the connector. Connector has no Odoo-specific code (1 file mention).

### 2.15 Manifest-driven table list (ACTIVE)
`/api/internal/connector/manifest` (no auth) returns provider tables + per-OneC `extraTables` (`connector.py:1-60`); admin can flip on any 1C table for one client via `PUT /internal/admin/onec/{id}/extra-tables` (`internal/routes/onec.py:194`). Venkon also forces GTD/import tables (`config.ts` `MANDATORY_MAIN_TABLES_BY_PROVIDER`). HR/payroll table mappings exist for venkon 1.3 (`config.ts` PROVIDER_MAPPINGS: hire, fire, payroll, vacation, timesheet…).

### 2.16 Telemetry / support bundle
Sentry (renderer + Rust crate), `sendDefaultPii:false`, 10% traces, no replay, write results logged (`maybeLogWriteResult`, `onec-sync-provider.tsx:1517-1524`), scrubber tests. Support bundle = `export_logs` from `/debug-logs` page (`debug-logs/page.tsx:36`). Device id = `machine_uid` (`utils/fingerprint.rs`). backend/backend accepts `X-MAC-Address`, `X-Location` headers on login (`utils/auth.py:150-152`) — connector does not send them.

### 2.17 Dev / emergency tools in the repo (not shipped)
`tools/pgsync` (connects as `postgres` superuser using creds from 1C cluster registry, uploads to backend — "DEV-ONLY … unacceptable to ship"), `tools/pull-table-direct.ps1` (emergency single-table pull with a user JWT), `scripts/dev/pg-dump.os`, `apps/app/scripts/dev/{sweep-probe,hash-one,adapter-path}.ts`, `scripts/styx-pin-autofill/StyxPinAutofill.exe` (types the token PIN into the vendor Styx dialog for unattended NBU bulk signing; PIN passed as CLI arg; not integrated in app), `scripts/upload-to-s3.ps1`, `upload-to-github.ps1`. Committed `apps/app/src-tauri/dev-certificate.pfx`.

### 2.18 Control-plane side notes (backend/backend, out of connector scope but surprising)
Admin "OTP Viewer" lists/deletes **plaintext OTPs** kept in Redis `otp_plain:{phone}` (`admin/views/otp_viewer.py:27-38`); admin "Cleanup" view bulk-deletes bank and Didox records via remote services (`admin/views/cleanup.py:68-98`).

### 2.19 Legacy `onec-ws` Node router (RETIRED, still built)
`backend/1c/onec-ws/index.js` (1498 lines) — old `wss://onec.aiba.group` router, which is still the connector's fallback `ONEC_SYNC_SOCKET_URL` default (`config.ts`). CI still builds/pushes/updates `aiba_1c-onec-ws` with `|| true` (`.gitlab-ci.yml:47-52,79,124,268`). It did check `unauthorized_sender` for some actions (`index.js:1469`) — stricter than its replacement.

---

## 3. Git history notes (connector, last 400)

- `c3fc269` (2026-08-09) event-log change detection added **and** dead pre-cloud layer removed (local drizzle DB, cron provider, `crypto.ts`, `onec-pull.ts`, venkon service). Commit says Rust was unverified.
- `bb7b729` (2026-07-24) removed leftover `/uilab` auth-bypass lines from `auth.tsx` (was on release).
- `c73f93f` (2026-09-07) removed `.github/workflows/recover-key.yml` — a one-shot workflow that exported the **Tauri updater signing private key** (encrypted to an owner RSA key) as an artifact. Implies the updater key was lost and recovered via CI.
- "`.exe` history purge" (origin/main 2026-08-16 "restore bank-connector sidecar after the .exe history purge") — history was rewritten.
- `e4e2a51` aiba-next mode: login/companies against Rust `/api/v2`, gated on base URL (`services/api.ts:49 isNextBackend`). Still present; local branches `next`, `onec-next`, `port/release-into-next`.
- `ee4107a` "stop the catalog probe scan destroying data every cycle"; probe-scan still wired (`sync-orchestrator.ts`).
- `0347fe2/157c72a` tunnel `base=signer` (admin API over tunnel); `0b756ab` iABS7 routing.
- `4ef5461` cloud commands for unpost/edit/delete by ref; `13100d8` post existing doc; `06b4358` `DELETE ?hard=true` actually deletes.
- `47212bf` direct-SQL bulk (bcp+keyset) — later declared dev-only experiment.
- Unmerged remote: **`origin/security/fixes-1.3.51`** (2026-08-04, "stop blocking the UI thread; bound every external command") never merged into `release`; plus `opt/onec`, `release-1.3.101/103` side branches.

---

## Most surprising findings (ranked)

1. **Unauthenticated cloud-command path into every online connector.** `/api/v2/connector/ws` accepts `write_to_1c`, `delete_from_1c`, `employee_fire_from_1c`, etc. from any socket, and lets anyone self-declare a connector identity (`connector_ws.py:99-125`, `cloud_router_ws.py:50-68`).
2. **Relay tunnel gives the backend arbitrary-path HTTP into the local bank signer admin API (:7777) and signing frames to the operator's chips.** No path allow-list (`tunnel_agent.rs:406-439`). It auto-connects after one visit to /settings.
3. **1C adapter reachable from the LAN without auth**: `[::]:55899` bind + inbound firewall allow-rule added by `start_adapter`, exposing `/query`, writes, hard deletes, the `hs` proxy (`adapter-router main.rs:2717`, `filesystem.rs:1227-1260,1595`).
4. **Odoo/BI partner tokens can unpost and hard-delete 1C documents** through `connectors_admin` (`dependencies.py:10-41`).
5. **The connector forces `exchange=true` on HR/payroll writes** (`onec-sync-provider.tsx:4688-4714`), against the workspace rule.
6. **Heal panel edits and `/UpdateDBCfg`s the customer's 1C configuration** from the desktop app (`heal_extconn.rs:409-559`).
7. **Passwordless `v8users` read via psql trust auth** on server bases (`onec_cluster.rs`).
8. Connector kills vendor Styx and grabs bank ports on every launch; OS-global **Alt+B** hotkey; devtools in prod via Mod+D→Mod+I.
9. ~6k lines of event-log subsystem shipped and mounted but hard-disabled; `targeted-refresh.ts`, `scan_base_heal_status`, Kapital webview scraper, `/connections/ws` socket, `tunnel-activate-request`, `signer-status` events all dead.
10. Updater signing key was recovered via a CI workflow; `dev-certificate.pfx` committed; `security/fixes-1.3.51` branch never merged.

## Open questions

1. Is `/api/v2/connector/ws` reachable from the public internet on `1c.aiba.group` (prod `.env.production` says `wss://1c.aiba.group/api/v2/connector/ws`)? Is `/ws/1c` routed by the edge?
2. Who runs `relay.aiba.uz` ("Qo'lda backend"), how is the connector token bound to a tenant, and which `path`s on `:7777` does it actually send? Can the relay create payments without operator action?
3. Does oscript `TCPСервер(port)` bind 0.0.0.0? (Router `[::]` is certain.) Is there any network ACL on client machines?
4. Who calls `connectors_admin` in production (aiba-next MCP, cloud-os, Odoo)? Is the MCP "no unpost/delete" rule enforced anywhere server-side?
5. Is `exchange=true` on HR docs intentional (bypassing exchange-plan registration) and approved?
6. Is `employee_list_from_1c` expected to work? Backend routes it, connector has no handler.
7. What is in `origin/security/fixes-1.3.51`, and why was it never merged into `release`?
8. Is `dev-certificate.pfx` password-protected / ever used to sign shipped builds?
9. Is the `github.com/IsaqjonovII/aiba-connector` remote intended to hold the full source (incl. adapter, bank signer binary)?
10. Should the legacy `aiba_1c-onec-ws` service and the dead `backend/1c/app/routes/**` tree be removed?
