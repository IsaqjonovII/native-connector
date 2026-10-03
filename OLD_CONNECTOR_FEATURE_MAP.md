# Old AIBA Connector: feature map

One row per feature of the old Connector (connector `release` @ `cfab3a1`, backend/1c
`origin/development` @ `b2e3590`). Evidence and verification in `OLD_CONNECTOR_DEEP_AUDIT.md`.

Columns: **Active?** ACTIVE / RARE (manual or settings only) / FLAGGED / UNUSED / DEAD /
BROKEN. **Useful?** Y / N / ? (unknown consumer) / RISK. **New Connector** DONE / PARTIAL /
MISSING / SUPERSEDED / DROPPED (intentionally) / N/A.

## Account and identity

| Feature | Subsystem | Trigger | Backend/Local | Active? | Useful? | New Connector | Notes |
|---|---|---|---|---|---|---|---|
| Login phone+password (`/api/v1/auth/login`) | auth | user | backend/backend | ACTIVE | Y | DONE | D41 |
| aiba-next login mode (`/api/v2`) | auth | base URL contains `/api/v2` | aiba-next | FLAGGED | ? | MISSING | session dies on reload (no refresh) |
| Refresh on 401 | auth | any 401 | backend/backend | ACTIVE | Y | DONE | |
| Token storage | auth | — | local | ACTIVE | RISK | SUPERSEDED (DPAPI) | old: plaintext localStorage |
| Companies list | auth | login | backend/backend | ACTIVE | Y | DONE | |
| Token keepalive + push bearer to signer `:7777` | auth/bank | 60 s | local | ACTIVE | RISK | DROPPED (no banks) | also `aiba_token.json` plaintext |
| Device id = MachineGuid | identity | startup | local | ACTIVE | Y | DONE (`DeviceId.cs`) | cached forever, cloned VMs collide |
| `X-Device-Id` header | identity | every API call | backend/backend audit | ACTIVE | Y | DONE | |
| `X-User-Agent: AIBA-Connector` device naming | identity | — | backend/backend | DEAD (header commented) | Y | MISSING | login notification "via AIBA Connector" never fires |
| Prod/dev switch Ctrl+Shift+D | shell | hotkey | local | ACTIVE (hidden) | RISK | DONE (+ badge) | any user can flip |

## Cloud connection records

| Feature | Subsystem | Trigger | Backend/Local | Active? | Useful? | New Connector | Notes |
|---|---|---|---|---|---|---|---|
| List company infobases | records | page | backend/1c v2 | ACTIVE | Y | DONE | v2 list has no user filter |
| Create record (`POST /onec`) | records | form | backend/1c | ACTIVE | Y | DONE | no company-access check server side |
| Edit record (name/odataName/version) | records | form | backend/1c | ACTIVE | Y | MISSING | |
| Delete record + purge data | records | row menu | backend/1c | ACTIVE | Y | DONE (with confirm) | |
| Entity purge only (`DELETE /entity/oneC/{id}`) | records | row menu | backend/1c | ACTIVE | Y | MISSING | local cursors not reset after purge |
| HTTP status heartbeat 60 s | presence | timer | backend/1c Mongo | ACTIVE | Y | DONE (own records only) | |
| Mark inactive on tray Quit | presence | quit | backend/1c | ACTIVE | Y | DONE (on close) | ignored on /login, debug pages |
| Org bindings GET/PUT (multi-org) | records | modal | backend/1c | ACTIVE | Y | PARTIAL (engine reads; no edit UI) | |

## Control plane (WebSocket)

| Feature | Subsystem | Trigger | Backend/Local | Active? | Useful? | New Connector | Notes |
|---|---|---|---|---|---|---|---|
| Presence socket (onec-hub) | control | always | backend/1c hub | ACTIVE | Y | MISSING | duplicate of command socket |
| Command socket hello/heartbeat/state_update | control | always | backend/1c hub | ACTIVE | Y | MISSING | no auth (D41 "not built on purpose") |
| `sync_now` | command | admin/aiba-next | backend/1c SS | ACTIVE | Y | MISSING | no fast ack for whole base |
| `pull_now` / read-table | command | admin, provodka-ai, report refresh 03:00 | backend/1c | ACTIVE | Y | MISSING | |
| `write_to_1c` | command | Odoo, avtoprovodka, aiba-next | backend/1c | ACTIVE | Y | MISSING (local write route DONE) | |
| `post_1c_document` | command | admin | backend/1c SS | ACTIVE | Y | MISSING (local DONE) | |
| `unpost_1c_document` | command | admin | backend/1c SS | ACTIVE | Y | MISSING (local DONE) | destructive |
| `update_1c_document` | command | admin | backend/1c SS | ACTIVE | Y | MISSING (local DONE, AIBA docs only) | |
| `delete_1c_document` (`hard`) | command | admin | backend/1c SS | ACTIVE | RISK | MISSING (local DONE, AIBA docs only) | physical delete |
| `catalog_update` CAS | command | kansler ИКПУ | backend/1c SS | ACTIVE | Y | MISSING | no catalog write op at all |
| `document_read` | command | EDO draft import | backend/1c SS | ACTIVE | Y | MISSING (local read DONE) | ≤200 rows |
| `catalog_read` | command | EDO | backend/1c SS | ACTIVE | Y | MISSING (local read DONE) | |
| `employee_write_to_1c` | command | HR / soliq Mehnat | hub NONE | ACTIVE | Y | MISSING | reports success on failed hire (A) |
| `employee_fire_from_1c` | command | HR | hub NONE | ACTIVE | Y | MISSING | exchange=true |
| `employee_list_from_1c` | command | soliq, aiba-next lane | hub | BROKEN | Y | MISSING | no handler |
| `check_exists` | command | HR, avtoprovodka | hub NONE | ACTIVE | Y | MISSING | |
| `delete_from_1c` (AIBA HR markers) | command | HR | hub NONE | ACTIVE | Y | MISSING | |
| `push_to_1c` departments/schedules | command | HR | hub NONE | ACTIVE | Y | MISSING | other types → hidden queue |
| `delete_in_1c` departments | command | HR | hub NONE | ACTIVE | Y | MISSING | |
| `holiday_push/delete/check` | command | HR | hub NONE | ACTIVE | Y | MISSING | info-register writes |
| `list_resource` schedules/departments/employees | command | HR | hub NONE | ACTIVE | Y | MISSING | |
| `list_resource` refdata / `refdata_get` | command | avtoprovodka | hub | BROKEN / UNREACHABLE | Y | MISSING | reply dropped |
| `get_list` | command | sotuv, HR | hub NONE | ACTIVE | Y | MISSING | |
| `create_ref` | command | sotuv, HR | hub NONE | ACTIVE | RISK | MISSING | `_postCreateMethod` |
| `server_ping` | command | cloud | hub | ACTIVE | Y | MISSING | fan-out without target |
| `get_bases` (connector branch) | command | — | — | UNUSED | N | N/A | hub answers |
| `department_write_to_1c` | command | legacy relay | — | UNREACHABLE | N | N/A | |
| `connector_status_request` | command | none | — | DEAD | Y | MISSING | useful "refresh presence" |
| `read_table` | command | none | — | DEAD | N | N/A | |
| `write_progress` frames 10 s | command | during writes | hub | ACTIVE | Y | MISSING | |
| Unknown frames → `/documents` hidden writer | command | fallthrough | local | RARE | RISK | DROPPED | raw fetch, no gate |
| Global write gate (90 s) parks sync | command | writes | local | ACTIVE | Y | SUPERSEDED (per-base write gates, D12) | |

## Adapter (local data API)

| Feature | Subsystem | Trigger | Backend/Local | Active? | Useful? | New Connector | Notes |
|---|---|---|---|---|---|---|---|
| Catalog list/page/by id | adapter | sync, writes | local | ACTIVE | Y | DONE | parity 682/682 |
| Document list/page/by id/batch | adapter | sync, writes | local | ACTIVE | Y | DONE | parity 525/525 |
| Register pages (acc/accum/info) | adapter | sync | local | ACTIVE | Y | DONE | parity 1,149/1,149 |
| Charts, enums, calc types | adapter | sync, writes | local | ACTIVE | Y | PARTIAL (chart op; enums/calcTypes via reads?) | |
| `/counts` batch | adapter | sync | local | ACTIVE | Y | SUPERSEDED (versions + feed) | |
| `/metadata` | adapter | writes | local | ACTIVE | Y | PARTIAL (`version` op + platform version) | |
| `/schema/*` | adapter | writes, export | local | ACTIVE | Y | PARTIAL (`tables` op) | |
| `/api/status`, `/databases/status` | adapter | 15 s | local | ACTIVE | Y | SUPERSEDED (`/v1/health`, `/v1/hosts`, `/v1/bases`) | |
| Document create / update / post / unpost / delete | adapter | cloud, local | local | ACTIVE | Y | DONE (AIBA-owned only) | D18, D38 |
| `exchange=true`, FORCE Load-mode post | adapter | HR, bank | local | ACTIVE | RISK | DROPPED | |
| `_idempotencyMarker` | adapter | writes | local | ACTIVE | Y | DONE | |
| `_monthlyUpsert` | adapter | payroll-type writes | local | ACTIVE | ? | DROPPED (400) | |
| Find-or-create refs, fuzzy nomenclature, subconto prediction | adapter | writes | local | ACTIVE | ? | DROPPED (explicit create only) | |
| Bank account auto-create | adapter | bank writes | local | ACTIVE | Y | PARTIAL (`allowCreate` flag) | |
| Catalog create/update/delete | adapter | cloud | local | ACTIVE | Y | MISSING | |
| Info-register write/delete | adapter | HR | local | ACTIVE | Y | MISSING | |
| `/stock` | adapter | sync side lane | local | ACTIVE | ? | DROPPED unless consumer (D-7) | |
| Accumulation `/turnover` | adapter | reports lane | local | ACTIVE | ? | MISSING | |
| `POST /query` | adapter | 2 internal texts | local | ACTIVE (internal) | RISK | DROPPED (whitelisted reads) | swallows errors |
| `/healScan` | adapter | heal panel | local | RARE | Y | MISSING | |
| `/changes` | adapter | none | local | UNUSED | N | SUPERSEDED (event log) | |
| `sotuv/istochnik-map` | adapter | none here | local | UNUSED | ? | MISSING | |
| `sql-dump`, `db-structure` | adapter | none | local | UNUSED | RISK | DROPPED | |
| `hs`/`http` proxy | adapter | none | local | UNUSED | RISK | DROPPED | |
| constants, processes, tasks, exchange plans, characteristics, openapi | adapter | none | local | UNUSED | N | DROPPED | |
| XDTO bulk (`bulk=1`) | adapter | flag | local | FLAGGED | N | DROPPED (D35) | |
| CLI `--discover` (ibases.v8i) | adapter | DB modal | local | RARE | Y | DONE (`LauncherBases.cs`) | |
| CLI `--test-connections` + comVersion detection | adapter | DB modal | local | RARE | Y | DONE (probe + retry) | old flips machine-wide comcntr |
| CLI `--configure`, `--add-db`, `--status` | adapter | none | local | UNUSED | N | N/A | |
| LAN bind + firewall rule | adapter | every start | local | ACTIVE | RISK | SUPERSEDED (loopback + token) | |
| Optional bearer auth | adapter | config | local | UNUSED (off) | Y | SUPERSEDED (`X-AIBA-Token`) | turning it on breaks the old app |

## Process and platform

| Feature | Subsystem | Trigger | Backend/Local | Active? | Useful? | New Connector | Notes |
|---|---|---|---|---|---|---|---|
| Router + ownership worker pool | runtime | start | local | ACTIVE | Y | SUPERSEDED (Supervisor + per-version hosts) | |
| Rust watchdog 1 s + UI health 15 s | runtime | always | local | ACTIVE | Y | SUPERSEDED (supervisor restart/backoff) | |
| Kill sweep in `ensure_adapter_dir` | runtime | ~15 commands | local | ACTIVE | N | DROPPED | kills own live router |
| comcntr regsvr32 / preference / version flip | runtime | start, tests | local | ACTIVE | N | SUPERSEDED (registry-free, D29) | |
| Elevation self-relaunch | runtime | startup | local | ACTIVE | ? | N/A | fires on ghost smart-card readers |
| Reports worker `:55949` | runtime | sync | local | ACTIVE | ? | MISSING | |
| Partition workers (cold read) | runtime | flag | local | FLAGGED | Y | DONE (slices) | |
| Lazy connect, file connect lock, circuit breaker | runtime | — | local | ACTIVE | Y | DONE | |
| 1uz .NET adapter `:55900` | runtime | 1uz tenants | local | ACTIVE | Y | MISSING | |
| Heal «Внешнее соединение» (`heal_base`) | runtime | heal panel | local | RARE | Y | MISSING | edits customer config |
| 1C users list (`1Cv8.1CD`, psql/sqlcmd trust) | runtime | DB modal | local | RARE | Y | MISSING (explicit) | |
| Old config backups ×20 | runtime | every config write | local | ACTIVE | Y | N/A | plaintext passwords |
| Autostart HKCU Run | platform | startup | local | ACTIVE | Y | MISSING | not removed on uninstall |
| Tray | platform | — | local | ACTIVE | Y | MISSING | |
| Updater (GitHub, forced modal) | platform | 30 min | GitHub | ACTIVE | Y | MISSING | `relaunch` plugin not registered |
| Debug logs window Alt+B (global) | diag | hotkey | local | ACTIVE | Y | PARTIAL (Activity page) | steals Alt+B |
| Sentry renderer + Rust | diag | errors | Sentry | ACTIVE (if DSN) | Y | MISSING | PII fields (A2) |
| Dev settings modal | diag | Ctrl+Shift+B / `?dev=true` | local | ACTIVE (hidden) | Y | N/A | URL overrides |
| DevTools in release | diag | Ctrl+D, Ctrl+I | local | ACTIVE (hidden) | RISK | N/A | |
| `write_file` / export data | diag | export | local | RARE | Y | MISSING | near-unrestricted path |

## Sync

| Feature | Subsystem | Trigger | Backend/Local | Active? | Useful? | New Connector | Notes |
|---|---|---|---|---|---|---|---|
| Poller one base per tick ~60 s | sync | timer | local | ACTIVE | Y | SUPERSEDED (scheduler + feed) | |
| Sync all Ctrl+Shift+S | sync | hotkey | local | ACTIVE (hidden) | Y | N/A | |
| Orchestrator with priority order and 10 min watchdog | sync | poller | local | ACTIVE | Y | SUPERSEDED | |
| Adaptive page size + poison rows | sync | reads | local | ACTIVE | Y | SUPERSEDED | |
| Count gate / refhash diff / probe scan / two-strike delete | sync | per table | backend/1c v1 | ACTIVE | Y | SUPERSEDED (feed, versions) | |
| Upload multipart, 413 split | sync | per page | backend/1c | ACTIVE | Y | DONE (Python target, local only) | |
| GZIP upload | sync | flag | backend/1c | FLAGGED / BROKEN | N | DROPPED | |
| Prune-missing (5 % cap) | sync | delete detect | backend/1c | ACTIVE | Y | DONE (target) | |
| Reconcile-recorder | sync | changed docs | backend/1c | ACTIVE | Y | DONE (target) | old uses `scope=connection` |
| Multi-org org-router | sync | per row | local | ACTIVE | Y | DONE (S10) | |
| Sync-tables GET/PUT + auto-seed + drawer | sync | each run | backend/1c v1 | ACTIVE | Y | PARTIAL | `[]`/`schedule` traps |
| Night-only accounting lane | sync | policy | local | ACTIVE | ? | DROPPED (D-8) | |
| ЧекККМ manual only | sync | policy | local | ACTIVE | ? | ? (D-8) | |
| Mandatory venkon GTD tables | sync | always | local | ACTIVE | Y | ? | |
| Stock snapshot | sync | each cycle | backend/1c | ACTIVE | ? | DROPPED unless consumer | |
| Data coverage | sync | each cycle | backend/1c | ACTIVE | ? | DROPPED unless consumer | |
| Retail turnover push (14 d / 400 d) | sync | reports lane | backend/1c | ACTIVE | ? | MISSING | |
| 1uz reconciliation ledger | sync | 1uz | 1uz backend | ACTIVE | Y | MISSING | |
| Event-log change feed | sync | — | local | DEAD | Y | DONE (rewrite reader) | |
| `targeted-refresh.ts` | sync | none | local | DEAD | N | N/A | |
| Production upload | sync | — | backend/1c | ACTIVE | Y | MISSING (not approved, D42 D-2) | |

## Banks (out of rewrite scope, D40)

| Feature | Subsystem | Trigger | Backend/Local | Active? | Useful? | New Connector | Notes |
|---|---|---|---|---|---|---|---|
| 11 bank integrations via signer `:7777` | banks | pages | local + bank backend | ACTIVE | Y | DROPPED (D40) | |
| Bank reconnect deletes cloud subscription first | banks | reconnect | bank backend | ACTIVE | RISK | DROPPED | history cascade |
| NBU payments + periodic auto-sign | banks | timer | bank backend + Styx | ACTIVE | RISK | DROPPED | no review |
| Keys: chip activation, pairing, USB isolation | banks | keys page | local | RARE | Y | DROPPED | |
| SQB Playwright sidecar | banks | startup | local | ACTIVE | Y | DROPPED | system python fallback |
| Relay tunnel to signer admin | banks | after Settings visit | relay.aiba.uz | ACTIVE | RISK | DROPPED | any path |
| Kapital webview login/sync | banks | none | local | UNUSED | RISK | DROPPED | |
| `usb_test_disable_one` | banks | none | local | UNUSED | RISK | DROPPED | |
| ePass `getCertInfo` at start | banks | seed | local | ACTIVE (result unused) | N | DROPPED | |
| Unisoft OData KPI dashboard | other | orphan page | local | UNUSED | N | DROPPED | |
| `/documents` Didox → 1C page | other | URL only | local | RARE | ? | MISSING | non-canonical comment |

## Unused Tauri commands (22)

`check_network_status`, `ensure_adapter_firewall_rule` (runs internally), `get_log_file_path`,
`get_runtime_environment_status`, `get_signer_health`, `get_sqb_health`, `restart_sqb_sidecar`,
`install_adapter`, `list_com_connector_versions`, `set_com_connector_version`, `list_readers`,
`usb_test_disable_one`, `open_kapital_auth_window`, `sync_kapital_via_webview`,
`parse_valuetable_xml`, `scan_base_heal_status`, `set_adapter_kind`, `set_adapter_port`,
`set_backend_locale`, `stop_reports_worker`, `tunnel_get_config`, `tunnel_set_config`. Of 77
registered (`connector/apps/app/src-tauri/src/lib.rs:806-882`).
