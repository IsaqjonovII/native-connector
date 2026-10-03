# A — Old AIBA Connector, TypeScript/React side: what the code actually does

- **Repo:** `D:\aiba\connector`, branch `release`, HEAD `cfab3a1` ("ci: let the release guard pass for a version that is not published yet", 2026-09-30). App version 1.3.109 (`apps/app/package.json`).
- **Scope:** `apps/app/src`, 268 non-test TS/TSX files, about 88k lines. The biggest files:
  - `providers/onec-sync-provider.tsx` (15,452 lines)
  - `app/(dashboard)/documents/page.tsx` (5,670)
  - `features/accounting/lib/sync/sync-orchestrator.ts` (3,202)
  - `app/(dashboard)/banks/page.tsx` (3,074)
  - `utils/incremental-fetcher.ts` (2,527)
  - `onec/services/unisoft.ts` (2,520)
- **Method:** read-only. Nothing was run, edited or sent. All paths below are relative to `apps/app/src` unless stated.
- **How status was judged:** by grepping for callers, mounts, flags and sidebar links. It was not checked against runtime telemetry.

**How this report was built:**
- The onec-sync-provider section and the banks/documents/unisoft section come from two full-file sub-audits.
- The app shell and the sync engine were re-derived here from targeted code reads. Their sub-audit results never reached me, so those two parts are less exhaustive.

---

## 0. Bugs and risks at the top

1. **`exchange=true` is still sent on HR writes.** This breaks the workspace hard rule.
   - `providers/onec-sync-provider.tsx:4688-4698` defines `DOCS_REQUIRING_EXCHANGE_MODE` with 9 document types: НачислениеЗарплатыРаботникамОрганизаций, НачислениеОтпуска…, НачислениеПоБольничномуЛисту, Отпуск, БольничныйЛист, ВозвратНаРаботу(Организаций), УвольнениеИзОрганизаций, Увольнение.
   - `adapterPostDocument` sets `?exchange=true` for any of them (`:4713-4715`). I verified this myself.
   - So `employee_fire_from_1c` and payroll, leave and sick-leave `write_to_1c` documents go out in exchange mode.
   - The file's own comment says exchange mode means "posted=true with ZERO movements" (`:12675-12681`).
2. **The cloud can hard-delete, unpost, post, edit or create anything in 1C over the hub WebSocket.**
   - `delete_1c_document` with `hard:true` does a physical DELETE (`:5845`, `:5218`).
   - `unpost_1c_document` (`:5697`).
   - `create_ref` takes any catalog name and any extra fields, with no allow-list (`:7159-7254`).
   - `write_to_1c` with `resource_type:"prebuilt_doc"` passes through any doc type and body (`:9711`, `:9817`).
   - The workspace notes say the MCP grants exclude unpost and delete on purpose. The connector accepts them from any hub frame.
3. **The hub WebSocket carries no auth from the connector.**
   - Both sockets do `new WebSocket(url)` with no token: `onec-sync-provider.tsx:15275`, `onec-hub.tsx:107`.
   - The identity is self-declared in `hello`: `user_id`, `device_id`, `tenant_id:"default"`, `role:"admin"` (`:1495-1505`).
   - `onec-hub` sends a bare `{action:"hello", connectorId, userId, deviceId}` (`onec-hub.tsx:73-78`).
   - Whatever trust exists must live in backend/1c `app/api/connector_ws.py`. That needs confirming (open question 1).
4. **Two sockets to the same hub, with the same `connector_id`.**
   - `OnecHubProvider` (presence only, dashboard layout) and `OneCSyncProvider` (presence plus commands, root layout).
   - Both register as `${userId}:${deviceId}`, and the backend fans every command out to both.
   - Command handling was removed from `onec-hub` because `sync_now` used to run twice (comment at `onec-hub.tsx:160-191`).
5. **1C passwords and API tokens are kept in plaintext localStorage.**
   - `aiba-infobases` persists `infoBases` and `infoBasesForSync`, including `password` ("1C password for sync") (`stores/infobase.ts:51,99,130-135`; `types/infobase.ts:17`).
   - `aiba-auth` persists `accessToken` and `refreshToken` (`stores/auth.ts:14-35`).
   - The unisoft OData path also sends the base username and password to Rust on every call.
6. **Unhandled hub frames fall into a hidden, ungated second write path.**
   - When `dispatchControlCommand` returns false, the frame goes to `useOneCSyncStore.enqueueMessage` (`onec-sync-provider.tsx:15330-15331`; buffer of 500, `stores/onec-sync.ts:3`).
   - The only consumer is `/documents` (`documents/page.tsx:3112-3116`, `:4721-4755`). It deep-scans any frame for documents and writes them to 1C with raw `fetch` (`:2988-3009`), outside the gateway and the write-gate.
   - This only happens if that hidden page is open.
   - Frames that fall through include `push_to_1c`/`create_in_1c` whose `resource_type` is not departments or schedules, and `delete_in_1c` whose type is not departments.
   - Those frames do take the global write-gate first (`:14884-14900`), but release it before the page runs the write.
7. **Concurrent cloud writes share one `activeChart` variable.**
   - `let activeChart` is declared at `:4891` and set per command at `:11770`.
   - Frames are handled with no serialization (`void (async()=>…)()`, `:15300`).
   - Two `write_to_1c` commands for different bases can interleave, so one base's document gets built with the other base's chart of accounts.
8. **The bank fallback loop may create duplicates.**
   - `postBankDocWithFallbacks` (`:12459-12519`) wraps every typed-1C document.
   - On an `unresolved_reference` for СчетКонтрагента or СчетОрганизации it re-POSTs up to 4 times, without checking whether the first POST already returned an id.
   - It can also create our own bank account in 1C (`СоздатьЕслиНетоПусто`) with no user confirmation.
9. **Reconnecting a bank deletes its cloud history first.**
   - Hamkor, Xalq, Trust, Ipoteka and Ubank send `DELETE {BANK}/subscription/{id}` before login (`banks/page.tsx:1463-1475, 1540-1551, 1635-1646, 1685-1696, 1775-1786`). The code comment says it "cascades accounts/transactions" (`:2100`).
   - Clicking a «Boshqa qurilmada» (connected on another machine) row triggers the same thing (`:3017-3021`).
10. **NBU periodic auto-signing.**
    - `banks/payments/page.tsx:369-419` signs every `status_id:1` payment on a 30 min, 1 h, 5 h or 10 h timer with the first certificate found (`:241`). Nobody reviews the payments.
    - It is enabled per company through localStorage `periodic_signing_<cid>`, and switches itself back on when the page is opened.
11. **Event-log sync is hard-disabled even though its UI flag exists.**
    - `isEventLogSyncEnabled()` always returns `false` (`utils/dev-settings.ts:63-69`). The comment says `/changes` "is a recent-document-date query, not a modification feed".
    - So the whole eventlog lane is dead: `eventlog-provider.tsx` (gates at `:126`, `:298`), `eventlog-driver.ts` (1,250 lines), `utils/eventlog-arm.ts`, and the Rust `eventlog_*` commands.
    - The `EVENTLOG_SYNC` dev-setting does nothing.
12. **The docs disagree with the code.**
    - `connector/CLAUDE.md` says `MAX_LIMIT` is 250. The code has `MAX_LIMIT = 500` (`utils/incremental-fetcher.ts:32`), with adaptive page memory.
    - The comment says the write gate holds for 80s. The code has 90s (`utils/write-gate.ts:66`), which equals the backend's 90s post timeout.
    - The write lane deadline is 600s (`utils/adapter-gateway.ts:396`). So background sync restarts under any write that runs longer than 90s.

---

## 1. Startup and provider tree

**Root `app/layout.tsx`** (static Next export loaded into the Tauri webview):
- Providers in order: Theme → Query → Observability (Sentry) → Runtime (`invoke validate_runtime`, `providers/runtime.tsx:48`) → AdapterProvider → AppUpdateProvider → ModeSwitcherProvider → AuthProvider → `WebSocketWrapper id="ws" path="/connections/ws" autoConnect={false}` (`:45-51`) → children.
- Also mounted here: `RequiredUpdateModal`, `OneCSyncProvider` (`:11,43`) and `EventLogSyncProvider` (`:8`, a no-op because of the flag).

**Dashboard `app/(dashboard)/layout.tsx`:**
- `OnecHubProvider` (`:29`, `:93`).
- `useAibaTokenKeepalive` (`:71`).
- Seed provider, sidebar, info-base sync provider (polling).

**Rust side at startup** (`src-tauri/src/lib.rs:884+`), for context:
- `silent_cleanup`
- HKCU Run key auto-start (`ensure_startup_entry`)
- system tray
- devtools shortcut
- global shortcut Alt+B for the debug-logs window
- emits `com-admin-required`

**Legacy socket `WebSocketWrapper` / `websocket/providers/socket.tsx`: DEAD.**
- It has `autoConnect={false}` and nothing calls connect. The only consumer is `providers/seed.tsx:23`, through `useWS("ws")`.
- So these never fire: the `auth.connect` request (`seed.tsx:232-239`, which carries the access token), and the `bank.credentials` / `auth.logout` events (`:216-229`).

---

## 2. Auth, company and mode

| Feature | Evidence | Status |
|---|---|---|
| Login, legacy `/api/v1` | `utils/login.ts:75-90`: `POST /auth/login` (form data), then `GET /user/me` | ACTIVE (prod API is `https://api.aiba.group/api/v1`) |
| Login, aiba-next `/api/v2` | `utils/login.ts:38-60`: JSON `POST /auth/login`, then `GET /me`; no refresh token (12h) | FLAGGED: only when the API base contains `/api/v2` (`services/api.ts:42-52`) |
| API client | `services/api.ts`: axios, **10-minute timeout**, `Authorization: Bearer`, `X-Device-Id` from `invoke get_device_id` | ACTIVE |
| Token refresh | `services/api.ts:69-110, 125-150`: on a 401, one `POST {base}/auth/refresh {refresh_token}`, then retry; if that fails, `clearAuth` and go to `/login` | ACTIVE |
| Token keepalive | `lib/aiba-token-keepalive.ts`: pushes the token to the local signer `POST :7777/api/aab/auth-token` every 60s and on focus; `GET /user/me` every 10 min to force a refresh | ACTIVE |
| Profile reload | `providers/auth.tsx:151` `GET /user/me` | ACTIVE |
| `unauthorized` Tauri event | `providers/auth.tsx:212-222` listens and runs `localStorage.clear()`. **No Rust code emits `unauthorized`** (grep of src-tauri finds none) | DEAD listener |
| Companies | `utils/company.ts:51,73`: `GET /me/companies` (v2) or `GET /company/` (v1); persisted `aiba-companies` | ACTIVE |
| Dev/prod mode | `providers/mode-switcher.tsx`. **Ctrl+Shift+D toggles prod↔dev for every user** (`:63-73`). Mode is stored as `app_mode` (`core/config.ts:676`) | ACTIVE, hidden |
| Devtools | Ctrl+D, then Ctrl+I within 2s, runs `invoke("open_devtools")` (`mode-switcher.tsx:75-118`) | ACTIVE, hidden |

---

## 3. Adapter lifecycle (local oscript 1C adapter on :55899, 1uz adapter on :55900)

- **`providers/adapter.tsx`:**
  - `get_adapter_config_status` (`:116`).
  - Health check every 15s (`HEALTH_CHECK_INTERVAL`, `:28`, `:335`) to `GET {ADAPTER}/api/status`.
  - Restarts after `MAX_UNRESPONSIVE_MS` = 90s (`:110`) using `stop_adapter` → `prepare_adapter` → `start_adapter` (`:161-178`).
  - The 1uz adapter is handled the same way: `start_uz_adapter`, `stop_uz_adapter`, `uz_adapter_status` (`:423-467`).
- **Database config modal** (`components/adapter/database-config-modal.tsx`):
  - Commands: `discover_databases`, `test_adapter_connections` (progress event `adapter:test-progress`), `add_adapter_database`, `remove_adapter_database`, `get_all_adapter_database_configs`, `get_adapter_database_config`, `list_infobase_users`, `restart_adapter`.
  - **Hidden Ctrl+Shift+T** toggles "bulk mode" (`:405-417`).
- **Heal panel** (`components/adapter/heal-panel.tsx`): `heal_base` (`:228`), `restart_adapter` (`:287`).
  - The header comment mentions `scan_base_heal_status` (`:9`) but the code never invokes it.
- **Settings page** (`app/(dashboard)/settings/page.tsx`):
  - Pool config: `get_adapter_pool_config` (`:214`), `set_adapter_pool_config` (`:255`), polled every few seconds (`:244`).
  - Tunnel: `tunnel_get_status` / `tunnel_ensure_config`, with the `tunnel-status` event (`:38-50`).
  - Bank key manager: `get_bank_key_manager_enabled` / `set_bank_key_manager_enabled` (`:147, 162`).
  - **Hidden Alt+C runs `invoke("start_adapter")`** (`:386-406`).
- **Gateway** (`utils/adapter-gateway.ts`): every adapter call goes through one priority queue.
  - Lanes: `high`, `write`, `writePrep`, `urgent`, `normal`, `low`, `partition`.
  - Caps: `MAX_HIGH=1` (`:337`), `MAX_WRITE_PER_BASE=1` (`:344`), `MAX_WRITE_PREP=1` (`:362`), `MAX_PARTITION_PER_WORKER=1` (`:369`).
  - Deadlines: `WRITE_LANE_DEADLINE_MS=600s` (`:396`), `WRITE_PREP_DEADLINE_MS=120s` (`:402`).
  - **Raw-`fetch` bypasses exist:** the 1uz write path (`onec-sync-provider.tsx:11855`, `:12025`, no timeout), the documents page, `stock-sync.ts:83`, `reconciliation-sync.ts:44`, and the reports-lane health check.
- **Write gate** (`utils/write-gate.ts`): `beginWrite()` uses one GLOBAL bucket, so any cloud write parks every base's sync. Auto-release at 90s.

---

## 4. The WebSocket hub: command surface (`providers/onec-sync-provider.tsx`)

**Connection:**
- URL: `ws(s)://<oneCApiUrl host>/api/v2/connector/ws` (`:1134-1158`). Prod is `wss://1c.aiba.group/api/v2/connector/ws`.
- The fallback is `ONEC_SYNC_SOCKET_URL`, default `wss://onec.aiba.group`, with **no path** (`core/config.ts:192-195`).
- The `.env` used for local dev points at the LAN host `ws://192.168.0.102:8004/...`.
- Heartbeat every 30s (`NEXT_PUBLIC_ONEC_SYNC_HEARTBEAT_SECONDS`, minimum 10s, `:70-74`).
- A late tick is logged (`heartbeat_tick_late`, `:15206`). No inbound traffic for 3× the interval closes the socket (`:15237`).
- Reconnect backoff is `min(30s, 1s·2^n)` plus 0–600ms jitter (`:15140-15160`).
- The socket is closed on `pagehide`/`beforeunload` (`:15387-15395`).
- The adapter DB list is refreshed every 15s (`:15372`).
- The legacy localStorage key `aiba.connector.fallback-id` is deleted at startup (`:183`, `:229`). This was the "ghost connector" fix from 2026-09-29.

### 4.1 Incoming actions (dispatcher at `:14905-15138`; write-gated set at `:137-167`)

| Action(s) | Line | Handler | Gate | Status |
|---|---|---|---|---|
| `error` (unsupported_action) | 15302 | records it; falls back to heartbeat | – | RARE |
| `ping`, `server_ping` | 14910 | replies `server_pong` | – | ACTIVE |
| `get_bases` | 14920 | `sendBasesReply` :1778 | – | ACTIVE |
| `sync_now`, `pull_now` | 14925 | `runSyncNowCommand` :1896 | – | ACTIVE (cloud-triggered sync, targeted/full/cursor) |
| `check_exists` | 14930 | :4359 (department / schedule / employee / document modes) | – | ACTIVE |
| `delete_from_1c`, `delete_doc_from_1c` | 14935 | :5939 (only AIBA_(LEAVE\|PREMIUM\|DEDUCTION) markers) | W | ACTIVE, destructive |
| `employee_write_to_1c` | 14945 | :6173 (ФизЛицо + Сотрудник + hire document) | W | ACTIVE |
| `write_to_1c`, `write_doc_to_1c` | 14959 | `handleWriteTo1CCommand` :11718 | W | ACTIVE: the main write path |
| `post_1c_document`, `post_doc_in_1c` | 14972 | :5594, 75s deadline | W | ACTIVE |
| `unpost_1c_document`, `unpost_doc_in_1c` | 14980 | :5697 | W | ACTIVE, destructive |
| `update_1c_document`, `edit_1c_document` | 14985 | :5752 (PUT, `autoUnpost`) | W | ACTIVE |
| `delete_1c_document` | 14990 | :5834 (mark, or `hard`) | W | ACTIVE, destructive |
| `push_to_1c`, `create_in_1c` (departments / schedules) | 14995 | :14385 / :13743, **no existence check** | W | ACTIVE |
| `push_to_1c`, `create_in_1c` (other type) | falls through | → store queue → /documents | W, then released | PARTIAL / risky |
| `department_write_to_1c` | 15015 | :14385 | W | RARE alias |
| `list_resource` (schedules / departments / employees) | 15020 | :13802 / :13994 / :14087 | – | ACTIVE |
| `list_resource` (bases / refdata) | 15074 | second `list_resource` branch | – | ACTIVE (the duplicate `if` is reached because the first one falls through) |
| `holiday_push_to_1c` / `holiday_delete_in_1c` / `holiday_check_1c` | 15047-15057 | :14635 / :14724 / :14801 | W / W / – | ACTIVE |
| `department_delete_in_1c`, `delete_in_1c` (departments) | 15060 | :14310 | W | ACTIVE, destructive |
| `refdata_get`, `get_refdata`, `refdata/get` | 15090 | :7500 (banks, transaction_operation_type; 10-min cache) | – | ACTIVE. **The cached reply reuses the old `request_id`** (:7571-7574) |
| `get_list` | 15099 | :7256 (enum, calcTypes, catalog with `fields` projection) | – | ACTIVE |
| `create_ref` | 15104 | :7159 | W | ACTIVE |
| `catalog_update` | 15109 | :13180 (CAS `expected`) | W | ACTIVE |
| `document_read`, `catalog_read` | 15114, 15119 | :13378, :13489 (EDO draft import; `urgent` lane) | – | ACTIVE (added 2026-09-28) |
| `employee_fire_from_1c` | 15124 | :6846, **sent with exchange=true** | W | ACTIVE |
| `connector_status_request`, `status_request` | 15129 | forced `state_update` | – | ACTIVE |
| anything else | 15331 | `enqueueMessage` → /documents page | – | see risk 6 |

**`hello.capabilities`** advertises 19 commands. Not every routed alias is advertised: `sync_now`, `pull_now`, `get_list`, `create_ref` and `employee_fire_from_1c` are routed but not advertised.

**`onec-hub.tsx` incoming:** it handles only `need_hello`. It ignores `pull_now`, `read_table` and `sync_now` (`:185-191`).

### 4.2 Outgoing messages

| Action | Shape and line |
|---|---|
| `hello` | `sendPresence`, `:1660-1759`. Fields: `protocol_version:1`, identity, `bases[]{base_key, infobase_id, name, status, company_ids, mapped_info_base_ids}`, `capabilities`, `ready` |
| `heartbeat` | same, without capabilities |
| `state_update` | `:1660`, plus the store effect at `:15431`. **The two senders fingerprint differently** (`:1686` vs `:15417`), so their dedup never matches |
| `write_progress` | every 10s while a write runs, 20-min cap (`:122`, `:130`, `:1620-1658`). Stages: resolving / writing / reading / posting |
| `write_to_1c_progress` | `:11735` |
| `error` | `:1761` |
| `server_pong` | `:14911` |
| `get_bases_result` | |
| `list_resource_result` | |
| `sync_now_ack`, `pull_now_ack`, `sync_now_result`, `pull_now_result` | `:2058`, `:1807` |
| `route_not_found` | `:4376` |
| `check_exists_result` | |
| `post_1c_document_result` | |
| `unpost_1c_document_result`, `update_1c_document_result`, `delete_1c_document_result` | |
| `delete_from_1c_result` | |
| `employee_write_to_1c_result` | **`success:true` even when the hire document failed** (`:6814-6832`) |
| `employee_fire_from_1c_result` | |
| `create_ref_result` | |
| `get_list_result` | |
| `refdata_response` | |
| `write_to_1c_result` | `:11748`…`:12826` |
| `catalog_update_result` | |
| `document_read_result`, `catalog_read_result` | no `status`/`success` keys |
| `push_to_1c_result` | also used for `department_write_to_1c` |
| `delete_in_1c_result` | |
| `holiday_*_result` | |

- `onec-hub` sends: `hello {action, connectorId, userId, deviceId}` and `heartbeat {action, connectorId}` every 30s, with a 3s reconnect.
- The `/documents` page sends: `write_to_1c_result` (`page.tsx:4134`) and `check_exists_result` (`:4193`).

### 4.3 How a write works (`write_to_1c`)

1. Read the chart of accounts: `GET /api/db/{db}/charts/Хозрасчетный?limit=5000`, write lane, 120s.
2. Pre-flight: if the adapter is not `active`, call `requestReconnect` and wait up to 8s. Otherwise fail with `adapter_offline`.
3. Read the base version from `/metadata`.
4. 1uz bases: raw POST to `:55900/.../documents/{BankAuto|EsfAuto|Operation}`.
5. 1C bases:
   - `learnDepartment` (majority vote over ПоступлениеТоваровУслуг).
   - Pick a builder (prebuilt passthrough / accrual / leave / timesheet / payroll / premium / deduction / bank / trade).
   - Probe `schema/documents/{type}`.
   - `POST /api/db/{db}/documents/{type}[?post=false][&exchange=true]`.
   - Then the optional СчетФактура, using `POST /api/db/{db}/query` with 1C query text built in the connector.
6. The idempotency marker `Комментарий` is passed through, along with `_idempotencyMarker` and `onec_number`.
7. Multi-org `pinOrganization` covers trade, bank, premium, deduction, hire and fire. **It does not pin timesheets or the payroll/leave passthroughs.**
8. Hardcoded NAS fallback accounts: 6310/0910, 6410/4410.4, 5110/6010/4010/4310 and others.

**Dead in this area:**
- `handleEmployeeWriteCommand` (`:12851`) and `buildEmployeeWriteDocument` (`:11038`) have no caller. They carry tenant-specific defaults: "IMKON", '"Bus Biznes Nur" Mchj', a named manager, salary defaults of 5,000,000 / 1,000,000.
- The hire path makes a GET whose result is thrown away, to the wrong route (`:6229-6238`).

**Caches that also store failures** (until restart):
- `dbConfigurationVersionCache` (`:4950`, stores `""` on error), so the base is treated as "not 1.3".
- `docSchemaAvailabilityCache` (`:4988`).
- `departmentByBase` (`:5277`).

---

## 5. Sync engine (scheduled 1C → cloud)

- **Poller.** `features/accounting/providers/info-base-sync-provider.tsx` builds a `SyncPoller` (`:330`) and starts it with `getSyncIntervalMs(POLLING_INTERVAL=60_000)` (`:112`, `:1002`).
  - The dev-setting `SYNC_INTERVAL_MIN` is clamped to 1–120 minutes.
  - The poller (`lib/sync/sync-poller.ts`) is a `setTimeout` chain that handles **one base per tick**: 150ms gap, 5s when idle, adapter status cached 2s.
  - After 3 failures a base is quarantined for 5 minutes (`:127-137`).
  - It skips bases while `isWritePending`.
- **Manual sync.** "Sync all" is bound to **Ctrl+Shift+S** (`info-base-sync-provider.tsx:845-853`). There are also per-row UI actions on the accounting page and the cloud `sync_now`/`pull_now`.
- **Orchestrator** (`lib/sync/sync-orchestrator.ts`), via `syncSingleInfoBase(..., {mode: full|cursor, onlyTables, urgent})`:
  - **Table list:** `GET {onec v1}/onec/{id}/sync-tables` (`utils/sync-tables-config-fetcher.ts:46,81`), merged with `TABLES_BY_PROVIDER`, `MANDATORY_MAIN_TABLES_BY_PROVIDER` (venkon GTD refs + ТорговыеТочки) and `DEFAULT_REPORT_TABLES` (`core/config.ts`).
  - **Order:** by `tableSyncPriority` (chart → catalogs → documents → giant AccountingRegister last).
  - **Watchdog:** a run with no progress for 10 minutes is aborted (`SYNC_STALL_TIMEOUT_MS`, polled every 30s, `:238-289`).
  - **Failures:** a table that fails after its retries is skipped, not fatal.
  - **Steps:**
    - `metadata-preload`
    - `probe-scan` (projection hash)
    - `change-detect` / hash diff against backend refhashes
    - `resume-offset`
    - `source-capability` (localStorage `sync_source_capability:<base>:<table>`)
    - stock snapshot
    - 1uz reconciliation ledger
    - register reconcile for documents
    - data-coverage report
- **Reading from the adapter** (`utils/incremental-fetcher.ts`):
  - `MAX_LIMIT=500` (`:32`); `PAGE_TIMEOUT_MS=120s` (`:67`).
  - On timeout the page size is halved after a 60s wait (`:113`, `:756-764`).
  - Page-size memory is kept in localStorage `adapter_page_limits` (`:178`).
  - A row that times out twice even alone is recorded in `adapter_poison_rows` (`:268-303`) and skipped.
  - Optional bulk XDTO parse via `invoke("parse_bulk_page")` (`:701`), gated by `BULK_REGISTERS`.
  - Optional parallel cold read: `start_partition_workers` / `stop_partition_workers` (`:1839-1940`), gated by `COLD_READ_WORKERS` 2–8, registers only.
  - Adapter routes used: `/api/db/{db}/{path}`, `/batch`, `/{id}`, `/api/status`.
- **Upload** (`lib/sync/chunk-uploader.ts`):
  - `POST {onecApi}/entity/upload` as multipart (`oneCId`, `file`) with a Bearer token.
  - Optional gzip via `CompressionStream` (`GZIP_UPLOAD`).
  - Up to 5 network retries with offline wait; 413 splits the chunk.
  - Per-attempt timeout is about 5 minutes.
  - localStorage `sync.debug.fullPayload` dumps the whole payload to the console (`:147`).
- **Backend reconciliation fetchers:**
  - `GET /onec/{id}/counts` (`utils/backend-counts-fetcher.ts:32`).
  - `GET /refids?table_name=` (`backend-refids-fetcher.ts:28`).
  - `GET /refhashes` (`:60, :152, :189`).
  - `POST /reconcile-recorder` (`:97`).
  - **`POST /prune-missing`** (`:235`): deletes cloud rows the 1C base no longer has.
- **Other lanes:**
  - Stock: `GET adapter /api/db/{db}/stock?accounts=…`, then `POST {api}/entity/stock-snapshot` (`stock-sync.ts:67-105`). ACTIVE, called from orchestrator `:829`.
  - Coverage: `POST {api}/entity/data-coverage` (`coverage-sync.ts:53`). ACTIVE.
  - 1uz reconciliation ledger: `GET :55900/.../reconciliation/ledger`, then `POST {api}/reconciliation/ledger/upload` (`reconciliation-sync.ts:42-56`). ACTIVE for 1uz only.
  - Reports lane (`reports-lane-driver.ts`, started at `info-base-sync-provider.tsx:1024`):
    - `invoke("start_reports_worker")` on port :55949 (adapter port + 50).
    - `GET /api/health`.
    - Turnover push: reads АккумуляцияРегистр РозничнаяВыручка, then `POST {onecApi}/reports/retail-rollup/ingest` (`turnover-push.ts:20, 141`).
    - Windows are 14 days, with a 400-day one-time backfill.
    - ACTIVE. `stop_reports_worker` is never invoked.
  - Eventlog lane: DEAD (see 0.11).
  - `targeted-refresh.ts`: no importer, **DEAD**.
- **Presence of connections:**
  - `providers/connection-status.tsx` sends `PATCH {api}/onec/connection/{oneCId} {status, totalCount}` every 60s (`:17`, `:126`, `:345`), with a late-tick warning.
  - On the Rust `app-quit-requested` event it marks connections inactive, then calls `invoke("exit_app")` (`:335`, `:486`).
- **Org bindings (multi-org):** `GET` / `PUT {api}/onec/{connectionId}/org-bindings` (`features/accounting/api/org-binding.service.ts:137-169`) and `org-mapping-modal.tsx`. ACTIVE.
- **Infobase list:**
  - `GET {onec}/onec?page&size&companyId` (`utils/info-base.ts:82-90`).
  - The 1uz list comes from a separate backend (`:110-140`).
- **Cleanup:**
  - "Delete entities" does `DELETE {onec}/entity/oneC/{id}`, then polls `GET …/delete-status` (`features/accounting/api/info-base.service.ts:35-96`; caller `app/(dashboard)/accounting/page.tsx:217`).
  - This is a destructive purge of cloud data, reachable from the row menu (`components/accounting/table/column.tsx:443`).
- **Export:** `export-data-service.ts` reads the adapter and writes a local file with `invoke("write_file")` (`:159`).
- **Startup warning:** the `com-admin-required` event shows a persistent toast (`info-base-sync-provider.tsx:810-830`).

---

## 6. Banks, keys, payments, documents, unisoft

(from the banks/documents/unisoft sub-audit)

**Banks.** `/banks` is linked from the sidebar ("Banklar", `layouts/sidebar.tsx:75-78`). Its tabs are Ulanishlar, To'lovlar (NBU only) and Kalitlar.
- 11 connector banks talk to the local signer sidecar at `http://127.0.0.1:7777` (overridable by localStorage `SIGNER_API_URL`). The signer stream is `ws://127.0.0.1:7777/api/stream`.
- Each bank has `/api/<bank>/{status, login, logout, sync, …}`:
  - Turon
  - AAB
  - Mikrokreditbank (a near-copy of the AAB page)
  - Ipoteka
  - Hamkor
  - Xalq
  - Trust
  - Kapital
  - Ubank
  - OFB (RARE)
  - SQB (OTP; proxied to a :7778 sidecar)
- Cloud bank calls:
  - `GET {BANK}/banks/list`
  - `GET /subscription/?company_id`
  - `POST /subscription/`: the generic path, which **sends the bank password to the cloud** (`page.tsx:1909-1920`)
  - `DELETE /subscription/{id}`
  - `POST {bank}/api/internal/connector-sync/init` with the user's JWT, against an "internal" route (`:1489`, `:1805`)
- Polls: healthz 3s, keys 5s, `get_pkcs11_status` 15s, bank status 5s, accounts/transactions 2 min (3s while backfilling), plus per-bank auto-sync cadence `sync-interval-v1:<bank>:<cid>`, pushed to the sidecar at `/api/sync-interval`.
- DEAD hooks:
  - `useIpotekaCreatePayment` (`connector-sync/create-payment`)
  - `useSqbCreatePayment`, `useSqbPaymentHistory`, `useSqbPurge`
  - `useTuronPurge`, `useUbankPurge`
  - `useOfbDelete` and friends
  - `subForBank`, `reconnectableRow`
  - the Mikrokreditbank `pushAibaToken`
- A stale `page.tsx.tmp.35312.*` file sits in the route folder.
- Kapital's bank JWT is exposed in the webview (`kapital/page.tsx:309`), used only for an expiry badge.

**Keys** (`/banks/keys`):
- Invokes: `activate_chip`, `activate_chip_via_vendor`, `cancel_chip_activation`, `restart_signer`, `toggle_debug_window`, `list_my_certs`, `pair_chip_cert`.
- Event: `chip-activation-progress`.
- HTTP: `/api/keys/mark-pinless` and `/api/keys/unmark-pinless`.
- Certificates containing "fiddler" are hidden (`:174-178`).

**NBU payments:**
- `POST {BANK}/nbu/payment-list` and `/nbu/payment-sign`.
- Signing through Styx WS `ws://localhost:8181/` with `signMSG` (`services/epass.ts:37`).
- Periodic auto-sign: see 0.10.

**ePass:** `useEpasses` calls `GET :6210/crypto/getCertInfo` on every start (`providers/seed.tsx:52,121`). **Nothing reads the result.**

**Documents** (`/documents`): hidden from the sidebar (`sidebar.tsx:80-81`), reachable only by URL. It is a Didox → 1C push for invoices and acts.
- Reads `GET {DIDOX}/api/v1/provodka/?…doctype=002,005…`.
- Reads the adapter: schema, enums, catalogs, chart, registers, and a fuzzy existence check.
- **Writes with raw `fetch`:** `POST adapter …/documents/{РеализацияТоваровУслуг|ПоступлениеТоваровУслуг}`. On a "credit account missing" error it retries once with hardcoded accounts.
- `Комментарий` gets the provodka text instead of the `AIBA_<KIND>_<id>` marker.
- Status: manual button RARE; socket queue PARTIAL (only for fall-through frames, see 0.6).

**Unisoft** (`onec/services/unisoft.ts`, `onec/utils/company.ts`, `fetcher.ts`): "unisoft" is a provider label for a plain 1C base, not a separate product.
- It builds a KPI dashboard from **1C OData** through `invoke("onec_get"|…|"onec_delete")`, using `LOCAL_ONEC_REGEX_URL=http://localhost/$1/odata/standard.odata`.
- Concurrency is `pLimit(3)`; up to 3 attempts with backoff.
- Only caller: `/accounting/info-base`, which nothing links to. **UNUSED.**

---

## 7. Complete lists

### 7.1 Tauri `invoke()` commands: registered in Rust vs called from TS

Registry: `src-tauri/src/lib.rs:805-883`.

| Command | TS caller(s) | Status |
|---|---|---|
| onec_get / onec_post / onec_put / onec_patch / onec_delete | onec/utils/fetcher.ts:123,165,183,201,219 | UNUSED (unisoft only) |
| open_devtools | providers/mode-switcher.tsx:110 | hidden hotkey |
| get_device_id | utils/device-id.ts:11; observability.tsx:43; onec-sync-provider.tsx:244 | ACTIVE |
| proxy | utils/tauri-proxy.ts:29 | check callers (tauri-proxy) |
| write_file | export-data-service.ts:159 | ACTIVE (export) |
| prepare_adapter | adapter.tsx:175; database-config-modal.tsx:460 | ACTIVE |
| start_adapter | adapter.tsx:178; settings/page.tsx:396 (Alt+C); database-config-modal.tsx:461 | ACTIVE |
| stop_adapter | adapter.tsx:166; update.tsx:226; database-config-modal.tsx:458 | ACTIVE |
| restart_adapter | database-config-modal.tsx:523; heal-panel.tsx:287; use-info-base-form.ts:114 | ACTIVE |
| get_adapter_pool_config / set_adapter_pool_config | settings/page.tsx:214 / :255 | ACTIVE |
| start_partition_workers / stop_partition_workers | incremental-fetcher.ts:1839 / 1849, 1853, 1940 | FLAGGED (COLD_READ_WORKERS) |
| start_reports_worker | info-base-sync-provider.tsx:390 | ACTIVE |
| adapter_status | adapter.tsx:161,259 | ACTIVE |
| start_uz_adapter / stop_uz_adapter / uz_adapter_status | adapter.tsx:423,443,453; uz-modal:77; use-info-base-form.ts:236,521; form.tsx:240 | ACTIVE (1uz) |
| set_uz_sql_auth | use-info-base-form.ts:223,490,501 | ACTIVE (1uz) |
| get_adapter_database_config | database-config-modal.tsx:434; form.tsx:266; stores/adapter.ts:44 | ACTIVE |
| get_all_adapter_database_configs | database-config-modal.tsx:344,585,654 | ACTIVE |
| list_infobase_users | database-config-modal.tsx:905 | ACTIVE |
| heal_base | heal-panel.tsx:228 | RARE |
| validate_runtime | providers/runtime.tsx:48 | ACTIVE |
| discover_databases / add_adapter_database / test_adapter_connections / remove_adapter_database | database-config-modal.tsx:273,706/821,233,859; use-info-base-form.ts:148 | ACTIVE |
| get_adapter_config_status | adapter.tsx:116 | ACTIVE |
| get_logs / clear_logs / export_logs / ui_log | debug-logs hooks :27,:38; page :36; utils/ui-log.ts:37 | ACTIVE (debug-logs page, Alt+B) |
| toggle_debug_window | banks/keys/page.tsx:404 | ACTIVE |
| get_pkcs11_status | lib/signer-api.ts:95 | ACTIVE |
| restart_signer | keys/page.tsx:388 | ACTIVE |
| prepare_for_update | update.tsx:237 | ACTIVE |
| get/set_bank_key_manager_enabled | settings :147,:162; bank-key-manager.tsx:25 | ACTIVE |
| activate_chip, activate_chip_via_vendor, cancel_chip_activation, list_my_certs, pair_chip_cert | keys/page.tsx:238,239,289,959,984 | ACTIVE |
| tunnel_get_status / tunnel_ensure_config | settings :38,:50 | ACTIVE |
| exit_app | connection-status.tsx:335 | ACTIVE |
| parse_bulk_page | incremental-fetcher.ts:701 | FLAGGED (BULK_REGISTERS) |
| eventlog_start / eventlog_ack / eventlog_status | eventlog-arm.ts:91 (via constant); eventlog-driver.ts:458; eventlog-provider.tsx:226,342 | DEAD (flag always false) |
| **Never called from TS:** list_com_connector_versions, set_com_connector_version, parse_valuetable_xml, set_backend_locale, check_network_status, install_adapter, ensure_adapter_firewall_rule, stop_reports_worker, set_adapter_kind, scan_base_heal_status, set_adapter_port, get_runtime_environment_status, get_log_file_path, get_signer_health, get_sqb_health, restart_sqb_sidecar, list_readers, **usb_test_disable_one**, tunnel_get_config, tunnel_set_config, **open_kapital_auth_window**, **sync_kapital_via_webview** | – | UNUSED from the UI (Rust-only or leftovers) |

### 7.2 Tauri events

| Direction | Event | Where |
|---|---|---|
| Listened | `log-entry` | debug-logs |
| Listened | `adapter:test-progress` | DB config modal |
| Listened | `tunnel-status` | settings |
| Listened | `bank-key-manager-status` | settings, bank-key-manager |
| Listened | `chip-activation-progress` | keys |
| Listened | `com-admin-required` | info-base-sync-provider.tsx:819 |
| Listened | `app-quit-requested` | connection-status.tsx:486 |
| Listened | `eventlog:changed` | dead |
| Listened | `unauthorized` | auth.tsx:212. **No emitter**: DEAD |
| Emitted by Rust, not listened to | `signer-status`, `sqb-status` | the TS code uses HTTP polling instead |

### 7.3 Backend URLs

Bases come from `core/config.ts` and `.env.production`:
- API: `https://api.aiba.group/api/v1`; dev `https://api-dev.aiba.uz/api/v1`
- 1C: `https://1c.aiba.group/api/v2`; dev `https://aiba-1c-dev.aiba.uz/api/v2`
- 1uz: `https://1uz.aiba.group/api/v2`; dev `https://1uz-dev.aiba.uz/api/v2`
- Bank: `https://bank.aiba.group/api/v2`; dev `https://aiba-bank-dev.aiba.uz/api/v2`
- Didox: `https://didox.aiba.uz` (prod uses a `.uz` host); dev `https://didox-dev.aiba.uz`
- Relay: `https://relay.aiba.uz`, for both modes

| Method | Path | Caller |
|---|---|---|
| POST | /auth/login; /auth/refresh | utils/login.ts; services/api.ts:134 |
| GET | /user/me, /me, /me/companies, /company/ | login.ts, auth.tsx:151, company.ts, token-keepalive |
| GET | {onec}/onec?page&size&companyId | utils/info-base.ts:82 |
| PATCH | {onec}/onec/connection/{id} | connection-status.tsx:126 |
| GET | {onec v1}/onec/{id}/sync-tables | sync-tables-config-fetcher.ts:46,81 |
| GET / PUT | {onec}/onec/{connId}/org-bindings | org-binding.service.ts:137,168 |
| GET | {onec}/onec/{id}/counts | backend-counts-fetcher.ts:32 |
| GET | …/{id}/refids, /refhashes | backend-refids-fetcher.ts:28,60,152,189 |
| POST | …/{id}/reconcile-recorder; …/{id}/prune-missing | backend-refids-fetcher.ts:97,235 |
| POST | {onec}/entity/upload | chunk-uploader.ts:229 |
| POST | {onec}/entity/stock-snapshot; /entity/data-coverage | stock-sync.ts:105; coverage-sync.ts:53 |
| POST | {onec}/reconciliation/ledger/upload | reconciliation-sync.ts:56 |
| POST | {onec}/reports/retail-rollup/ingest | turnover-push.ts:141 |
| DELETE / GET | {onec}/entity/oneC/{id}; …/delete-status | info-base.service.ts:44,72 |
| WS | ws(s)://{onec host}/api/v2/connector/ws | onec-sync-provider.tsx:1144; onec-hub.tsx:47 |
| WS | {API_SOCKET}/connections/ws | websocket wrapper (never connects) |
| GET | {BANK}/banks/list; /subscription/?company_id | utils/bank.ts:33,72 |
| POST / DELETE | {BANK}/subscription/; /subscription/{id} | banks/page.tsx:1913; 1207…2147 |
| POST | {BANK}/api/internal/connector-sync/init | banks/page.tsx:1489,1805 |
| POST | {BANK}/api/internal/connector-sync/create-payment | ipoteka-api.ts:180 (DEAD) |
| POST | {BANK}/nbu/payment-list, /nbu/payment-sign | services/payment.ts:23,32 |
| GET | {DIDOX}/api/v1/provodka/?… | documents/page.tsx:3170-3200 |

**Local services:**
- Adapter `:55899`. Routes: `/api/status`, `/api/databases/status`, `/api/db/{db}/{catalogs,documents,registers/*,charts,enums,calcTypes,metadata,schema/documents,query,stock,batch,…}`, plus `/post`, `/unpost`, PUT and DELETE (with `hard`).
- 1uz `:55900`: `/api/status`, `/api/db/{db}/…`, `/reconciliation/ledger`.
- Reports worker `:55949`: `/api/health`, `/api/db/{db}/registers/accumulation/…`.
- Signer `:7777`; SQB `:7778` (proxied).
- Styx `ws://localhost:8181`; ePass `:6210`.
- OData `http://localhost/$1/odata/standard.odata` (unused).

### 7.4 Browser storage keys (localStorage; no IndexedDB use found)

| Key | Where | Content |
|---|---|---|
| `aiba-auth` | stores/auth.ts:32 | user, **accessToken, refreshToken** |
| `aiba-companies` | stores/company.ts:37 | companies, selection |
| `aiba-infobases` | stores/infobase.ts:130 | infobases **including 1C passwords** |
| `aiba-connector-store` | stores/app.ts:84; also read by sentry.ts:50 | mode, apiUrl, oneCApiUrl, deviceId, … |
| `app_mode` | core/config.ts:676 | dev/prod |
| `dev_settings` | utils/dev-settings.ts:3 | URL overrides and flags (7.6) |
| `adapter_page_limits`, `adapter_poison_rows` | incremental-fetcher.ts:178,275 | adaptive paging memory |
| `table_metadata:{user}:{mode}:{ib}:…` | utils/table-metadata-storage.ts:481 | per-table sync metadata and cursors |
| infobase sync state (prefix in infobase-sync-storage.ts) | utils/infobase-sync-storage.ts:31-100 | sync progress |
| metadata-preload cache | lib/sync/metadata-preload.ts:39,50 | |
| `sync_source_capability:{base}:{table}` | lib/sync/source-capability.ts:22 | |
| `sync.debug.fullPayload` | chunk-uploader.ts:147 | debug flag |
| `aiba.connector.fallback-id` | onec-sync-provider.tsx:183 | removed at startup |
| `SIGNER_API_URL`, `SQB_API_URL` | lib/*-api.ts | **redirect bank credential traffic** |
| `sync-interval-v1:{bank}:{cid}` | lib/sync-interval.ts:28 | |
| `turon-filters-v1:{scope}` | lib/turon-filters.ts:60 | |
| `bank:local-drop:{cid}`, `ipoteka:added:{cid}`, `periodic_signing_{cid}` | banks pages | |

`localStorage.clear()` runs in `providers/auth.tsx:222` (the dead `unauthorized` listener).

### 7.5 Timers

| Period | What | Where |
|---|---|---|
| 30s (env) | hub heartbeat + staleness check | onec-sync-provider.tsx:15202 |
| 1–30s backoff | hub reconnect | :15153 |
| 15s | adapter DB refresh | :15372 |
| 10s | write_progress (20-min cap) | :1633 |
| 75s | post deadline | :5599 |
| 30s / 3s | onec-hub heartbeat / reconnect | onec-hub.tsx:127,97 |
| 15s | adapter health; restart after 90s unresponsive | providers/adapter.tsx:28,335,467 |
| 60s | connection PATCH heartbeat | connection-status.tsx:17,345 |
| 30 min / 15 min | updater check / download retry | providers/update.tsx:16-17,269 |
| 60s / 10 min | token push to signer / `/user/me` | lib/aiba-token-keepalive.ts:45,51 |
| 60s × interval setting, per-base `setTimeout` chain | sync poller | sync-poller.ts:173 |
| 30s poll, 10-min stall | sync watchdog | sync-orchestrator.ts:238-289 |
| 120s page; 60s retry delay | page reads | incremental-fetcher.ts:67,113 |
| about 5 min per attempt | upload | chunk-uploader.ts |
| few seconds | settings pool status | settings/page.tsx:244 |
| while open | DB config modal poll | database-config-modal.tsx:399 |
| see section 6 | bank pages | ipoteka:234, kapital:132/810, payments:374, turon:148, ubank:86 |

### 7.6 Env vars and dev settings

Env vars (all `NEXT_PUBLIC_*`, `process.env`):
- `API_URL`, `API_SOCKET`, `API_DEV_URL`, `API_DEV_SOCKET`
- `1C_API`, `1C_API_DEV`, `1UZ_API`, `1UZ_API_DEV`
- `ADAPTER_API_URL`, `ADAPTER_API_DEV_URL`, `1UZ_ADAPTER_API_URL`, `REPORTS_ADAPTER_API_URL`
- `ONEC_SYNC_SOCKET_URL`, `ONEC_SYNC_HEARTBEAT_SECONDS`, `ONEC_SYNC_ADAPTER_REFRESH_SECONDS`
- `BANK_API_URL`, `BANK_API_DEV_URL`, `DIDOX_API_URL`, `DIDOX_API_DEV_URL`
- `EPASS_API_URL`, `SIGNER_API_URL`, `SQB_API_URL`
- `TUNNEL_BACKEND`, `TUNNEL_BACKEND_DEV`, `LOCAL_ONEC_REGEX_URL`
- `BULK_REGISTERS`, `GZIP_UPLOAD`
- `RETAIL_TURNOVER_WINDOW_DAYS`, `RETAIL_TURNOVER_BACKFILL_DAYS`
- `SENTRY_DSN`, `SENTRY_ENV`, `APP_VERSION`
- plus `NODE_ENV`

**Dev-settings overrides** (localStorage `dev_settings`, `utils/dev-settings.ts:5-61`). These override env for every URL plus:
- `SYNC_INTERVAL_MIN`
- `BULK_REGISTERS` (off unless "1")
- `GZIP_UPLOAD` (off unless "1")
- `COLD_READ_WORKERS` (1 unless 2–8)
- `EVENTLOG_SYNC` (**ignored**)
- the turnover windows

The modal opens with **Ctrl+Shift+B** or the URL parameter **`?dev=true`** (`providers/dev-settings-provider.tsx:30-45`).

### 7.7 Hotkeys and hidden tools

| Key | What it does | Where |
|---|---|---|
| Ctrl+Shift+D | prod↔dev mode toggle | mode-switcher.tsx:63 |
| Ctrl+D, then Ctrl+I | open devtools | mode-switcher.tsx:75-118 |
| Ctrl+Shift+B, or `?dev=true` | dev settings modal | dev-settings-provider.tsx:30 |
| Ctrl+Shift+S | sync all companies | info-base-sync-provider.tsx:845 |
| Alt+C (settings page) | start adapter | settings/page.tsx:386 |
| Ctrl+Shift+T (DB config modal) | bulk mode | database-config-modal.tsx:405 |
| Ctrl+Shift+X | theme | providers/theme.tsx:8 |
| Alt+B (Rust global) | debug-logs window | lib.rs:935 |

The debug-logs page is `app/debug-logs/*` (get, clear and export logs, live `log-entry` stream).

### 7.8 Observability

`lib/observability/sentry.ts`:
- `sendDefaultPii:false`; `tracesSampleRate` 1.0 in dev, 0.1 otherwise.
- `beforeSend` scrubbing.
- `setUser` is called with an id (`:201-204`).
- The DSN comes from env (redacted here).
- Write results are logged through `logWrite` in `maybeLogWriteResult` (`onec-sync-provider.tsx:460-505`). The documents page uses `captureWriteFailure`.

---

## Hidden / surprising findings

1. HR documents still go out with `exchange=true` (`onec-sync-provider.tsx:4688-4715`), against the hard rule. This can produce posted documents with no movements.
2. The cloud can drive `delete_1c_document hard:true`, `unpost`, generic `create_ref` and `prebuilt_doc` writes over an unauthenticated, self-identified WebSocket (`role:"admin"`, `tenant_id:"default"`).
3. There is a hidden second 1C write path: unhandled hub frames go to the store queue and then to the `/documents` page, which writes with raw fetch, no write-gate and a non-canonical `Комментарий`.
4. Two sockets share one `connector_id`. Only one may handle commands, enforced by a comment (`onec-hub.tsx:160-191`). A third, legacy socket (`/connections/ws`) never connects, so its `auth.connect` / `auth.logout` are dead.
5. Plaintext secrets in localStorage: 1C passwords (`aiba-infobases`) and access/refresh tokens (`aiba-auth`). `SIGNER_API_URL` / `SQB_API_URL` keys can redirect bank logins.
6. Event-log near-real-time sync is fully built (about 1,700 lines plus a Rust tailer) and permanently disabled in code (`dev-settings.ts:63-69`). The settings toggle is cosmetic.
7. 22 registered Rust commands are never called from the UI, including `usb_test_disable_one`, `open_kapital_auth_window`, `sync_kapital_via_webview`, `install_adapter`, `ensure_adapter_firewall_rule` and `set_adapter_kind`.
8. The Rust event `unauthorized` has no emitter. The listener that would `localStorage.clear()` is dead.
9. Bank reconnect does a cloud `DELETE /subscription/{id}` first, which cascades the history, including when reconnecting from a second PC. NBU payments auto-sign on a timer with no review.
10. A Ctrl+Shift+D global hotkey lets any user flip a production connector onto dev backends (all URLs switch).
11. Doc drift: `MAX_LIMIT` is 500, not 250; the write gate is 90s, not 80s; and the gate (90s) is shorter than the write-lane deadline (600s).
12. The `refdata_get` cache replays a stale `request_id`. The two `state_update` fingerprints never match. Hire reports `success:true` when the hire document failed. Version, schema and department caches store failures until restart.
13. The local `apps/app/.env` / `.env.production` hold MinIO access keys (`NEXT_PUBLIC_MINIO_*`, value redacted). They are git-ignored, not referenced in `src`, and not present in `out/`, so they are not shipped. They are still a plaintext secret on the dev machine.
14. Unisoft "KPI dashboard over OData" (2.5k lines) and the `/accounting/info-base` page are orphaned. `targeted-refresh.ts` has no importer.
15. Prod Didox uses `didox.aiba.uz` (a dev-style domain) in `.env.production`.

## Open questions needing deeper investigation

1. Does `backend/1c app/api/connector_ws.py` authenticate the connector socket at all (token, origin, IP)? If not, anyone who knows a `user_id:device_id` can impersonate a connector, receive write commands, or spoof presence.
2. Do backend/1c or kansler still emit `employee_fire_from_1c` and payroll, leave or sick-leave `write_to_1c` today? If so, exchange-mode documents with no movements are being created in production.
3. Which cloud senders, if any, emit `push_to_1c` / `create_in_1c` / `delete_in_1c` with a `resource_type` other than departments or schedules? Those land on the `/documents` legacy writer.
4. When `main.os` returns `unresolved_reference` for a bank account, does it still create the document? If yes, `postBankDocWithFallbacks` makes duplicates.
5. What does `POST /onec/{id}/prune-missing` delete on the backend, and can a partial or truncated adapter read trigger a mass prune?
6. Does the bank backend's `DELETE /subscription/{id}` really cascade accounts and transactions?
7. Is `/documents` (and its about 1,500-line socket queue) safe to remove, now that the provider owns `write_to_1c`?
8. Are the uncalled Rust commands (`usb_test_disable_one`, the kapital webview, the firewall rule, `install_adapter`) reachable another way (tray menu, CLI args), or are they dead?
9. Is the multi-org gap real? Timesheets and the payroll/leave passthroughs are not org-pinned, which matters on 19-org bases such as MIRL.
10. The app-shell and sync-engine sub-audits did not deliver. The updater flow (`providers/update.tsx`: mandatory-update policy source), the `proxy` command's callers and use, and the full sync-orchestrator branch list were only spot-checked. They need one more pass.
