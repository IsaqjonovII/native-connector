# E — Cloud → Connector control plane, presence & fleet (OLD AIBA Connector)

Read-only audit, from code. Code wins over docs. Secrets redacted. Date: 2026-10-03.

## 0. Checkouts audited

| Repo | Path | Branch | Last commit |
|---|---|---|---|
| backend/1c (control plane owner) | `D:\aiba\backend\1c` | `development` | `8107bc0` 2026-10-02 "internal: serve bank accounts, operation kinds and document metadata over HTTPS" |
| backend/backend (main, :8000) | `D:\aiba\backend\backend` | `feat/purchase` (stale) | `53b24b35` 2026-05-12. `origin/development` = `dfdc4176` 2026-09-08 also grepped. |
| connector (client) | `D:\aiba\connector` | `release` | `cfab3a1` 2026-09-30 |
| aiba-next backend | `D:\aiba\aiba-next\backend` | `feat/soliq-turnover-limit` | `2060486` 2026-09-27 |
| cloud-os | `D:\aiba\cloud-os` | `development` | `f8d67e41d` 2026-07-24 |
| backend/report | `D:\aiba\backend\report` | `development` | `bc8f133` 2026-09-25 |
| backend/bank | `D:\aiba\backend\bank` | `production` | `1267f8d` 2026-09-28 |
| backend/soliq | `D:\aiba\backend\soliq` | `production` | `01e5182` 2026-09-17 |

**Headline:** the real control plane is **backend/1c**, not backend/main. backend/main has **zero** code that talks to connectors (no `connectors` reference on `feat/purchase` nor `origin/development`; `git log --all -S "connectors/infobase"` empty). The docstring at `backend/1c/app/api/internal/routes/connectors_admin.py:1-4,79` ("backend/main calls this when an admin clicks an online infobase") is **false**.

---

## 1. WebSocket endpoints

### 1.1 Inventory

| # | Endpoint | Server | Who connects | Auth | Status |
|---|---|---|---|---|---|
| W1 | `wss://<1c-host>/api/v2/connector/ws` | backend/1c `app/api/connector_ws.py:99` (mounted `main.py` prefix `/api/v2/connector`) | Connector (2 sockets: `onec-hub.tsx:47,107` + `onec-sync-provider.tsx:1144,15275`); ALSO cloud clients (aiba-next `onec_write.rs`, others — see §4) | **None.** `connector_ws.py:9-11`, `onec-hub.tsx:23` "No auth on this socket by design" | ACTIVE |
| W2 | `wss://<1c-host>/ws/1c` | backend/1c `app/api/cloud_router_ws.py:918` | Legacy cloud clients only (same handler as W1 cloud branch) | **None** | Probably UNUSED: every `/ws/1c` client found is relative to the cloud-os/soliq host and lands on the cloud-os Node relay (W3), not on the 1c host (§5a) |
| W3 | `/ws/1c` → Node `onec-ws:9010` | `backend/1c/onec-ws/index.js` (1498 lines) and a **diverged** copy `cloud-os/onec-ws/index.js` (1282 lines) | Old connectors (fallback `ONEC_SYNC_SOCKET_URL=wss://onec.aiba.group`, `connector core/config.ts:192`), soliq Mehnat (`wss://cloud.uic.aiba.uz/ws/1c`) | **None** (no auth/token/origin check in index.js) | backend/1c copy: "retired by the connector-hub migration" (`.gitlab-ci.yml:47-52`) but still built/updated with `|| true` (`:51-52,79,124,268`). cloud-os copy: proxied by `guacamole-proxy.conf:18-21`. |
| W4 | `wss://<main>/api/v1/connections/ws` | backend/main `app/api/v1/connection.py:72` (prefix `api/v1/api.py:31`) | Connector registers it (`app/layout.tsx:45-49`, path `/connections/ws`) but with **`autoConnect={false}`** and nothing ever calls connect → never opens | First message `auth.connect{access_token}` (`core/websocket/plugins/auth.py` handler) | **DEAD from the connector** (see §1.6) |
| W5 | `/api/v1/auth/qr/ws/{session_token}` | backend/main `api/v1/auth.py:920` | Desktop QR login listener | session token in path | Login only, not control plane |

Connector URL derivation: both sockets take `oneCApiUrl` (mode-switched `ONEC_API_URL` / `ONEC_API_DEV_URL`, `stores/app.ts:51-60,101-102`) → host root → `/api/v2/connector/ws` (`onec-hub.tsx:33-51`, `onec-sync-provider.tsx:1133-1157`). Docs: prod `https://1c.aiba.group`, dev `https://aiba-1c-dev.aiba.uz` (`docs/connector-sync.md` §2).

Why `/api/v2`: edge strips Upgrade on `/api/internal` (`main.py` comment above `include_router(connector_ws_router)`).

### 1.2 Handshake (connector → hub)

Socket A — **presence-only hub** `providers/onec-hub.tsx` (mounted only in `app/(dashboard)/layout.tsx:93`):
- requires `oneCApiUrl && deviceId && userId` (`:67`)
- `hello {action, connectorId:"<userId>:<deviceId>", userId, deviceId}` (`:80-85,122`)
- bare `heartbeat {action, connectorId}` every 30s (`:30,127-136`)
- replies to `need_hello` (`:156`); **ignores all commands** (`:161-191`, deliberately)
- reconnect fixed 3s (`:31`)

Socket B — **command + routing socket** `providers/onec-sync-provider.tsx` (mounted in ROOT `app/layout.tsx:43`):
- `sendPresence("hello"|"heartbeat"|"state_update")` `:1660-1759`. Payload = `{action, type, protocol_version:1, timestamp, reason, client_id:<session_id>, connector:<identity>, ...identity, ...snapshot}`; on hello adds `message_type:"connector_hello"`, `capabilities{commands[...], resources:["bases","refdata","holidays"]}` (`:1717-1748`), `state`, `ready:true`.
- identity (`:1458-1507`, type `:174-183`): `connector_id = "<user_id>:<device_id>"`, `tenant_id:"default"` (hardcoded), `user_id`, `device_id`, `session_id` (random UUID per load), `app_version` (Tauri `getVersion()`), `app_mode` (dev/prod), `role:"admin"` (hardcoded).
- snapshot (`resolveConnectorState` `:1186-…`): `connected_base_count`, `bases[{base_key, infobase_id, name, status, company_ids[], mapped_info_base_ids[]}]`. Status forced not-connected when the oscript adapter is down (`:1191-1195`). Org bindings folded into parents.
- heartbeat every `HEARTBEAT_INTERVAL_MS` (30s default, env `NEXT_PUBLIC_ONEC_SYNC_HEARTBEAT_SECONDS`, min 10s; `:70-74`), carries full snapshot.
- `state_update` on store change if fingerprint changed (`:15405-15449`).
- half-open detection: no inbound for 3 intervals → close + reconnect (`:15232-15243`); exp backoff 1s→30s + jitter (`:15134-15153`); closes on `pagehide`/`beforeunload` (`:15386-15397`).
- **does NOT handle `need_hello`** (only grep hit for hello_ack/heartbeat_ack is a comment, `:15185`); unknown frames are buffered into `stores/onec-sync.ts` (cap 500) and never answered.

`device_id` = Rust `machine-uid` crate (`src-tauri/src/utils/fingerprint.rs:4-6`) = Windows MachineGuid, cached in zustand (`utils/device-id.ts`). Retired random fallback id is deleted on mount (`onec-sync-provider.tsx:205-243`).

### 1.3 Hub server message handling (`backend/1c/app/api/connector_ws.py`)

| Inbound action | Lines | Behaviour | Reply |
|---|---|---|---|
| any of `CLOUD_COMMAND_ACTIONS` ∪ `get_connector_bases` | `:126-130` | socket treated as a **cloud client** → `handle_cloud_message` (§1.5); also starts `connector-presence:update` relay | per cloud action |
| `hello`, `state_update`, `ready`, `agent_ready` | `:133-159` | `connector_id = _derive_connector_id(msg)` (`:88-96`: `connectorId`/`connector_id`, else `userId:deviceId`, else `anon:nodevice`); `register_connector(id, msg, ws.client.host)`; starts `_command_relay` once | `hello_ack {connectorId, ts}` |
| `heartbeat`, `ping`, `agent_heartbeat` | `:162-180` | `need_hello` if no hello yet; `heartbeat(id,msg)`; if record aged out re-registers only if payload has **`infobases`** (connector sends `bases`) else `need_hello` | `heartbeat_ack {ts}` |
| has `requestId`/`request_id` AND action ∈ {`result`,`ack`} or endswith `_result`/`_ack`/`_progress` | `:189-197` | `publish_result(reqId, msg)` → Redis `connector-resp:{reqId}` | none |
| anything else | `:200-203` | **silently dropped** (debug log) | none |
| disconnect | `:205-223` | cancel relay; `unregister_connector(id)` | — |

### 1.4 Redis keys / channels (backend/1c `app/core/connector_hub.py`)

| Key / channel | Type | TTL | Writer | Reader |
|---|---|---|---|---|
| `connector:{connectorId}` | HASH {connectorId, deviceId, userId, appVersion, infobases(JSON), ip, lastSeen, commands(JSON)} | 90s (`HEARTBEAT_TTL` `:35`) | `register_connector` `:270-340`, `heartbeat` `:343-403` | `list_online`, `connector_supports` |
| `connectors:online` | ZSET member=connectorId score=lastSeen | pruned on read (`list_online` `:459-478`) | register/heartbeat | `list_online` |
| `conn-of-infobase:{oneCId}` | STR → connectorId | 90s | register/heartbeat, only for bases with status ∈ {connected, ready, ok} (`:87-91,211-227`); disowned routes released (`:241-267`) | `connector_for_infobase` `:504-512`, `connection_state.routes_for_infobases` |
| `sync-of-infobase:{oneCId}` | STR "1" | 120s | upload path `mark_sync_active` (`services/connection_state.py:144-162`) | `connection_state` |
| `connector-cmd:{connectorId}` | pub/sub | — | `publish_command` `:539-546`, `dispatch_to_infobase` `:619` | socket-holding worker `_command_relay` (`connector_ws.py:56-85`) |
| `connector-resp:{requestId}` | pub/sub | — | `publish_result` `:549-556` | `dispatch_to_infobase`, `_relay_command_and_forward` |
| `connector-presence:update` | pub/sub {event: upsert/heartbeat/offline, connectorId, ts} | — | `publish_presence_update` `:559-575` | cloud sockets' `_presence_update_relay` (`cloud_router_ws.py:670-693`) → pushes `connector_bases_update` |

No Redis Streams. Pure pub/sub ⇒ **no persistence, no replay**: a command published while the relay is reconnecting is lost (`n==0` → `connector_offline`, `:633-636`); a reply published after the waiter gave up is lost.

### 1.5 Two dispatch paths (both end on `connector-cmd:{id}`)

**P1 — `dispatch_to_infobase(onec_id, action, payload, timeout, on_progress)`** `connector_hub.py:587-672` (admin/HTTP + Celery + Odoo):
1. `conn-of-infobase:{onec_id}` → connectorId; none → `{ok:false,error:"connector_offline"}`.
2. `requestId = uuid4().hex`; subscribe resp channel **before** publish.
3. publish `{action, requestId, request_id, oneCId, onec_id, **payload}`.
4. wait: `*_progress` frames → `on_progress` (≤5s each), not terminal; first other frame (incl. `*_ack`) is the answer (`_is_terminal_reply` `:578-584`). Timeout → `{ok:false,error:"timeout",requestId}`; non-JSON → `bad_result`.
- Timeouts: `COMMAND_TIMEOUT` 120s (env `CONNECTOR_COMMAND_TIMEOUT`) `:40`; `WRITE_COMMAND_TIMEOUT` 300s `:51`; post 90s (`services/onec_post.py:57`); reads 120s.
- No retry, no dedup at this layer. Retries live in callers (Odoo pipeline `MAX_ATTEMPTS=8`, `services/odoo_write.py:132`; `connector_offline` refunded).

**P2 — cloud-router `handle_cloud_message`** `cloud_router_ws.py:704-910` (for W1 cloud clients and W2):
- requires `request_id` from the client; per-socket in-flight set rejects duplicates (`:724-737`) — only per socket, not global.
- target resolution `_sanitize_target` (`:287-371`) from `target.{company_id,name,tin,base_key,infobase_id,base_name,…}` / `company.*` / top-level aliases; matching `_resolve_matches` (`:413-487`) over **`list_online()`** (NOT `conn-of-infobase`): explicit base key/id/name, else company_id ∈ base.company_ids, else **fuzzy name match** (substring / compact / acronym `_names_likely_match` `:140-160`). TIN is parsed but never matches (`:462-463`).
- write-class actions require explicit base (`REQUIRES_BASE_SELECTION_ACTIONS` `:88-100`, `:772-775`).
- several connectors advertise one explicit base → newest `appVersion` then freshest heartbeat wins (`_match_liveness` `:173-184`, `:852-862`); otherwise `ambiguous_route`.
- relay: subscribe, publish, forward **every** frame to the caller; final on `*_result`, `server_pong`, `error`, `route_not_found`, `ambiguous_route`, `requires_base_selection`, `connector_offline` (`:536-543`). Clock: 120s idle window, extended to now+45s by each non-final frame, hard ceiling 600s (`:27-48,546-614`).
- `server_ping` without target → fan-out to **every** online connector (`:752-768`, `:617-667`).
- local-only answers (no connector round trip): `connect` → `connect_result` (`:781-795`), `get_bases` → `get_bases_result` (`:797-813`), `list_resource` type bases (`:815-833`), `get_connector_bases` → `connector_bases_snapshot` with `bases` (for given user_id), **`user_bases` (every user) and `all_bases` (whole fleet)** (`:490-533,715-718`).

### 1.6 Main-backend socket (W4) — dead path
- Connector: `WebSocketWrapper id="ws" path="/connections/ws" autoConnect={false}` (`app/layout.tsx:45-49`); `WebSocketProvider` only connects when `autoConnect` (`websocket/providers/socket.tsx:124-133`); `useWS().reconnect` only re-registers, never connects (`:294-299`); no caller of `reconnect` anywhere. ⇒ `SeedProvider`'s `auth.connect` (`providers/seed.tsx:230-238`) and `auth.logout` listener never run.
- Server side senders: `utils/company.py:44 request_data_from_client` → **no callers** (DEAD); `pending_requests` (bank.credentials) never populated (DEAD); `core/websocket/plugin_handler.py:15` imports `connection_manager` which does not exist in `connection_manager.py` (only `manager`) — module never imported (DEAD, would ImportError). Only live use: `api/v1/auth.py:602-606` sends `auth.logout` to an associated session on logout — no-op for connectors that never connected.

---

## 2. Command registry (cloud → connector)

Legend: Route = trigger. Auth: SS = `X-Service-Secret` accepting **any** of `SERVICE_SECRET_KEY`, `ODOO_API_TOKEN`, `MCP_API_TOKEN`, `AIBA_BI_API_TOKEN` as full peers (`api/internal/dependencies.py:10-48`); NONE = unauthenticated socket. Connector handler lines are `connector/apps/app/src/providers/onec-sync-provider.tsx`. "Cap" = advertised in hello `capabilities.commands` (`:1719-1745`).

| Action sent to connector | Triggered by (backend/1c) | Auth | Timeout | Connector handler | Reply action(s) | Cap | Status / evidence |
|---|---|---|---|---|---|---|---|
| `sync_now {mode: cursor\|full}` | `POST /api/internal/admin/connectors/infobase/{id}/sync?mode=` `connectors_admin.py:173-186` | SS | 120s | `:14925` → `runSyncNowCommand` `:1896` | `sync_now_result` (whole-base run waits for the full sync result); `sync_now_ack` only for targeted runs (`:2051-2066`) | no | ACTIVE; callers: provodka-ai / aiba-next / MCP / runbooks (§4). Whole-base sync from HTTP will usually **timeout at 120s** while sync continues (doc §4 claims a fast `running` ack — code only acks targeted runs). `sync_in_progress` if already running (`:1971-1980`). |
| `pull_now {tables[], priority}` | `POST …/infobase/{id}/read-table` `:189-196`; Celery `tasks.refresh_report` (`core/celery/reports.py:134`) — on demand `POST /api/v2/reports/{key}/refresh` (JWT) and daily 03:00 beat (`core/celery/app.py` "refresh-all-reports-daily") | SS / JWT / cron | 120s | same; targeted+preempting unless priority=low (`:1955-1969`) | `pull_now_ack` fast, then `pull_now_result` (orphan — waiter already returned) | no | ACTIVE |
| `write_to_1c {documents[], doc_type, post_document, base_key, infobase_id, onec_connection_id, target}` | Odoo pipeline `services/odoo_write.py:702-728 dispatch_document` (from `POST /api/internal/admin/odoo-inbox` inline + beat `process_odoo_inbox_backstop`); cloud W1/W2 clients (P2) | SS / NONE | 300s (P1) / idle 120s+45s, ceiling 600s (P2) | `:14959` | `write_to_1c_progress`, `write_progress` every 10s (`:122,1565-1658`), `write_to_1c_result` | yes | ACTIVE |
| `post_1c_document {ref_key, doc_id, doc_type, base_key…}` | `POST …/infobase/{id}/document/post` → `onec_post.post_existing_document` (`connectors_admin.py:272-328`, `services/onec_post.py:200-238`); cloud bases go OData instead | SS | 90s | `:14972` | `post_1c_document_result` | yes | ACTIVE |
| `unpost_1c_document {doc_type, ref_key}` | `POST …/document/unpost` `:331-340` | SS | 120s | `:14980` | `*_result` | yes | ACTIVE (route); MCP excludes it from grants per workspace CLAUDE.md |
| `update_1c_document {doc_type, ref_key, fields, auto_unpost}` | `POST …/document/update` `:343-358` | SS | 120s | `:14985` | `*_result` | yes | ACTIVE |
| `delete_1c_document {doc_type, ref_key, hard}` | `POST …/document/delete` `:488-498` (hard=true physically deletes) | SS | 120s | `:14990` | `*_result` | yes | ACTIVE |
| `catalog_update {catalog, ref_key, fields, expected?}` | `POST …/catalog/update` `:361-405`; refuses if connector lacks `catalog_update` / `catalog_update_cas` capability | SS | 120s | `:15109` | `catalog_update_result` | yes | ACTIVE (callers: kansler ИКПУ per docstring) |
| `document_read {table, date_from, date_to, number, posted_only, limit≤200, offset, organization}` | `POST …/documents/read` `:462-476` (409 `connector_too_old` if no cap) | SS | 120s | `:15114` | `document_read_result` | yes | ACTIVE (EDO draft import) |
| `catalog_read {catalog, ids≤200}` | `POST …/catalog/read` `:479-485` | SS | 120s | `:15119` | `catalog_read_result` | yes | ACTIVE |
| `employee_write_to_1c` | P2 cloud socket (soliq Mehnat via legacy `/ws/1c`; HR) | NONE | P2 clocks | `:14945` | `employee_write_to_1c_progress`, `_result` | yes | ACTIVE |
| `employee_list_from_1c` | P2 | NONE | P2 | **no handler** — `list_resource{resource_type:employees}` exists (`:15020-15045`) but bare `employee_list_from_1c` falls through → buffered, never answered | — | no | **BROKEN on new hub** (times out); only legacy Node relay may translate |
| `employee_fire_from_1c` | P2 | NONE | P2 | `:15124` | `employee_fire_from_1c_result` | no (handled, not advertised) | ACTIVE |
| `check_exists` | P2 | NONE | P2 | `:14930` | `check_exists_result` | yes | ACTIVE |
| `delete_from_1c` / `delete_doc_from_1c` | P2 | NONE | P2 | `:14935` (AIBA-marked HR docs only) | `delete_from_1c_result` | yes | ACTIVE |
| `push_to_1c` / `create_in_1c` (departments / schedules) | P2 | NONE | P2 | `:14995-15013` | `push_to_1c_result` | yes | ACTIVE; other resource types fall through → no reply |
| `delete_in_1c` / `department_delete_in_1c` | P2 | NONE | P2 | `:15060-15072` | `delete_in_1c_result` | yes | ACTIVE |
| `holiday_push_to_1c` / `holiday_delete_in_1c` / `holiday_check_1c` | P2 | NONE | P2 | `:15047-15058` | `*_result` | yes | ACTIVE |
| `list_resource {resource_type}` | P2 (bases answered by hub locally; schedules/departments/employees/refdata go to connector) | NONE | P2 | `:15020-15097` | `list_resource_result`; **refdata → `refdata_response`** | yes | refdata branch **BROKEN** (reply dropped, §6) |
| `get_list` | P2 | NONE | P2 | `:15099` | `get_list_result` | no | ACTIVE |
| `create_ref` | P2 | NONE | P2 | `:15104` | `create_ref_result` | no | ACTIVE |
| `server_ping` / `ping` | P2 (targeted or fan-out to all) | NONE | 120s | `:14910` | `server_pong` | no | ACTIVE |
| `get_bases` | answered by hub locally; connector handler exists `:14920` | NONE | — | — | `get_bases_result` | yes | connector branch effectively UNUSED |
| `refdata_get` / `get_refdata` | not in hub `CLOUD_COMMAND_ACTIONS` (`cloud_router_ws.py:50-68`) → `unsupported_action` | — | — | `:15090-15097` | `refdata_response` | yes | **UNREACHABLE** via new hub |
| `department_write_to_1c` | not in hub action set | — | — | `:15015` | — | no | UNREACHABLE via hub (legacy only) |
| `connector_status_request` / `status_request` | **no sender** in backend/1c | — | — | `:15129-15135` → forces `state_update` | — | no | DEAD (nice-to-have "refresh presence" hook, unused) |
| `read_table` | no sender | — | — | not handled (hub socket explicitly ignores it `onec-hub.tsx:185-190`) | — | — | DEAD |
| `pull_now`/`sync_now` on hub socket | fan-out reaches socket A too | — | — | ignored by design | — | — | n/a |

**Generic / extensible patterns**
- **No "proxy any adapter path" command exists** in this control plane. Every action is an explicit `if` in `dispatchControlCommand` (`:14905-15138`). Unknown action → `return false` (`:15137`) → silently buffered (`:15330-15331`, `stores/onec-sync.ts`) → caller times out.
- Near-generic levers: `pull_now{tables[]}` takes **any** 1C table name (whatever the adapter can read; the connector narrows `onlyTables`), and `/api/internal/connector/manifest` + `OneC.extraTables` (`PUT /api/internal/admin/onecs/{id}/extra-tables`, `api/internal/routes/onec.py:194`) add tables per base without a release. `document_read` / `catalog_read` give bounded live reads (Document_* tables only, ≤200 rows / ids).
- Capability gating: hub stores `capabilities.commands` lowercased (`connector_hub.py:317-325`); used only by `catalog/update`, `documents/read`, `catalog/read` (`connector_supports` `:481-501`).

**Connector → cloud (upstream) messages**: `hello`, `heartbeat`, `state_update` (+`ready`); `*_result`, `*_ack`, `*_progress`, `write_progress`; `server_pong`; `error`; `refdata_response`; `route_not_found` (`:4376`). Data itself does **not** go over the socket: rows go `POST /api/v2/entity/upload` (JWT) and the HTTP status heartbeat goes `PATCH /api/v2/onec/connection/{id}` (§3).

---

## 3. Presence / fleet — what the backend knows per installation

Three independent presence signals exist and disagree by design:
1. **WS presence** (Redis, 90s TTL) — authority for "can write" (`services/connection_state.py:1-15`).
2. **HTTP status heartbeat** — connector `PATCH /api/v2/onec/connection/{oneCId} {status:"active", totalCount?}` every 60s, only for bases that synced once (`connector providers/connection-status.tsx:16,109-160`), persisted on `OneC.status/statusUpdatedAt` in Mongo; Celery `tasks.check_onec_heartbeat` every 60s demotes to `inactive` after 3 min (`core/celery/api.py:2459-2509`).
3. **Upload activity** — `sync-of-infobase:{id}` 120s TTL.

| Field | Connector sends? | Backend observes/stores | Persisted where | Exposed to admins |
|---|---|---|---|---|
| connectorId | `connector_id` = `user_id:device_id` (`onec-sync-provider.tsx:1500`, `onec-hub.tsx:78`) | `connector:{id}.connectorId` | Redis only, 90s | `GET /api/internal/admin/connectors` (SS) |
| deviceId | yes, MachineGuid (`fingerprint.rs:4`) | `deviceId` | Redis 90s. Also `X-Device-Id` header on main-backend HTTP (`services/api.ts:61`) → `AuditService` (`backend/backend app/core/audit/service.py:51-56`) | connectors list; main admin audit log |
| installationId | **not a concept** — device_id is per machine, connectorId per (user, machine). Two Windows users / two installs on one machine with the same AIBA user collide | — | — | — |
| userId (AIBA) | yes | `userId` | Redis 90s | connectors list; `get_connector_bases` (NONE auth) |
| company | per base `company_ids` | inside `infobases` JSON | Redis 90s; Mongo `OneC.companyId` | connectors list |
| machine name / hostname | **no** | — | — | — |
| Windows user | **no** | — | — | — |
| OS / OS version | **no** | — | — | — |
| app version | `app_version` | `appVersion` (hello + every beat, `connector_hub.py:383-393`) | Redis 90s | connectors list; used for routing tie-break |
| adapter version (`adapter.version`) | **no** | — | — | — |
| capabilities | hello only | `commands` (`:317-325`); `resources` dropped | Redis 90s | connectors list |
| 1C platform version | **no** | — | — | — |
| 1C configuration version per base | not over WS; via HTTP PATCH `version` (`utils/info-base.ts:257-270`) | `OneC.version` | Mongo | internal `/onecs`, main admin "Connections" (no version column shown, `admin/views/onec_remote.py:159-177`) |
| bases + statuses | `bases[]` with status from adapter `/api/databases/status` | normalized `infobases` (`:94-208`); `provider`, `version`, `configuration_version` always empty (connector never sends) | Redis 90s | connectors list; `onlineInfobaseIds` |
| app_mode, tenant_id, role, session_id, protocol_version, connected_base_count | yes | **dropped** (not in record `:293-316`) | — | — |
| connectedSince | no | **not tracked** | — | — |
| lastSeen | implicit | `lastSeen` epoch + `ageSeconds` on read | Redis | connectors list |
| heartbeat | 30s both sockets | TTL refresh | Redis | — |
| reconnect state / counts | client-side only (`reconnectAttemptsRef`) | not sent | — | — |
| IP | not sent | `ws.client.host` (`connector_ws.py:102`) — **socket peer**, no `X-Forwarded-For` handling anywhere in hub. Behind the edge proxy this is the proxy/ingress IP unless uvicorn runs with `--forwarded-allow-ips`; the swarm service command is **not in the repo** (`Makefile:7-10` has plain `uvicorn`, Dockerfile has no CMD). Overwritten by whichever of the 2 sockets hello'd last | Redis `ip` 90s | connectors list |
| offline history / last-ever-seen | — | **none**: record deleted on close (`unregister_connector` `:406-430`) or TTL. Once offline, the backend forgets the installation entirely | — | admins can only see "online now" |

Admin exposure summary: only `GET /api/internal/admin/connectors` and `GET …/connectors/infobase/{id}` (SS). backend/main admin (`starlette_admin`, `app/admin/views/onec_remote.py:144-180`) shows Mongo `OneC` rows (name, odataName, provider, **status** = HTTP heartbeat, totalCount, percentage) plus EntityData / CachedMetric / EntityCount, edit/delete — no WS presence, no versions, no sync button. User-facing: `GET /api/v1|v2/onec` annotated with `connection_state` / `can_write` / `state_reason` (`services/connection_state.py:165-188`; doc `docs/connector-status-frontend.md`).

---

## 4. Identity model

| Entity | Identifier | Proven? |
|---|---|---|
| User | AIBA user UUID (`user_id`) | **Self-declared** on the socket. HTTP routes verify JWT (`app/dependencies/auth.py:43-60`, HS JWT with `JWT_SECRET_KEY`); WS never does. |
| Connector | `"<user_id>:<device_id>"` | Self-declared; explicit `connectorId` honoured verbatim (`connector_ws.py:91-93`). |
| Device | MachineGuid | Self-declared. |
| Session | `session_id` UUID per page load | Sent, ignored by the hub. |
| Infobase | Mongo `OneC._id` (24-hex) = `oneCId` = `infobase_id`; `base_key` = adapter-side key (`connector_hub.py:162-205`) | Self-declared list; hub never checks that the claimed `oneCId` belongs to the claimed user/company. |
| Org binding | `OneC` doc with `connectionId`+`orgRef`; folded into parent `mapped_info_base_ids` | as above |
| Cloud client vs connector | decided by **action name** of each message (`connector_ws.py:121-130`) | not by credential |
| Service callers | shared secrets, 4 interchangeable tokens | `OdooAuditMiddleware` records which token on `/api/internal/admin/*` |

User ↔ data ownership is enforced on JWT HTTP routes via `get_onec_with_ownership` (userId match), but not on the socket.

---

## 5. Admin / staff features over connectors (what exists)

| Feature | Exists? | Where | Caller evidence |
|---|---|---|---|
| List online connectors + bases | yes | `GET /api/internal/admin/connectors` `connectors_admin.py:146-163` | aiba-next `onec_meta.rs:593-659` (per-company filtered, via tenant module); runbooks (`docs/fleet-resync-runbook.md:107`). No UI. |
| Is base online | yes | `GET …/connectors/infobase/{id}` `:166-170` | provodka-ai `cloud-os/aiba-provodka-ai/app/clients/aiba_1c.py:105-118` (from `services/cost_lookup.py:316`) |
| List offline / last seen ever | **no** | — | — |
| Versions | appVersion only (online only) | connectors list | — |
| Force sync (cursor/full) | yes | `POST …/sync?mode=` | aiba-next `onec_pull.rs:199-211` (warehouse fallback, cursor); kansler `onec_pull.rs:177,188`; fleet runbook (manual, full) |
| Targeted table pull | yes | `POST …/read-table`; reports refresh | provodka-ai `aiba_1c.py:121-139` (auto during classify, ≤1/15min/base, `cost_lookup.py:35`); aiba-next `onec_pull.rs:236-240` (`SyncCatalogButton`, `frontend/.../avtoprovodka/api.ts:1184`); backend/1c Celery reports |
| Remote doc post/unpost/update/delete | yes | `…/document/*` | |
| Catalog update (CAS) | yes | `…/catalog/update` | |
| Live reads | yes (bounded) | `…/documents/read`, `…/catalog/read` | |
| Diagnostics / logs / errors / resources / restart / update | **no command** | — | logs only on the PC (`uiLog` → `debug.log`, Sentry) |
| Org mappings | yes (user JWT) | `GET/PUT/DELETE /api/v2/onec/{id}/org-bindings` (`api/v2/routes/onec.py:515,606,735`) | connector UI |
| Table config | yes | `PUT /api/internal/admin/onecs/{id}/extra-tables` (SS); `PUT /api/v1/onec/{id}/sync-tables` (user) ; `GET /api/internal/connector/manifest?onec_id=` (**no auth**, `routes/connector.py`) | |
| Counts / coverage | yes | `GET /api/internal/admin/entity-data/stats?onec_id=` (SS); `GET /api/v2/onec/{id}/counts` (JWT); `OneC.dataCoverage` | |
| Rebuild / resync | yes | `POST /api/v2/onec/connection/rebuild[?oneCId=]` — **no auth** (`api/v2/routes/onec.py:255-285`); full sync; fleet runbook `docs/fleet-resync-runbook.md` | |
| Row-level repair | yes | `GET/DELETE /api/internal/admin/entity-data/{id}` | |
| Command dispatch generic | **no** | — | — |

### 5a. Consumers outside backend/1c (who actually calls the control plane)

Paths relative to `D:\aiba`; NC = `cloud-os\nextcloud-server\apps`. (Sub-agent sweep; spot-checked PHP URL lines myself.)

| Client | Channel / actions | Trigger | Auth sent | Notes |
|---|---|---|---|---|
| NC `aiba_avtoprovodka` browser JS (`js/aiba-avtoprovodka.js:13-19,901,976`) | WS `ONEC_WS_URL` → default `wss://1c.aiba.group/api/v2/connector/ws` (`lib/Controller/PageController.php:20`), else `/ws/1c`; `get_connector_bases`, `write_to_1c`; handles `refdata_response`, `route_not_found`, … | accountant base picker + send-to-1C | **none** | write timeout 600s; online = WS OR Mongo `active` OR cloud base (`:1092-1103`) |
| NC `aiba_rdp` browser JS (`js/aiba-rdp-provodka.js:76-82,171,1287`) | same URL logic (`PageController.php:28`); `check_exists`, writes | RDP provodka page | none | 120s / 300s |
| NC `aiba_employees` (`js/employees.js:19,6406`; `js/payroll.js:139,…`) | WS hardcoded `/ws/1c` (local Node relay): `get_list`, `create_ref`, `employee_fire_from_1c`, `check_exists`, `push_to_1c`, `list_resource`, `delete_in_1c`, `get_bases`, `delete_from_1c`, `holiday_*` | HR / payroll pages | none | 120s |
| NC `aiba_integration` PHP AI chat (`AiChatController.php:121,10425-10647,10980-11077`) | server-side WS `ONEC_WS_URL` or `ws://127.0.0.1/ws/1c`; `get_bases`, writes, `check_exists` | AI chat tools | none, TLS verify off | 15s |
| NC `aiba_documents` | URL injected (`DocumentsController.php:56`) but JS never opens a socket | — | — | UNUSED |
| `cloud-os/aiba-provodka-ai` | HTTP `connectors/infobase/{id}`, `read-table` (`app/clients/aiba_1c.py:105-139`); WS `ws://onec-ws:9010` `hello{role:"provodka_service"}` + `write_to_1c`/`check_exists` (`app/clients/onec_ws.py:100-118,226,244`) | `/api/v2/classify`, `POST /api/v1/write` | SS for HTTP; WS sends Bearer service secret that the relay never checks | |
| `backend/soliq` Mehnat (`app/services/v1/onec_ws_service.py:41-55,131-193`) | WS `wss://cloud.uic.aiba.uz/ws/1c`: `employee_write_to_1c`, `employee_fire_from_1c`, `employee_list_from_1c` | `/api/v1/mehnat/employees/sync-to-1c` etc. (`routes/v1/mehnat.py:316,331`, no auth) | Origin header only | 60s |
| aiba-next / kansler | see §5b | | | |
| backend/main admin | **none** (only Mongo-side `RemoteOneCAdmin`) | | | |
| `web\*` MFEs, backend/report, bank, documents, purchases | **none** (only stale OpenAPI copies) | | | |

Relays: cloud-os still builds and runs its own Node `onec-ws` (`cloud-os/docker-compose.yml:204-209`, Apache `/ws/1c` → `ws://onec-ws:9010/` `guacamole-proxy.conf:17-21`); diet-cloud has a byte-identical copy. `backend/1c/onec-ws/index.js` diverged (adds `PENDING_DISPATCH_HOLD_MS` 60s offline hold, `index.js:10`). No auth in any of them; `hello` is what makes a socket a "connector" there too (noted in `cloud-os/.planning/codebase/CONCERNS.md:58`).

**Answer to "is there a staff fleet UI?": no.** No admin/staff screen in any repo lists connectors online/offline, versions, last seen, or triggers sync/read-table across companies. The only connector-aware UIs are per-company pickers (cloud-os avtoprovodka, aiba-next ConnectorPicker / toolbar badge).

---

## 5b. "NEXT mode" — aiba-next and the per-tenant Rust hub

Second, parallel control plane. Evidence from `D:\aiba\aiba-next\backend\crates\…` (sub-agent read, file:line cited) and `D:\aiba\next-modules\onec` (`master` `6c46165` 2026-09-27).

- aiba-next no longer calls central backend/1c on user paths: `st.onec` = tenant `onec_module_url`, sealed if unset (`api-core/src/tenant.rs:774-781`); WS URL derived per request as `ws(s)://<module>/api/v2/connector/ws` (`tenant.rs:796-821,884`). Global default `wss://1c.aiba.group/api/v2/connector/ws` (`api-core/src/config.rs:326`) survives only on non-tenant paths — e.g. `avtoprovodka_check_scheduler.rs:104-109` swaps only the DB pool, so it **likely still hits central backend/1c**.
- `next-modules/onec/src/connector.rs` is a Rust port of the same hub: `/api/v2/connector/ws`, `/api/internal/admin/connectors`, `…/infobase/:id/sync`, `…/read-table` (`:2297-2310`). Same self-declared `hello`. Differences: optional handshake gate `X-Service-Secret` (`connector_ws_require_secret`, **default OFF**, `:1110-1146`); peer IP from `x-forwarded-for` (`peer_of` `:1153+`); records `via = next|direct`.
- Connector → aiba-next tunnel (`accounting/src/modules/onec_proxy.rs`): `GET /api/v2/connector/ws` and `/ws/connector` → tenant module hub, frames copied verbatim, `x-service-secret` injected upstream; client auth = **JWT signature only** (`:311-325,335`), token may come from `aiba.jwt.<token>` subprotocol (`:277-280`). `ANY /api/v2/1c/*path` → module `/api/v2/{path}` with Authorization stripped and service secret added, any logged-in user, no company check (`:144-215,241`) — carries the connector's `/entity/upload`.
- Company-gated read-only lane `GET /api/v2/onec/companies/:company_id/connector/ws` (+ `/api/ext/v1/1c/...`): allowlist `get_connector_bases, check_exists, get_list, employee_list_from_1c, holiday_check_1c` (`onec_proxy.rs:455-461`), base must be owned (`:478-533`) — but `get_connector_bases` skips the ownership check and hub `connector_bases_update` broadcasts pass through (`:501-503,677-686`) → leaks other companies' presence.
- aiba-next as a cloud client sends: `get_connector_bases` (6s, `platform/src/modules/onec_write.rs:33,58-95`, fixed request_id `next-roster-{pid}` `:61`), `write_to_1c` (90s from dispatch, +45s per progress/ack, cap 600s, `:397-463`; request_id `next-{pid}-{nanos}` `:485`), `check_exists`, `get_list`, `create_ref` (sotuv). HTTP: `read-table`, `sync?mode=cursor` (`accounting/.../onec_pull.rs:199-240`, 130s), `document/post` (100–120s), `GET /api/internal/admin/connectors` (filtered to the company, `onec_meta.rs:593-659`), manifest.
- UI over connectors exists **only in aiba-next frontend, per company**: toolbar `write_ready`/`relay_status` badge (`frontend/src/modules/avtoprovodka/toolbar.tsx:270-291`, kansler_bank `toolbar.tsx:260-281`); «Ulanishlar» ConnectorPicker showing `app_version`, `online_seconds`, device, user, polling `GET /1c/onec/{id}/connectors` every 15s and `PUT /1c/onec/{id}/preferred-connector` (`connector-picker.tsx`, `api.ts:2220-2238`); `SyncCatalogButton` → `onec/pull-table` (`api.ts:1184`). Superadmin only has module `/health` ping (`control/src/modules/provision.rs:3316,3357-3363`). **No cross-company fleet screen anywhere.**

## 6. Remote support — what staff can do without the customer's PC

| Capability | Mechanism | Live or async | Result stored | Limits |
|---|---|---|---|---|
| Base list / status | `GET /connectors`, `/connectors/infobase/{id}`, `get_connector_bases` | live snapshot from Redis (no PC round trip) | Redis only | online only; no history |
| Connection test | `server_ping` (P2 socket) | live | none | proves the JS socket, not 1C/COM |
| Metadata / schema | **none over the control plane** in backend/1c; `GET /api/internal/admin/onec/{id}/write-metadata/{operation-kinds,documents}` (`routes/write_metadata.py:432,490`) serves stored metadata | stored | Mongo | not live |
| Row reads by filter | `documents/read` (Document_* by date/number/org, ≤200), `catalog/read` (by ids ≤200), `get_list`, `check_exists` (P2) | live, 120s | not stored | single-threaded adapter; can queue behind a sync |
| Row reads of stored data | `GET /api/internal/admin/entity-data?...` | async copy | Mongo `entitydatas` | as fresh as last sync |
| Counts | `entity-data/stats`, `/onec/{id}/counts` | stored | Mongo | — |
| Pull a table now | `read-table` / `pull_now` | ack live, data async via `/entity/upload` | Mongo | no per-request status; poll counts |
| Full / cursor sync | `sync` | result live (often times out at 120s), data async | Mongo | `sync_in_progress` refusal |
| Writes / post / unpost / update / delete | admin routes + Odoo inbox + P2 | live | Odoo rows `WrittenDoc`/inbox | 300s/90s; hard delete possible |
| Logs / errors / health / resources | **not available remotely** | — | client `debug.log`, Sentry | — |
| Restart connector / adapter / update app | **not available** | — | — | — |

---

## 7. Hidden / surprising findings

1. **backend/main is not the control plane.** No code in backend/main touches connectors; the 1c docstring claiming it does is wrong (`connectors_admin.py:1-4,79`).
2. **Connector errors and refdata replies never reach the caller on the new hub.** Hub only relays frames whose action is `result`/`ack` or ends `_result|_ack|_progress` (`connector_ws.py:189-197`). Connector sends `action:"error"` (`sendErrorReply` `onec-sync-provider.tsx:1761-1776`, used e.g. at `:4371`), `refdata_response` (`:7520,7537,7613`), `route_not_found` (`:4376`) → all dropped → caller waits out 120s (P1) or idle/ceiling (P2). cloud-os avtoprovodka JS (default URL = new hub) explicitly handles `refdata_response`/`route_not_found` from the connector — those code paths can only work on the legacy Node relay.
3. **Unknown/unsupported commands are never answered** — `dispatchControlCommand` returns false (`:15137`) and the frame is buffered (`:15330-15331`). `employee_list_from_1c`, `refdata_get`, `department_write_to_1c` are in one router's vocabulary but not the other's.
4. **Whole-base `sync` over HTTP has no fast ack.** The doc promises `sync_now_ack … status:"running"`; code acks only targeted runs (`onec-sync-provider.tsx:2051-2066`). A full sync triggered via `/sync` returns `timeout` at 120s while it keeps running.
5. **Two sockets, one identity, one close wipes both.** Hub and sync socket share `connectorId`; either socket's close runs `unregister_connector`, deleting the shared hash and every route (`connector_hub.py:406-430`) even though the other socket is alive. The sync socket ignores `need_hello`, so routes only come back when the hub re-hellos (3s) and the sync socket's next beat lands (≤30s) — a recurring write outage window on every hub reconnect / dashboard unmount. Also, after TTL loss, re-register-from-heartbeat checks `infobases`, but the connector sends `bases` (`connector_ws.py:174`).
6. **Commands fan out to both sockets** (both subscribe `connector-cmd:{id}`), so `publish` returns 2; hub ignores them by convention only (`onec-hub.tsx:161-191`).
7. **No durable command log.** Redis pub/sub only; no requestId status endpoint; reply after timeout is lost; late `pull_now_result` / `sync_now_result` frames are orphaned.
8. **Presence forgets offline machines.** No last-seen history, no install inventory; `tenant_id` hardcoded "default", `role` hardcoded "admin".
9. **IP is probably the proxy's** (socket peer, no XFF), and the two sockets overwrite each other's `ip`.
10. `services/write_idempotency.py` (WrittenDoc reserve/find) has **no callers** — its docstring describes a cloud_router_ws write orchestrator that does not exist; Odoo uses its own `_mark_written`.
11. `cloud_router_ws._base_is_connected("")` → True (`:187-191`) — opposite of the hub's "absent is not connected" rule; masked today because the hub normalizes absent status to `not_connected`.
12. backend/main websocket stack is mostly dead code (`request_data_from_client`, bank credential relay, `plugin_handler.py` with a broken import).
13. Doc references `docs/1c-socket-hub-plan.md` (`main.py`, `connector_ws.py:9-11`) — file not present in `docs/`.
14. Legacy Node relay still built/deployed on every CI run with `|| true`, and a diverged copy runs in cloud-os.
15. **Split brain between relays.** Modern connectors register only on the backend/1c hub (`oneCApiUrl` → `/api/v2/connector/ws`). Clients that use relative `/ws/1c` on a cloud-os host (aiba_employees HR/payroll, AI chat default, soliq Mehnat `wss://cloud.uic.aiba.uz/ws/1c`, provodka-ai `ws://onec-ws:9010`) reach the cloud-os Node relay, where only legacy connectors (fallback `wss://onec.aiba.group`) could be registered. aiba-next's own config comment says it outright: "the cloud.uic.aiba.uz endpoint answers but carries no connectors, so a write there always comes back ROUTE_NOT_FOUND" (`aiba-next/backend/crates/api-core/src/config.rs:323-326`). Unless `ONEC_WS_URL`/env overrides differ in prod, those HR/Mehnat/provodka-ai write paths are effectively DEAD.

## 8. Security concerns

1. **Unauthenticated command socket = remote write into customers' 1C.** Any internet client can open `/api/v2/connector/ws` or `/ws/1c` and send `write_to_1c`, `delete_from_1c`, `employee_fire_from_1c`, `push_to_1c`, `create_ref`, … with an explicit `base_key`/`infobase_id`; the hub routes it to the customer's PC (`cloud_router_ws.py:704-910`). Unless the edge (not in repo) blocks it, this is the single biggest risk. Strong hint the edge does NOT block it: cloud-os hands this URL **to the end-user browser** (`aiba_avtoprovodka/lib/Controller/PageController.php:20`, `aiba_rdp/.../PageController.php:28`, `aiba_documents/.../DocumentsController.php:56`, default `wss://1c.aiba.group/api/v2/connector/ws`; compose comment `cloud-os/docker-compose.yml:64-67`) and browsers cannot add auth headers to a WS handshake — so the socket must be publicly reachable for those apps to work.
2. **Fleet enumeration.** `get_connector_bases` returns every online base of every user (`user_bases`, `all_bases`) to an unauthenticated socket (`:490-533,715-718`), and cloud sockets get live `connector_bases_update` pushes. Gives the ids needed for #1.
3. **Route hijack / data exfiltration.** A fake connector can `hello` with any `connectorId`/`userId` and a `bases[]` list claiming victim `oneCId`s with `status:"connected"`; `conn-of-infobase` is last-writer-wins (`connector_hub.py:330-331,396-397`) and P2 prefers the **highest appVersion** (`_match_liveness`) — claim `appVersion:"99"` and win routing. The impostor then receives document payloads (`write_to_1c` envelopes, catalog updates) and can return forged `*_result` frames (fake "posted" outcomes).
4. **Result spoofing without hijack.** Any socket can publish `{"action":"x_result","requestId":<id>}` to `connector-resp:{id}` (`connector_ws.py:189-197`); requestIds are uuid4 (unguessable) for P1, but P2 uses **client-supplied** request_ids (soliq: `req-soliq-<timestamp>-<8 hex>`), so they are predictable-ish.
5. **Unauthenticated destructive HTTP:** `POST /api/v2/onec/connection/rebuild` with no `oneCId` deletes **all** `CachedMetric` and `EntityCount` fleet-wide and re-queues (`api/v2/routes/onec.py:255-285`); `POST /api/v2/onec/connection/backfill-total-count` likewise unauthenticated. `GET /api/internal/connector/manifest` unauthenticated (low impact).
6. **Partner tokens are full admin.** `ODOO_API_TOKEN`, `MCP_API_TOKEN`, `AIBA_BI_API_TOKEN` are accepted everywhere `SERVICE_SECRET_KEY` is, incl. hard delete of 1C documents (`dependencies.py:10-48`). No per-company scoping on any `/api/internal/admin/connectors/*` route.
7. **soliq Mehnat routes have no auth** (`backend/soliq app/routes/v1/mehnat.py:16,316,331`, only CORS in `main.py`) and drive `employee_write_to_1c` / `employee_fire_from_1c` through the legacy relay with TLS verification disabled (`onec_ws_service.py:49-55`).
8. **aiba-next tunnel** (sub-agent evidence, not re-verified line by line): JWT-signature-only auth on the full `/api/v2/connector/ws` tunnel lets any tenant user send `hello` (fake presence) or `write_to_1c` to any base in the tenant (`onec_proxy.rs:328-350`); possible cross-tenant hop because the subprotocol token is not used for tenant resolution (`tenant.rs:579-658` vs `onec_proxy.rs:277-280`); `/api/v2/1c/*` gives any user service-secret reach into the tenant module (preferred-connector writes, possible `..` path escape — untested); post-in-1c routes accept body `infobase_id` without ownership check (`avtoprovodka.rs:3645-3651,7819-7820`).
9. Hello carries `role:"admin"` — currently ignored server-side; if anything ever trusts it, it is self-asserted.

## 9. Open questions

1. Does the edge (nginx / Traefik on the 1c host, not in any repo) restrict `/api/v2/connector/ws` or `/ws/1c` by IP, mTLS or header? Code says no auth; confirm on the box.
2. Swarm service command for `aiba_1c` (`--workers`, `--proxy-headers`, `--forwarded-allow-ips`) — decides whether `ip` is real.
3. Is `aiba_1c-onec-ws` (backend/1c Node relay) still running, and does `onec.aiba.group` still resolve to it? Old connectors fall back there.
4. Which connector versions are still in the field without `capabilities` / `document_read`? (no fleet inventory exists to answer it.)
5. Who sends `employee_list_from_1c` today, and does it ever succeed on the new hub?
6. Prod values of `ONEC_WS_URL` in cloud-os / diet-cloud / provodka-ai / soliq `.env` — decide whether finding 15 (relay split brain) is real in prod.
7. Is the 2-socket unregister race (finding 5) visible in prod logs as periodic `connector_offline` bursts?
