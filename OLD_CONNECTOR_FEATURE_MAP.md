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
| Device id = MachineGuid | identity | startup | local | ACTIVE | Y | DONE (`DeviceId.cs`) | cached forever, cloned VMs collide; re-verified unchanged by 2nd pass B (zero commits touched the relevant files since first pass) — also collides across multiple processes/sessions of the same user on one machine, and a copied user profile carries a stale cached GUID that is never revalidated against the new machine's real registry key |
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
| Presence socket (onec-hub) | control | always | backend/1c hub | ACTIVE | Y | MISSING | duplicate of command socket; closing either socket unconditionally deletes the WHOLE presence record incl. the other's routes — "overwrite" half of this bug (empty `[]` clobbering the real base list) is now FIXED (`_normalize_infobases` returns `None`, not `[]`); "unregister-on-close" half is UNCHANGED and is now the dominant failure mode, and does NOT self-heal via heartbeat (2nd pass B) |
| Command socket hello/heartbeat/state_update | control | always | backend/1c hub | ACTIVE | Y | MISSING | no auth (D41 "not built on purpose"); `capabilities.commands` gained a 2nd, 30-day storage tier (`connector-commands:{id}`) after a real prod incident (KANSLER 2026-10-05, 74 ИКПУ cards blocked for hours on `connector_unsupported`) — 2nd pass B |
| `sync_now` | command | admin/aiba-next `onec_pull.rs`/MCP `onec_trigger_sync` | backend/1c SS | ACTIVE | Y | MISSING | no fast ack for whole base; MCP tool traced exactly, 60s per-base cooldown (2nd pass A) |
| `pull_now` / read-table | command | admin, report refresh 03:00, kansler `onec_pull.rs::dispatch_read_table`, aiba-next `onec_pull.rs::pull_table` | backend/1c | ACTIVE | Y | MISSING | MCP deliberately excludes this (2 dedicated tests force callers to `sync_now` instead); 2nd pass A/D |
| `write_to_1c` | command | Odoo, cloud-os avtoprovodka JS, **aiba-next native HR** (`employees.rs` payroll-send-1c/roster-sync/timesheet), **kansler-bank "Prixod"** (`kansler_bank.rs`), **cloud-os AI chat tool** (`AiChatController.php::toolOnecWrite`, 6 business areas + HR hire) | backend/1c | ACTIVE, materially broader than first pass | Y / RISK (AI chat path) | MISSING (local write route DONE) | 2nd pass A/C found 3 new real callers; AI chat is a live LLM-tool-callable writer, unaudited confirmation/rate-limit |
| `post_1c_document` | command | admin, **aiba-next kansler-bank EDO "Post to 1C" button** (`kansler_bank.rs:8135-8230`, 120s timeout by design vs backend's 90s cap) | backend/1c SS | ACTIVE | Y | MISSING (local DONE) | 2nd pass A found 2nd real caller |
| `unpost_1c_document` | command | admin; **MCP `onec_unpost_document`** (confirm-gated) | backend/1c SS | ACTIVE | Y | MISSING (local DONE) | destructive; MCP default-denies this tool (`mcp.rs:146` `DEFAULT_DENIED_TOOLS`) but backend/1c itself enforces nothing — a raw call with `MCP_API_TOKEN` bypassing backend-mcp succeeds (2nd pass A/C) |
| `update_1c_document` | command | admin; **MCP `onec_update_document`** (not denied, header-only) | backend/1c SS | ACTIVE | Y | MISSING (local DONE, AIBA docs only) | tabular sections explicitly refused; 2nd pass A |
| `delete_1c_document` (`hard`) | command | admin; **MCP `onec_delete_document`** (confirm-gated, default-denied, soft-only — tool never sets `hard`) | backend/1c SS | ACTIVE | RISK | MISSING (local DONE, AIBA docs only) | physical delete only reachable via raw admin route with any of 4 equal shared secrets, never via MCP; `AIBA_BI_API_TOKEN` has same reach, zero real sender (2nd pass A/C) |
| `catalog_update` CAS | command | **confirmed**: kansler `ikpu_audit/write.rs:547` (ИКПУ classifier-code audit/repair) | backend/1c SS | ACTIVE, confirmed by a dated prod incident (KANSLER 2026-10-05, 74 cards blocked for hours) | Y | MISSING | no catalog write op at all; caller's exact code found by 2nd pass C (first pass only had a docstring claim) |
| `document_read` | command | **EDO draft import only** (connector-side comment names it explicitly) | backend/1c SS | ACTIVE, narrow single-purpose caller — not a general read path | Y | MISSING (local read DONE) | ≤200 rows (connector-enforced); no sync-config check — any `Document_*` name readable live regardless of sync list (2nd pass F) |
| `catalog_read` | command | **EDO draft import only** | backend/1c SS | ACTIVE, same caveat | Y | MISSING (local read DONE) | 200 ids/call, enforced both sides; live, not stored (2nd pass F) |
| `adapter_read` (**NEW — 26th command, added 2026-10-04, after the first-pass audit**) | command | **backend/1c's own `vat_registry.py:355`/`vat_regulated.py:24,114`** (VAT/QQS reporting — live query is the PRIMARY path, synced data only a fallback on timeout) | backend/1c SS (any of 4 shared secrets) | ACTIVE for those 2 internal callers; **NO cloud-side caller found** (aiba-next, MCP, cloud-os all zero hits) | RISK | MISSING | functional reincarnation of raw `/query`: GET mode no row cap, QUERY mode caller-supplied ВЫБРАТЬ/SELECT text up to 20,000 chars, no row cap, bypasses org-binding routing; superseded first pass's "`/query` used only with fixed internal texts" claim (2nd pass A/F) |
| `employee_write_to_1c` | command | HR / soliq Mehnat — confirmed real UI: `cloud-os/apps/aiba_employees/js/employees.js:6850-6875` | hub NONE | ACTIVE | Y | MISSING | reports success on failed hire (A); legacy-only, zero aiba-next/MCP reimplementation (2nd pass A) |
| `employee_fire_from_1c` | command | HR — confirmed: `employees.js:4158-4197` (`firePushTo1C()`) | hub NONE | ACTIVE | Y | MISSING | exchange=true; legacy-only (2nd pass A/C) |
| `employee_list_from_1c` | command | soliq, aiba-next lane | hub | BROKEN | Y | MISSING | no handler; reconfirmed unchanged by 2nd pass A |
| `check_exists` | command | HR, avtoprovodka | hub NONE | ACTIVE (legacy NC only) | Y | MISSING | zero aiba-next/MCP caller found (2nd pass A) |
| `delete_from_1c` (AIBA HR markers) | command | HR | hub NONE | ACTIVE (legacy NC only) | Y | MISSING | zero aiba-next/MCP caller found (2nd pass A) |
| `push_to_1c` departments/schedules | command | HR | hub NONE | **ACTIVE CAPABILITY, NO CURRENT SENDER FOUND** (narrowed from flat ACTIVE) | Y | MISSING | other types → hidden queue; exhaustive literal-string sweep of cloud-os/web×14/kansler/aiba-next/backend-backend found zero real caller (2nd pass C) |
| `delete_in_1c` departments | command | HR | hub NONE | **ACTIVE CAPABILITY, NO CURRENT SENDER FOUND** | Y | MISSING | same sweep, zero caller (2nd pass C) |
| `holiday_push/delete/check` | command | HR — confirmed: `payroll.js:6466,6477`; `HolidayController.php:142-148` confirms PHP deliberately never calls 1C itself, only the browser does | hub NONE | ACTIVE (legacy NC only) | Y | MISSING | info-register writes; zero modern reimplementation (2nd pass A/C) |
| `list_resource` schedules/departments/employees | command | HR | hub NONE | ACTIVE (legacy NC only) | Y | MISSING | |
| `list_resource` refdata / `refdata_get` | command | avtoprovodka | hub | BROKEN / UNREACHABLE | Y | MISSING | reply dropped; reconfirmed unchanged (2nd pass A) |
| `get_list` | command | sotuv (`aiba-next/backend/crates/accounting/src/modules/sotuv.rs`, `kansler/backend/.../sotuv.rs`), HR | hub NONE | ACTIVE | Y | MISSING | |
| `create_ref` | command | sotuv, HR — confirmed: `employees.js:2023,4096-4109`, `sotuv.rs:8366-8373`, `kansler/.../sotuv.rs:9545` | hub NONE | ACTIVE | RISK (latent, not exercised) | MISSING | `_postCreateMethod` is RCE-shaped (`Рефлектор.ВызватьМетод` on any common module, no allow-list) and passed through unfiltered by both connector and backend/1c, but confirmed **unused** — every real caller sends empty/absent `extras` (2nd pass C) |
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
| `POST /query` | adapter | 2 internal texts | local | ACTIVE (internal) | RISK | DROPPED (whitelisted reads) | swallows errors; superseded in significance by `adapter_read` QUERY mode below — the same route is now reachable with CALLER-supplied text, not just fixed internal strings |
| `adapter_read` (**NEW 26th command**, both sides shipped 2026-10-04, after first-pass audit) | adapter via admin-API relay | backend/1c `vat_registry.py`/`vat_regulated.py` (live is PRIMARY path for VAT/QQS reports, not fallback) | local + backend/1c SS | ACTIVE for those 2 callers; NO cloud-side caller found anywhere | RISK | MISSING | GET mode: path restricted to `catalogs/documents/enums/schema/metadata/calcTypes`, no row cap. QUERY mode: caller-supplied ВЫБРАТЬ/SELECT up to 20,000 chars, no row cap, bypasses org-binding routing; reachable by any of 4 equal shared secrets; real prod timeout observed (KANSLER 2026-10-06, `purchase_goods: timeout`, fell back to synced docs) — 2nd pass A/F |
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
| Reconcile-recorder | sync | changed docs | backend/1c | ACTIVE | Y | DONE (target) | old uses `scope=connection`, confirmed SAFE (not the rewrite's D46 bug): old connector always reads each base once whole, keyed by the connection's own id, and partitions only on upload — the key set passed to reconcile/prune is always base-wide, matching exactly what `scope=connection` requires; backend/1c's own `_scoped_onec_ids` guard additionally refuses `scope=connection` on any org-binding id (HTTP 400) (2nd pass F, proof traced architecturally not just by absence-of-bug) |
| Multi-org org-router | sync | per row | local | ACTIVE | Y | DONE (S10) | **rewrite's cross-org deletion bug (DECISIONS.md D46) does NOT exist in the old connector** — proven: upload partitioning happens strictly after the delete/reconcile key-set comparison, never feeding into it. A real but different limitation remains: org-less movement rows and re-mapped documents can leave stale/invisible copies in the wrong partition (a staleness bug, backend-acknowledged via `_stranded_movement_scan`, not a cross-org deletion bug) (2nd pass F) |
| Sync-tables GET/PUT + auto-seed + drawer | sync | each run | backend/1c v1 | ACTIVE | Y | PARTIAL | `[]`/`schedule` traps confirmed at the exact Python-truthiness level (`stored if stored else None`); ONE write door (`onec.py:898`) but THREE real callers — connector drawer, connector auto-seed, and **NEW: kansler's GTD-sync panel** (`ved_sync.rs`, real accountant-facing feature, merges+PUTs then immediately `pull_now`s, 15-min sweep) which independently had to hand-roll a 409 defense against the same `[]`-vs-`null` ambiguity; a 4th generalized route (`request-tables`) is built+reachable with ZERO UI caller anywhere (2nd pass D) |
| Night-only accounting lane | sync | policy | local | ACTIVE | ? | DROPPED (D-8) | |
| ЧекККМ manual only | sync | policy | local | ACTIVE | ? | ? (D-8) | |
| Mandatory venkon GTD tables | sync | always | local | ACTIVE | Y | ? | |
| Stock snapshot | sync | each cycle | backend/1c | ACTIVE | **Y — REQUIRED, proven consumer** | DONE needed (not DROPPED) | real shipped consumer found: aiba-next `warehouse.rs::stock_1uz` + frontend tab "1UZ jonli qoldiq" (`stock-1uz.tsx`), gated to 1uz/BePro-base companies, full i18n — reference view alongside native Sklad 2.0 (2nd pass E) |
| Data coverage | sync | each cycle | backend/1c | ACTIVE | **Y — REQUIRED, LOAD-BEARING FOR ACCOUNTING CORRECTNESS** | DONE needed (not DROPPED) | real consumer found: aiba-next `sotuv.rs::mirror_reach_from_onec` — decides whether "document absent from mirror" licenses creating a NEW accounting document in the avtoprovodka write path; without it, partial backfill risks duplicate documents. NOT surfaced as any user/support-visible health indicator today (2nd pass E) |
| Retail turnover push (14 d / 400 d) | sync | reports lane | backend/1c | ACTIVE (producer side) | **DEAD-leaning — exhaustive search found zero consumer** | MISSING | machinery (ingest route, Mongo collection, indexed fast-path reader, manual rebuild endpoint, Celery refresh) is real and maintained but zero caller found in aiba-next frontend/backend, cloud-os, web×14, backend/report, backend/backend; one unchecked gap is the separate production `aiba-cloud` tenant-API service (not a local checkout) (2nd pass E). Raw retail entity slugs (`retail-sales`/`retail-revenue`) ARE wired into aiba-next's External API/BI product as grantable endpoints — LIKELY REQUIRED as part of that generic surface even without a dedicated dashboard |
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
| Relay tunnel to signer admin | banks | after Settings visit | relay.aiba.uz | ACTIVE | RISK | DROPPED | NOT "any port" — scoped to exactly 2 fixed local targets (`:7777` bank-signer admin, `:6210` crypto helper) selected by the frame's `http.base` field; within those 2, fully unauthenticated/unrestricted path+method+body, and `:7777` is bank-connectro's own ~100-route admin API incl. `create-payment` for all 10 integrated banks (precondition: an already-active login session, not a security control); relay itself (`relay.aiba.uz`) is NOT implemented anywhere in this workspace — confirmed by exhaustive grep, operated externally, its own auth/tenant-binding is unauditable from any checked-out repo (2nd pass C) |
| cloud-os AI chat → `write_to_1c` (**NEW row, not in first pass**) | other / banks-adjacent | user chat message | cloud-os `AiChatController.php::toolOnecWrite` | ACTIVE | RISK | MISSING | LLM tool-callable writer: sends `write_to_1c`/`employee_write_to_1c` with `post_document:true` across 6 business areas (timesheet, leaves, premiums, deductions, payroll, journal entries) plus HR hire; a write can originate from what a user TYPED to the chat assistant, not just a deliberate form submit; confirmation/rate-limit/audit-trail parity with the human UI not examined (2nd pass C) |
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
