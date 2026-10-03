# Old AIBA Connector: hidden and forgotten features

Only items that are **not** already written down in this repo's docs (`AGENT.md`,
`DECISIONS.md`, `MIGRATION_*.md`, `SYNC_*.md`, `IPC_CONTRACT.md`,
`BACKEND_SECURITY_FINDINGS.md`, and the earlier `SYNC_ARCHITECTURE_AUDIT.md`) and not visible
from normal use of the old app. Already-documented items (dead event-log feed, LAN-exposed
adapter with firewall rule, two sockets without a token, `/changes` not being a feed,
unauthenticated `rebuild`, `sql-dump`, Ctrl+Shift+S, D38's write quirks) are left out on
purpose.

Prefixes: `connector:` = `D:\aiba\connector\apps\app\src\`, `tauri:` =
`…\src-tauri\src\`, `main.os:` = the adapter, `router:` = `connector/adapter-router/src/main.rs`,
`b1c:` = backend/1c `origin/development` @ `b2e3590`. "Verified" = re-opened by the final
reviewer; "reported" = from one investigator, not re-opened.

## A. Remote powers nobody lists

### A1. Any internet client can send write commands to customers' 1C through the hub (verified)
The hub socket `/api/v2/connector/ws` decides "connector or cloud client" by the action name
of each message, not by a credential (`b1c:app/api/connector_ws.py:119-130`). The cloud action
set includes `write_to_1c`, `employee_write_to_1c`, `employee_fire_from_1c`, `delete_from_1c`,
`delete_in_1c`, `push_to_1c`, `holiday_*`, `create_ref`, `get_list`
(`b1c:app/api/cloud_router_ws.py:50-68`). The earlier audit noted the connector sends no
token; it did not note that the **same unauthenticated socket accepts the commands**.
**Why it matters:** the security boundary of every customer 1C base is the edge proxy, which
is not in any repo. cloud-os hands this exact URL to end-user browsers, so it is probably
public.

### A2. Fleet enumeration and route hijack (verified)
`get_connector_bases` returns `user_bases` for every user and `all_bases` for the whole fleet
to that unauthenticated socket (`cloud_router_ws.py:491-531`). A fake `hello` with a victim's
`oneCId` and status `connected` overwrites `conn-of-infobase:{oneCId}` (last writer wins), and
the cloud router prefers the highest `appVersion` (E, `cloud_router_ws.py:173-184`).
**Why it matters:** an attacker can list every base, then receive real write payloads and
return forged "posted" results.

### A3. `create_ref` is a remote procedure call into the customer's configuration (verified)
The connector spreads the cloud's `extras` raw into the catalog create body
(`connector:providers/onec-sync-provider.tsx:7168,7220`). The adapter honours
`_postCreateMethod`: `"Module.Method"` calls `ОбщиеМодули.<Module>.<Method>()` and then again
with the new ref; a bare name calls a method on the new object (`main.os:7150-7180`). It is
also logged-and-ignored on failure.
**Why it matters:** any 0/1-argument server procedure in a client's 1C is reachable from A1 or
from the LAN. Nothing in the rewrite's docs mentions this directive.

### A4. Partner tokens can hard-delete 1C documents (verified)
`X-Service-Secret` accepts the master secret **or** `ODOO_API_TOKEN`, `MCP_API_TOKEN`,
`AIBA_BI_API_TOKEN` as equal peers (`b1c:app/api/internal/dependencies.py:10-50`), and the
same gate protects `/connectors/infobase/{id}/document/delete` with `hard`
(`b1c:app/api/internal/routes/connectors_admin.py:384-393`). The workspace note that MCP grants
exclude unpost/delete is enforced only in aiba-next.
**Why it matters:** an external partner token can physically delete documents in any
customer's base.

### A5. The relay tunnel is a remote shell into the bank signer, not only a signing pipe (verified)
The earlier audit lists the tunnel as "bank signing only". The code forwards **any HTTP method
and any path** to `127.0.0.1:6210`, or to the signer **admin** API `:7777` when the backend
sets `http.base = "signer"|"admin"` — documented in code as "bank actions: kapital
create-payment, sync" (`tauri:handlers/tunnel_agent.rs:47-55, 404-439`). It connects after one
visit to Settings (token generated there) and then on every start while the bank key manager is
on.
**Why it matters:** whoever controls `relay.aiba.uz` can drive payment actions on the PC.
Out of rewrite scope (D40), but it is a live risk on every old install.

## B. Data-integrity behaviour hidden inside writes

### B1. HR documents are always written in exchange (Load) mode (verified)
`DOCS_REQUIRING_EXCHANGE_MODE` (9 types: payroll, vacation, sick leave, return to work,
dismissal) makes `adapterPostDocument` add `exchange=true` regardless of the caller
(`connector:providers/onec-sync-provider.tsx:4688-4715`). The adapter's own comment says a
forced exchange post would be "empty" (no movements). This directly contradicts the workspace
rule and the rewrite's D38 refusal, but the old behaviour itself is undocumented.
**Why it matters:** production HR documents may be posted with no register movements.

### B2. Incoming bank documents can be force-flagged as posted (verified)
When posting an incoming bank document fails with an accounting-policy error, `СоздатьДокумент`
sets posted under `DataExchange.Load` and writes again (`main.os:17194-17252`, log tag
`[CREATE][FORCE]`).
**Why it matters:** "posted" in 1C does not prove movements exist for these documents.

### B3. Every catalog item the connector creates skips configuration checks (verified)
`СоздатьЭлементСправочника` always sets `DataExchange.Load = True` before `Write()`
(`main.os:7136-7142`).
**Why it matters:** counterparties, nomenclature, employees created by the connector bypass
BeforeWrite/OnWrite handlers.

### B4. A hidden second write path for unknown cloud frames (reported by A, path verified)
`dispatchControlCommand` returning false sends the frame to a 500-entry store queue
(`onec-sync-provider.tsx:15330-15331`). The only consumer is the `/documents` page, which is
not in the sidebar, deep-scans frames for documents and writes them with raw `fetch`, outside
the gateway and with the provodka text instead of the `AIBA_` marker.
**Why it matters:** a mistyped cloud action can still write to 1C if someone left that page
open.

### B5. Requests without a base prefix hit "base #0" (reported by C)
Every adapter route without `/api/db/{base}` silently runs against the first active base
(`main.os:2948-2956`); with the router's base-less meta worker it is whichever base that worker
loaded first.
**Why it matters:** a client bug can write to the wrong company with no error.

## C. Control-plane behaviour that breaks silently

### C1. Error replies never reach the caller (verified)
The hub relays only `result`, `ack`, `*_result`, `*_ack`, `*_progress`
(`connector_ws.py:189-203`). The connector answers many failures with `action:"error"`
(`onec-sync-provider.tsx:1761-1767`), `refdata_response` (`:7520`) or `route_not_found`
(`:4376`). All are dropped. **Why it matters:** callers wait the full 120-600 s; refdata via the
new hub can never work; `employee_list_from_1c` has no handler at all.

### C2. Closing one socket takes the other's routes down (verified)
Both sockets share one `connectorId`. `unregister_connector` deletes the hash and **every**
route; its comment says it only clears routes still pointing at this connector, but the code
deletes unconditionally (`b1c:app/core/connector_hub.py:406-430`). The command socket ignores
`need_hello` (only `onec-hub.tsx:156` handles it), and heartbeat re-registration looks for
`infobases` while the connector sends `bases` (`connector_ws.py:174`).
**Why it matters:** each hub reconnect or dashboard unmount opens a write outage of up to 30 s.

### C3. Whole-base `sync_now` never acknowledges (verified)
Only targeted runs send `*_ack` (`onec-sync-provider.tsx:2051-2068`). A full sync from the admin
route returns `timeout` at 120 s while it keeps running; the final result is orphaned.

### C4. Presence forgets machines instantly and the IP is probably the proxy's (verified)
The record is deleted on close or after 90 s; there is no last-seen history. IP is
`ws.client.host` with no `X-Forwarded-For` handling (`connector_ws.py:102`), and the two
sockets overwrite it. Fields the connector sends but the hub drops: `session_id`, `app_mode`,
`role`, `tenant_id`, `connected_base_count`. **Why it matters:** there is no way to answer
"which versions are in the field" or "when was this PC last online".

### C5. A ready-made "refresh presence" command exists but nothing sends it (verified)
`connector_status_request` / `status_request` force a `state_update`
(`onec-sync-provider.tsx:15129-15135`); backend/1c never sends it.

## D. Configuration levers that do not do what they look like

### D1. An emptied sync-table list resurrects itself, and night/active choices never persist (verified)
`GET /sync-tables` returns `null` for `[]` (`b1c:app/api/v1/routes/onec.py:881`); the poller
then PUTs the compiled defaults back (`connector:features/accounting/providers/info-base-sync-provider.tsx:179-226`).
`_clean_sync_tables` keeps only `{table, reports}`, so the drawer's `schedule` is silently
dropped (`onec.py:815-830`). The rewrite's D46 notes "objects / null" but not these traps.

### D2. `extraTables` / manifest still look like a lever but do nothing for connectors (reported by D)
Admin `PUT /onecs/{id}/extra-tables` and the unauthenticated
`/api/internal/connector/manifest` are live, and aiba-next proxies the manifest, but the
connector stopped reading it in `c3fc269` (2026-08-09).

### D3. "Enable auth" on the adapter is a trap (verified)
The adapter supports a bearer token (`main.os:21360-21374`), but no connector code sends one,
so turning `enableAuth` on breaks the app itself.

### D4. aiba-next calls a sync-tables route that does not exist (reported by D)
`onec_meta.rs:301` uses `/api/v2/onec/{id}/sync-tables`; backend/1c has it only on v1.

## E. Local surprises

### E1. Opening Settings can restart the live adapter (verified in code, runtime unverified)
`get_adapter_pool_config` runs `ensure_adapter_dir`, whose sweep `taskkill`s every LISTENING
oscript/adapter-router process on ports 55899-55924, including the healthy router
(`tauri:handlers/filesystem.rs:86-119, 1022-1028, 2364-2369`). The watchdog respawns it; every
base pays a cold COM connect. About 15 commands share this path.

### E2. Heal edits the customer's 1C configuration (verified)
`heal_base` runs Designer `/DumpConfigToFiles`, ticks «Внешнее соединение», then
`/LoadConfigFromFiles … /UpdateDBCfg -Dynamic+` (`tauri:handlers/heal_extconn.rs:543`), with the
password on the command line and the backup left in `%TEMP%`. No undo command.

### E3. 1C user names read without any 1C password (verified)
File bases: `1Cv8.1CD` parsed directly; server bases: `psql -w` / `sqlcmd -E` against
`v8users` using trust / Windows auth (`tauri:handlers/onec_cluster.rs:14-21, 298`). The rewrite
explicitly did not port the dropdown; the mechanism itself is undocumented.

### E4. Hidden keys any user can press (verified for D and devtools)
Ctrl+Shift+D switches the whole app to dev backends and logs out; Ctrl+D then Ctrl+I opens
DevTools in **release** builds (`connector/scripts/updater.ps1:277-284` builds with
`--features devtools`); Alt+B is registered OS-wide; Ctrl+Shift+B or `?dev=true` opens a modal
that overrides every backend URL; Ctrl+Shift+T in the DB modal connects many bases with one
login; Alt+C on Settings starts the adapter.

### E5. The OneScript runtime never updates on existing installs (reported by B)
Extraction is gated only on a `.extracted` marker (`tauri:handlers/runtime.rs:348-442`), and a
re-extract runs `taskkill /F /IM oscript.exe /T` machine-wide (`:32-37`).

### E6. Elevation prompt on any PC that ever had a smart-card reader (reported by B)
`admin_work_pending()` counts non-present `SmartCardReader` devices; the relaunch drops the
original arguments (`tauri:utils/privileges.rs`).

### E7. Release `28e98b1` changed `main.os` without bumping `adapter.version` (reported by C)
`ensure_adapter_dir` copies `main.os` only when the stamp differs, so that fix does not reach
existing installs from this tree.

### E8. Device naming on the main API is dead (verified)
backend/backend names a device "AIBA Connector" and sends a login notification when
`X-User-Agent: AIBA-Connector` arrives, but the connector line is commented out
(`connector:services/api.ts:60`).

## F. Bank-side behaviour (out of rewrite scope, still on every old install)

- **NBU payments auto-sign on a timer** (30 min to 10 h) with the first certificate, no review;
  it switches itself back on when the page opens
  (`connector:app/(dashboard)/banks/payments/page.tsx:106-129, 374-400`). Verified.
- **Reconnecting a bank first deletes its cloud subscription** (cascades accounts and
  transactions per the code comment), also from a second PC
  (`connector:app/(dashboard)/banks/page.tsx:1463-1471` and four more banks). Verified for one.
- **Registered but unused**: a hidden Kapital Bank WebView that injects the user's login and
  password and spoofs "Uzum Business" headers (`tauri:lib.rs:67-480`), and
  `usb_test_disable_one`, which persistently disables a smart-card reader. Reported by B.

## G. Split brain between relays (reported by E, not re-verified)

Modern connectors register only on the backend/1c hub. Several cloud clients still use a
relative `/ws/1c` (cloud-os HR/payroll, AI chat default, soliq Mehnat
`wss://cloud.uic.aiba.uz/ws/1c`, provodka-ai `ws://onec-ws:9010`) that lands on the cloud-os
Node relay, where no modern connector is registered. aiba-next's own config comment says that
endpoint "carries no connectors". Unless prod env overrides differ, those HR/Mehnat/provodka-ai
write paths are dead.
**Why it matters:** product features that look alive may have been failing with
`ROUTE_NOT_FOUND` for weeks.
