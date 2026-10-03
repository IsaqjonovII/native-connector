# Auth and cloud link — spec for the WinUI app

Date: 2026-09-30. Read-only mapping. Nothing here was run against any AIBA server.

Note 2026-10-02: every `N:src/OneC.Sync/…` file, `StubBackend`, `SyncTests`, `SyncScheduler.cs` and the
`--sync-config` / `GET /v1/sync` named below belong to the milestone 5.9 sync engine, removed on
2026-10-02 (D48). The current Sync engine (D42) reuses those names for different code
(`SYNC_ENGINE_ARCHITECTURE.md`); its upload auth is the Python target with `OneC.Cloud` refresh
(§18 there). D39 is superseded by D42/D48.

## Sources (what "cited" means below)

| prefix | checkout | revision |
|---|---|---|
| `C:` | `D:\aiba\connector\apps\app\` | branch `release` @ `503addf` (1.3.107) |
| `B:` | `D:\aiba\backend\backend` (main API, api.aiba.group) | `origin/production` @ `2e763d7f`. **Local ref dated 2026-07-15, not fetched** — prod may be newer (see risks) |
| `O:` | `D:\aiba\backend\1c` (1c.aiba.group) | `origin/production` @ `608d4e6` (2026-09-30) |
| `N:` | `D:\aiba\1c-arch` | `master` @ `f81489e` + uncommitted plan/status edits |

Anything not read in code is marked **(inferred)**.

## Summary

- **Login = phone + password only.** Form POST `https://api.aiba.group/api/v1/auth/login`. No OTP, no captcha, no E-IMZO, no SSO in the connector. Returns `access_token` (JWT HS256, 1 day) + `refresh_token` (30 days).
- **Refresh = on 401 only**, `POST /auth/refresh {refresh_token}`. The refresh token is not rotated. Logout is local only.
- **Env = runtime toggle** (Ctrl+Shift+D), default `prod`, persisted. Switching clears the session.
- **Companies = `GET /api/v1/company/`**, paged. No company or tenant header anywhere. Company goes in bodies/query as `companyId`.
- **Link a base = `POST https://1c.aiba.group/api/v2/onec`** with `{userId, companyId, name, odataName, provider, version}`. The returned `id` is the **oneCId** used by every later call (upload, status, WS).
- **"Connected" in the cloud has two meanings:**
  1. New UIs paint `connection_state` from a Redis key that only the **connector WebSocket** sets (`wss://1c.aiba.group/api/v2/connector/ws`, `hello` + `heartbeat` every 30 s, 90 s TTL, **no auth on the socket**).
  2. Legacy consumers read Mongo `status`, kept `active` by `PATCH /api/v2/onec/connection/{id}` every 60 s (demoted after 3 min).
- **Upload** = multipart `POST /api/v2/entity/upload` (`oneCId` + `file`), user JWT Bearer, nothing else. `N:UploadTarget.cs` already matches the wire format. Missing: live token + refresh, oneCId from the link, stopping on 409/403.

---

## 1. Login

### What the user does
Phone + password form, nothing else (`C:src/app/(auth)/login/page.tsx:44-67`, fields `113-201`). Hint text: use the phone and password of the "AI yordamchi" mobile app (`page.tsx:95`).

Client-side checks before sending (`C:src/schemas/auth.ts:3-19`): phone 5–32 chars; password 8–64 chars with upper, lower, digit, and one of `!@#$%^&*(),.?":{}|<>`. Phone widget is `react-phone-number-input`, `international`, default `UZ` (`C:src/components/ui/phone-input.tsx:4,23-31`) — it emits E.164 like `+998901234567` (inferred from the library).

### Request
```
POST {API}/auth/login            API = https://api.aiba.group/api/v1
Content-Type: application/x-www-form-urlencoded
X-Device-Id: <device id>         (added by the axios interceptor)

phone=+998…&password=…&is_socket_device=false
```
`C:src/utils/login.ts:70-79`, interceptor `C:src/services/api.ts:53-66`.

Backend (`B:app/api/v1/auth.py:565-614`): `phone`, `password` are `Form(...)`. `is_socket_device` is a plain function arg, so FastAPI reads it from the **query**, not the form — the form field is ignored (inferred from FastAPI rules; value is false either way). reCAPTCHA is commented out (`auth.py:570-578`). Rate limit **5/minute per client IP** (`auth.py:566`, `B:app/core/limiter.py:14`, 429 handler `B:app/main.py:303-304`).

Phone is normalised with `phonenumbers` to E.164, default region `US` (`B:app/utils/sanitization.py:32-41`) — so a UZ number **must** carry `+998`.

Optional headers the backend records on the session row (`B:app/utils/auth.py:120-195`): `Client-Device` (must be `android|ios|web`, default `web`, anything else → 400 `auth.client_device_error`, `B:auth.py:105-107`, `B:app/models/enums.py:72-79`), `User-Agent`, `App-Version`, `X-Location`, `X-MAC-Address`, `X-User-Agent` (`aiba-connector` → device name "AIBA Connector"). The connector sends none of these (`X-User-Agent` is commented out, `C:services/api.ts:60`). A new-login notification is sent for every non-socket login (`B:utils/auth.py:185-191`).

### Response
`200 {access_token, refresh_token, token_type:"bearer"}` (`B:app/schemas/auth.py:87-90`).
- JWT HS256, claims `sub` (user id), `exp`, `iat`, `jti` (`B:app/utils/auth.py:71-88`).
- Access lifetime **1 day**, refresh **30 days** (`B:app/core/config.py:168-171`).

Then the connector calls `GET {API}/user/me` with the new Bearer (`C:utils/login.ts:81-86`; backend `B:app/api/v1/user.py:94-105`) and keeps only `id, first_name, last_name, avatar.url` (`login.ts:88-99`, type `C:src/types/auth.ts:1-15`).

### Errors the UI handles (`C:utils/login.ts:113-138`, `C:services/api.ts:73-81`)
- body has `errors[]` → one toast per `errors[i].message`;
- else `detail` → toast with the raw `detail` string;
- else generic "Tizimga kirishda xatolik yuz berdi!".

Backend details: `400 auth.user_not_found`, `401 auth.password_incorrect`, `400 auth.invalid_phone_number`, `429` from the limiter (body shape is slowapi's default — inferred). Details are i18n keys, shown raw.

Side effect to avoid: a `401` on login also runs the global 401 handler (refresh attempt → clear auth → go to `/login`) (`C:services/api.ts:84-107`). Harmless there; the new app should not run refresh for `/auth/*` calls.

### Not used by the connector
Backend also has OTP (register/forgot only, `B:auth.py:281-341`), QR login (`824-918`), Telegram (`957`), passkeys (`997-1197`). None are called from `C:`.

### Alternate target (aiba-next)
If the API base contains `/api/v2`, the connector switches to aiba-next: JSON `POST /auth/login {username,password}` → `{access_token, token_type, username, display_name}`, `GET /me`, `GET /me/companies {items,count}`, no refresh (12 h token per comment) (`C:utils/login.ts:35-68`, `C:services/api.ts:41-51`, `C:utils/company.ts:47-71`). Only reachable through a Dev Settings URL override; prod uses `/api/v1`.

---

## 2. Token lifecycle

| topic | old connector | cite |
|---|---|---|
| store | zustand → `localStorage["aiba-auth"]`, **plain JSON** (access + refresh + profile + appMode) | `C:src/stores/auth.ts:14-36` |
| attach | `Authorization: Bearer <access>` + `X-Device-Id` on every axios call | `C:services/api.ts:53-66` |
| refresh when | only after a 401 (once per request, `_retry`) | `C:services/api.ts:83-95` |
| refresh call | `POST {API}/auth/refresh` JSON `{refresh_token}` → reads `access_token`, `refresh_token` | `C:services/api.ts:122-152` |
| refresh backend | returns `{access_token, refresh_token}` — **same refresh token back**; 401 `auth.invalid_refresh_token`; 30/minute | `B:auth.py:655-674`, validity `B:app/models/user.py:189-191` |
| refresh fails | clear auth, navigate to `/login` | `C:services/api.ts:97-106` |
| idle keepalive | `GET /user/me` every 10 min so a 401 → refresh happens even when the UI is idle | `C:src/lib/aiba-token-keepalive.ts:51,103-114` |
| startup | if stored token + mode match: `GET /user/me`; **any** failure (even network) → clear auth | `C:src/providers/auth.tsx:118-191` (clear at `173`) |
| logout | local only: clear stores, hard reload to `/login`. **Never calls** `POST /auth/logout` | `C:providers/auth.tsx:101-116` |
| backend logout | `POST /auth/logout {refresh_token, fsm_token}` + Bearer; marks session logged out | `B:auth.py:781-813`, `B:app/schemas/auth.py:206-208` |
| multi-account | none. One user; valid only for the `appMode` it logged in under | `C:providers/auth.tsx:61` |
| sync token | read from the store at the **start** of each sync run; uploads use `fetch`, so no refresh on a 401 mid-run | `C:features/accounting/lib/sync/sync-poller.ts:377`, `chunk-uploader.ts:260-272` |

Session checks on the main API (`B:auth.py:103-177`): a JWT whose `jti` belongs to a logged-out session → 401 `auth.session_logged_out`.

backend/1c checks the same JWT **locally** (shared secret, signature + `exp` only) (`O:app/dependencies/auth.py:29-65`). So a logged-out but unexpired token still works on 1c.aiba.group (inferred from that code). A service secret (`X-Service-Secret`) passes `get_current_user` there but has no user id, so ownership checks refuse it (`O:app/dependencies/auth.py:50-51,67-88`).

Dead path: `providers/auth.tsx:209-237` listens for a Tauri `"unauthorized"` event; nothing emits it (grep of `C:src` and `C:src-tauri/src`).

---

## 3. Environment

- Mode is `"prod" | "dev"`, **default `prod`** (`C:src/stores/app.ts:15,48`).
- Toggle: **Ctrl/Cmd+Shift+D** (`C:src/providers/mode-switcher.tsx:63-73`) → clear auth → set mode → hard navigate to `/login` (`42-61`). A blue "DEV MODE" badge shows in dev (`20-31`).
- Persisted: only `appMode` + `deviceId` in `localStorage["aiba-connector-store"]`; URLs recomputed on load (`C:stores/app.ts:83-103`).
- Overrides: any URL can be overridden per key in `localStorage["dev_settings"]`, override wins over env (`C:src/utils/dev-settings.ts:3,102-135`). Values are read at module load, so a change needs a reload (inferred from module-level constants in `C:src/core/config.ts`).
- Sentry is off in dev mode (`C:src/lib/observability/sentry.ts:68-79`).

| | prod | dev |
|---|---|---|
| main API | `https://api.aiba.group/api/v1` | `https://api-dev.aiba.uz/api/v1` |
| 1C API | `https://1c.aiba.group/api/v2` (and `/api/v1` for counts/prune/sync-tables) | `https://aiba-1c-dev.aiba.uz/api/v2` |
| connector WS | `wss://1c.aiba.group/api/v2/connector/ws` | `wss://aiba-1c-dev.aiba.uz/api/v2/connector/ws` |

Sources: `C:.env.production:3-14`, `C:core/config.ts:154-161,180-195`. The WS URL is **derived from the 1C API host** (`scheme→ws/wss`, path forced to `/api/v2/connector/ws`); `NEXT_PUBLIC_ONEC_SYNC_SOCKET_URL` is only a fallback (`C:src/providers/onec-hub.tsx:33-51`, `C:src/providers/onec-sync-provider.tsx:1115-1141`). Bank/Didox/tunnel URLs exist but are out of scope (D40).

---

## 4. After login: companies

- Query runs only with a token for the current mode (`C:src/hooks/queries/companies.ts:9-25`).
- `GET {API}/company/?pageNumber=N&pageSize=50&page=N&size=50`, loop until `pages` (`C:src/utils/company.ts:37-136`).
- Backend (`B:app/api/v1/company.py:589-702`): companies the user **owns** + companies from **accepted invites**. Page params read from `pageNumber|page`, `pageSize|size` (default 10). Response `{results, count, page, size, pages}`; each result has `id, name, form, inn, pinfl, director_pinfl, email, logo{…url}, reg_date, certificate_id, is_default, isYaTT, parent_company_id, membership_type, role` + enrichment.
- Kept locally: `id, name, form, inn, email, logoUrl, isDefault, appMode` (`C:src/types/company.ts:6-15`, map `C:utils/company.ts:21-35`), persisted in `localStorage["aiba-companies"]` with `selectedCompany` (`C:src/stores/company.ts:17-45`).
- Selected company: keep the current one if still listed, else `is_default`, else first (`C:src/providers/seed.tsx:94-103`).
- **No company/tenant header.** Only `Authorization` + `X-Device-Id` are ever added (`C:services/api.ts:53-66`). `onec_company_key`, `X-Company*`, `X-Tenant*` do not appear in `C:src` (grep). `X-Device-Id` only feeds the main API audit log (`B:app/core/audit/service.py:56`).
- Device id = `machine_uid` crate (`C:src-tauri/src/utils/fingerprint.rs:1-7`), which on Windows is the **MachineGuid** (`C:providers/onec-sync-provider.tsx:196` says `${user_id}:${MachineGuid}`), cached in the app store (`C:src/utils/device-id.ts:4-14`).

---

## 5. Connecting a 1C base to the cloud

### 5.1 Link (REST)
Old flow (`C:src/features/accounting/lib/use-info-base-form.ts:322-437`):
1. Needs a logged-in user and a selected company (`328-337`).
2. Reads the 1C configuration version from the local adapter, normalised to `major.minor` (`340-347`, `C:src/utils/onec-version.ts:201-217`).
3. `POST {1C}/onec` (`349-362`):
   ```json
   { "userId": "<user id>", "companyId": "<company id>", "name": "<display name>",
     "odataName": "<adapter db name>", "adapterDbName": "…", "adapterDbType": "file|server",
     "adapterDbPath": "…", "adapterDbServer": "…", "adapterDbDatabase": "…",
     "provider": "unisoft|venkon", "version": "3.0" }
   ```
   Provider comes from a radio, default `unisoft` (`C:src/features/accounting/ui/info-base-form/form.tsx:198-199,353-400`).
4. `oneCId = response.id` (or `_id`, or first array item) (`C:features/accounting/lib/utils.ts:21-47`).
5. Adds credentials to the local adapter config and restarts it (`use-info-base-form.ts:143-171,392-424`) — in the new app that is `BaseStore` + supervisor restart (D31).
6. If credentials were given: `PATCH …/onec/connection/{oneCId} {status:"running"}` (`426-433`).

Backend (`O:app/api/v2/routes/onec.py:77-94`):
- Stored fields are only `userId, provider (^(unisoft|venkon)$), odataName, companyId, companyInn, name, version, totalCount` (`O:app/schemas/mongodb_schemas.py:8-16`). The `adapterDb*` fields are dropped (inferred: pydantic default ignores extra keys).
- `userId` must equal the JWT `sub` → else 403; duplicate `(companyId, odataName, name)` → 409 (`O:app/utils/onec.py:336-351`). The old app treats 409 "already exists" as success only for 1uz (`use-info-base-form.ts:42-48,274-294`).
- Response `OneCResponse` (`mongodb_schemas.py:33-77`): `id, userId, provider, odataName, companyId, name, version, status, totalCount, connectionId, orgRef, orgName, orgCode, isShared, statusUpdatedAt, connection_state, can_write, state_reason`.

Re-list (the source of truth afterwards): `GET {1C}/onec?page=N&size=50&companyId=<id>` per company, every 30 s (`C:src/utils/info-base.ts:72-95`, `C:src/hooks/queries/infobases.ts:34-56`). Response is fastapi-pagination `{items,total,page,size,pages}` (`O:onec.py:97-103`). Rows with `status:"deleting"` are dropped locally (`info-base.ts:217`). If the local version/name differs, the app fire-and-forgets `PATCH …/onec/connection/{id} {userId, companyId, adapterDbName, odataName, version}` (`info-base.ts:255-279`).

Local match between a cloud record and a local base: by `adapterDbName`/`odataName`, lower-cased, whitespace removed (`C:src/providers/connection-status.tsx:30-34`, `onec-sync-provider.tsx:1186-1192,1274-1275`).

Multi-org (one base, many companies): records with `connectionId` are org bindings, never synced themselves; `isShared` marks the connection (`C:src/types/response.ts:74-83`, `C:hooks/queries/infobases.ts:78-108`, `GET /onec/{id}` `C:utils/info-base.ts:378-433`). **Out of the minimal plan.**

### 5.2 Status heartbeat (REST) — drives Mongo `status`
`PATCH {1C}/onec/connection/{oneCId}` body `{status, totalCount?}`, Bearer (`C:providers/connection-status.tsx:111-159`).
- Every 60 s, batches of 20, for "confirmed" bases: `"running"` if syncing else `"active"` (`16-21,191-279`).
- A base becomes confirmed after an `active` PATCH succeeds: at startup for bases with local credentials (`359-399`) and whenever the adapter reports the base connected (`410-479`).
- On quit from tray: `"inactive"` for all confirmed bases (`282-336`).

Backend: ownership check, 409 if `deleting` (`O:onec.py:138-169`); `OneCUpdate.status` pattern `active|inactive|running|deleting` (`mongodb_schemas.py:19-30`); every status write stamps `statusUpdatedAt` (`O:app/repositories/mongodb_repository.py:188-206`); a Celery job demotes to `inactive` when `statusUpdatedAt` is older than **3 min** (`O:app/core/celery/api.py:2459-2480`).

### 5.3 Connector WebSocket hub — drives `connection_state` and carries commands

**URL**: `wss://1c.aiba.group/api/v2/connector/ws` (`O:main.py:101`, route `O:app/api/connector_ws.py:99`).
**Auth on connect: none.** Identity is self-declared in `hello` (`O:connector_ws.py:9-11`, `C:providers/onec-hub.tsx:23`). No token in the URL.

**Connector id** = `"{userId}:{deviceId}"` (`C:onec-sync-provider.tsx:1456-1464,1476-1487`; server fallback `O:connector_ws.py:88-96`).

**hello** (sent on open, `C:onec-sync-provider.tsx:15244-15255`, built `1643-1742`):
```json
{
  "action": "hello", "type": "hello", "protocol_version": 1,
  "timestamp": "<ISO>", "reason": "socket_open",
  "client_id": "<per-launch uuid>",
  "connector": { …identity… },
  "connector_id": "<userId>:<deviceId>", "tenant_id": "default",
  "user_id": "<userId>", "device_id": "<MachineGuid>", "session_id": "<uuid>",
  "app_version": "1.3.107", "app_mode": "prod", "role": "admin",
  "connected_base_count": 1,
  "bases": [{
    "base_key": "<oneCId>", "infobase_id": "<oneCId>", "name": "<adapter db name>",
    "status": "connected",
    "company_ids": ["<companyId>"], "mapped_info_base_ids": ["<oneCId>"]
  }],
  "message_type": "connector_hello",
  "capabilities": { "commands": ["write_to_1c", "…"], "resources": ["bases","refdata","holidays"] },
  "state": { "connected_base_count": 1, "bases": [ … ], "updated_at": "<ISO>" },
  "ready": true
}
```
Base list rules (`C:onec-sync-provider.tsx:1169-1345`): one entry per linked base; `status` is the local adapter's status for that db (`connected` only if the adapter process is up and reports the db connected — `C:src/stores/adapter.ts:29-57`, `C:src/utils/adapter-databases.ts:62`), else `not_connected`; unlinked local dbs are also sent as `db_<slug>` with empty `company_ids`.

**heartbeat**: same envelope with `action:"heartbeat"` and the current `bases`, every **30 s** (env override, min 10 s) (`C:onec-sync-provider.tsx:70-74,15167-15212`). **state_update**: same, sent when the base list changes (`15372-15414`).

**Server side** (`O:app/core/connector_hub.py`):
- `hello`/`state_update`/`ready` → `register_connector`; `heartbeat`/`ping` → `heartbeat()` (`O:connector_ws.py:133-180`).
- Replies: `{"action":"hello_ack","connectorId","ts"}`, `{"action":"heartbeat_ack","ts"}`, `{"action":"need_hello"}` when the record is unknown/aged out, `{"action":"error","error":"invalid_json"}` (`O:connector_ws.py:116,155-180`).
- Presence hash + online zset, TTL **90 s** (`connector_hub.py:35,327-331,394-397`).
- Routing key `conn-of-infobase:{oneCId}` = connectorId, TTL 90 s, **only for bases whose status is `connected|ready|ok`** (`connector_hub.py:87-91,211-227`). Ids come from `mapped_info_base_ids`, `oneCId`, `id`, `infobase_id` (24-hex ObjectIds preferred) (`connector_hub.py:150-205`).
- A beat's own `bases` list replaces the stored one; absent list = "no claim" (`connector_hub.py:94-107,343-403`).
- `capabilities.commands` is stored and used to gate some commands (`connector_hub.py:317-323,481-501`).
- On socket close: presence + routing keys deleted (`connector_hub.py:406-430`).

**Commands from the cloud** (`dispatch_to_infobase`, `O:connector_hub.py:587-672`), published to the socket's worker and relayed down (`O:connector_ws.py:56-85`):
```json
{ "action": "<name>", "requestId": "<hex>", "request_id": "<hex>",
  "oneCId": "<id>", "onec_id": "<id>", …payload }
```
No connector → `{ok:false, error:"connector_offline"}`; no reply in time → `{ok:false, error:"timeout"}`. Timeouts: 120 s default, 300 s for writes (`connector_hub.py:40,51`).

**Connector replies**: anything with `requestId`/`request_id` whose action is `result`, `ack`, `*_result`, `*_ack` or `*_progress` is forwarded to the waiter (`O:connector_ws.py:189-197`); `*_progress` is non-terminal (`connector_hub.py:578-584`).
- Result shape: `{action:"<cmd>_result", request_id, requestId, oneCId, onec_id, timestamp, ok|success, …}` (`C:onec-sync-provider.tsx:1790-1803`).
- Write liveness: `{action:"write_progress", request_id, client_id, stage, timestamp}` every 10 s while a write runs (`C:onec-sync-provider.tsx:122-130,1548-1641`).
- `ping`/`server_ping` → `{action:"server_pong", request_id, status:"ok"}`; `get_bases` → `get_bases_result` with `connector_id, connected_base_count, bases`; `status_request` → a `state_update` (`C:onec-sync-provider.tsx:1761-1788,14875-14888,15094-15100`).
- **Trap:** the connector's generic `{action:"error", request_id, error:{code,message}}` (`1744-1759`) is **not** forwarded by the hub (not in the list above), so the caller waits out the full timeout. The new app must answer every command as `<action>_result` with `ok:false`.

Commands the old connector handles (`C:onec-sync-provider.tsx:14870-15103`): `ping, get_bases, sync_now, pull_now, check_exists, delete_from_1c, employee_write_to_1c, write_to_1c, post_1c_document, unpost_1c_document, update_1c_document, delete_1c_document, push_to_1c, department_write_to_1c, list_resource, holiday_*, department_delete_in_1c, refdata_get, get_list, create_ref, catalog_update, document_read, catalog_read, employee_fire_from_1c, status_request`.

Client behaviour worth copying: reconnect backoff 1 s·2ⁿ up to 30 s + 0–600 ms jitter (`15105-15125`); treat "no inbound frame for 3 beats" as a dead socket and reconnect (`15199-15209`); close the socket on shutdown (`15341-15360`).

Old-app quirk not to copy: it opens **two** sockets under the same connectorId (presence-only `onec-hub.tsx` + `onec-sync-provider.tsx`), both subscribed to the same command channel (`C:onec-hub.tsx:161-184`), and the sync socket ignores `need_hello`. One socket is enough; handle `need_hello` by re-sending `hello`.

### 5.4 What the cloud needs, per goal

| goal | needed | cite |
|---|---|---|
| base **listed** under the company | the `POST /onec` record | `O:onec.py:77-103` |
| base **shown connected** in new UIs (`connection_state: writable`, `can_write: true`) | WS `hello` + `heartbeat` ≤ 90 s apart, base advertised with `status:"connected"` and its oneCId | `O:app/services/connection_state.py` (whole file); `mongodb_schemas.py:65-76` |
| base shown active to status-based consumers (e.g. avtoprovodka picker per comment) | `PATCH /onec/connection/{id} {status:"active"}` at least every 3 min | `O:celery/api.py:2459-2490`, `connection_state.py` docstring |
| "syncing" badge | any upload; the backend sets a 120 s Redis key + `status:"running"` itself | `O:app/services/connection_state.py:26-34,144-163`; `O:app/api/v2/routes/entity.py:344-345` |
| **sync/upload** | only a user JWT + oneCId. No WS needed | `O:entity.py:308-345` |
| **writes/reads from the cloud** | WS presence **and** a handler that answers each command | `O:connector_hub.py:587-672` |

---

## 6. Upload of synced data

### Old connector contract
```
POST {1C}/entity/upload                 1C = https://1c.aiba.group/api/v2
Authorization: Bearer <user access token>
Content-Type: multipart/form-data
  oneCId = <oneCId>
  file   = infoBase-<id>-chunk-<n>.json   (application/json; gzip only if GZIP_UPLOAD=1, off by default)
           body: { "<TableName>": [ rows… ] }
```
- Code: `C:src/features/accounting/lib/sync/chunk-uploader.ts:157-272`; gzip flag `C:core/config.ts:36-42`; errors `399-466` (413 → split in half, recurse ≤ 10; network → 5 retries 1/2/3/5/8 s; other 4xx/5xx → throw). Only `Authorization` is sent — no `X-Device-Id`, no company header (`260-272`).
- Target base URL chosen per provider + mode (`C:sync-orchestrator.ts:583-590`). Company is **not** sent; the partition is `oneCId` only. Multi-org files each org's rows under that binding's oneCId (`sync-orchestrator.ts:509-579,1283-1330`).
- Related v1 routes on the same host (`/api/v2` → `/api/v1`): `GET /onec/{id}/counts?scope=connection`, `POST /onec/{id}/prune-missing?scope=connection`, `POST /onec/{id}/reconcile-recorder?scope=connection`, `GET|PUT /onec/{id}/sync-tables` (`C:src/utils/backend-counts-fetcher.ts:20-32`, `C:src/utils/backend-refids-fetcher.ts:11-20,97,235`, `C:src/utils/sync-tables-config-fetcher.ts:18-19,32-62`).

Backend (`O:app/api/v2/routes/entity.py:308-391`): ownership via user id or company access (`322`; `O:app/dependencies/auth.py:67-88`) → a service secret gets 403; `status == deleting` → **409** "This 1C connection is being deleted" (`333-337`); marks syncing (`344-345`); body must be UTF-8 JSON object (`348-364`); tables must be valid for the record's **provider** or 400 "Check table names match your provider" (`374-391`).

### Gaps in `N:src/OneC.Sync/UploadTarget.cs` (and around it) — 5.9 engine, removed 2026-10-02 (D48)

Already the same as the connector: multipart `oneCId` + `file` with a filename, plain JSON, Bearer only, 413 split, 5 retries on network/5xx, v1 paths with `scope=connection` for counts/prune/reconcile (`UploadTarget.cs:39-155`).

| # | gap | where | fix |
|---|---|---|---|
| 1 | Token is a static string from a JSON file on disk, never refreshed | `N:SyncScheduler.cs:16-17`, `N:src/OneC.Supervisor/Program.cs:71-80` | token provider fed from memory (see plan d); no token in any file |
| 2 | 401 goes straight to the caller as `UploadException` | `UploadTarget.cs:141-155` | on 401: ask for a fresh token once, retry once, then report `auth_expired` |
| 3 | `oneCId` is hand-written in `--sync-config` | `SyncScheduler.cs:6` | take it from the link (`POST /onec` → `id`) |
| 4 | 409 "being deleted" and 403 back off forever (2 s … 300 s) | `SyncScheduler.cs:81-87` | stop that base, mark the link dead, re-read the list |
| 5 | Provider not known; wrong provider → 400 on every chunk | `N:src/OneC.Desktop.Core/BaseStore.cs:10-20` has no provider | store provider in the link; table names per provider |
| 6 | Table list static; the backend owns a per-connection list | `SyncBaseConfig.Tables` vs `sync-tables-config-fetcher.ts:32-62` | optional: `GET /api/v1/onec/{id}/sync-tables`, fall back to local defaults |
| 7 | No status PATCH around passes (connector: running/active + totalCount) | `C:sync-orchestrator.ts:691,979,1172,3140` | covered by the 60 s status heartbeat; totalCount optional |
| 8 | Multi-org partitions, bindings, `isShared` refusal | `N:MIGRATION_STATUS.md` §5.9 | out of minimal scope; refuse to sync a record with `connectionId` or `isShared` until done |
| 9 | Desktop never starts sync (`--sync-config` not passed) | `N:src/OneC.Desktop.Core/SupervisorProcess.cs:91` | plan d |

---

## 7. Security

- **Tokens at rest:** old app keeps access + refresh tokens as plain JSON in WebView `localStorage` (`C:stores/auth.ts:31-34`), and 1C usernames/passwords in `localStorage["aiba-infobases"]` (`C:src/stores/infobase.ts:129-136`). The new app must keep access + refresh tokens **only DPAPI-protected** (same scheme as `N:BaseStore.cs:88-96`), and never write the login password anywhere.
- **Token in files:** `N:SyncScheduler.cs:16-17` reads the Bearer from a config file. Remove that path for real targets.
- **Token in URL:** none for auth or WS (`C:onec-hub.tsx:33-51`). The tunnel relay uses `?token=` with the connector's own token (`C:core/config.ts:218-222`) — out of scope; do not copy the pattern.
- **Logs:** `console.error(error)` on a failed login (`C:utils/login.ts:114`) ships an AxiosError (with request config) to Sentry via `captureConsoleIntegration` (`C:lib/observability/sentry.ts:112`). The scrubber matches `password=`/`token` keys (`sentry.ts:125-175`) but not an `Authorization: Bearer …` header (inferred leak path). New app: never log request bodies of `/auth/*`, never log `Authorization`, scrub `Bearer <jwt>` and `access_token`/`refresh_token` everywhere.
- **Profile secrets:** `/user/me` returns `socials[].access_token/refresh_token` (third-party OAuth) (`C:src/types/response.ts:17-28`). Read `id` and names only; never store or log the raw body.
- **TLS:** all real URLs are https/wss. Refuse plain http/ws except loopback (the stub).
- **Unauthenticated WS (prod, existing):** anyone who can reach the hub can `hello` as any `userId:deviceId` and claim any oneCId, taking that base's write route (`O:connector_ws.py:9-11,133-159`; `connector_hub.py:331`). Not ours to fix here; do not rely on the socket for trust.
- **Cross-tenant read (prod, existing):** `GET /api/v2/onec` has no ownership check; with no `companyId` it returns every tenant's connections (`O:onec.py:97-103`, `O:mongodb_repository.py:137-171`). `POST /api/v2/onec/connection/backfill-total-count` and `/connection/rebuild` have no auth at all (`O:onec.py:234-256`).
- **Logout does not revoke on backend/1c:** it checks signature + exp only (`O:dependencies/auth.py:29-43`). Delete tokens locally on logout; call `POST /auth/logout` too (the old app never does).

---

## 8. Minimal build plan (new app)

Placement (proposal — record as a new decision): **Desktop owns login, refresh and the DPAPI token store** (it has the UI). **Supervisor owns cloud presence, status heartbeat and upload** (it already owns base health and sync, D39, and will own cloud commands, D38). Desktop pushes the current access token + link map to the supervisor over the loopback edge; the supervisor keeps it in memory only. This removes the token file and keeps one token holder for uploads. Note: the supervisor dies with the desktop (`N:SupervisorProcess.cs:14-15`), so closing the app = offline in the cloud (old app lives in the tray).

New shared project `src/OneC.Cloud` (net9.0, no COM, referenced by Desktop.Core and Supervisor). New dev-only project `src/OneC.CloudStub` (loopback Kestrel) used by tests and by the desktop in a "Local stub" environment. Existing `OneC.Sync/StubBackend.cs` keeps the upload routes; `CloudStub` hosts it or sits beside it.

### (a) Login + token store + env switch
Add
- `src/OneC.Cloud/CloudEnvironment.cs` — `Prod`, `Dev`, `LocalStub`: `ApiBase` (`…/api/v1`), `OneCBase` (`…/api/v2`), `WsUrl` derived from the OneC host exactly as `C:onec-hub.tsx:33-51`. Default `Prod`. Optional overrides file `%LOCALAPPDATA%\AIBA\Connector\cloud.json` (no secrets in it).
- `src/OneC.Cloud/AuthClient.cs` — `LoginAsync(phone, password)` (form POST, E.164 phone), `MeAsync`, `RefreshAsync`, `LogoutAsync` (`{refresh_token, fsm_token:""}` — see open question 6). Sends `Client-Device: web` and `X-User-Agent: AIBA-Connector` (optional, gives the session a readable name).
- `src/OneC.Cloud/TokenStore.cs` — `%LOCALAPPDATA%\AIBA\Connector\session.json`: `{env, userId, phone, firstName, lastName, access (DPAPI), refresh (DPAPI), savedUtc}`. Wiped on env switch and logout.
- `src/OneC.Cloud/AuthHandler.cs` — `DelegatingHandler`: adds Bearer + `X-Device-Id`; on 401 does one single-flight refresh and one retry; skips `/auth/*`; refresh 401 → `SignedOut` event.
- `src/OneC.Cloud/DeviceId.cs` — read `HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid` (read-only).
- `src/OneC.Desktop/Views/LoginPage.xaml(.cs)`; env picker in settings (+ Ctrl+Shift+D); `MainWindow` shows login until a session exists.

Test without prod
- `CloudStub` routes: `POST /api/v1/auth/login` (form; `+998…` only; 400 `auth.user_not_found`, 401 `auth.password_incorrect`, 400 `auth.invalid_phone_number`, 429 after 5/min), `POST /api/v1/auth/refresh` (same refresh token back; 401 `auth.invalid_refresh_token`), `POST /api/v1/auth/logout`, `GET /api/v1/user/me` (includes a fake `socials[].access_token` to prove it is never stored/logged). Short-lived fake JWTs (HS256, stub key) so expiry can be forced.
- `tests/OneC.Tests/CloudAuthTests.cs`: login ok/fail cases; 401 → one refresh → retry; refresh 401 → signed out; session.json holds no readable token (search the file for the token string); env switch wipes session; log output contains no `Bearer ` / token text.

### (b) Company list + link infobase → company
Add
- `src/OneC.Cloud/CompanyClient.cs` — `GET {ApiBase}/company/?pageNumber&pageSize=50&page&size=50` until `pages`; keep `id, name, form, inn, isDefault`; default selection rule from `C:seed.tsx:94-103`.
- `src/OneC.Cloud/OneCLinkClient.cs` — `ListAsync(companyId)` (`GET {OneCBase}/onec?page&size=50&companyId`), `CreateAsync` (`POST {OneCBase}/onec {userId, companyId, name, odataName, provider, version}`), `GetAsync(id)`, `PatchAsync(id, …)`.
- `src/OneC.Desktop.Core/LinkStore.cs` — `%LOCALAPPDATA%\AIBA\Connector\links.json`, keyed `(env, userId, baseName)` → `{oneCId, companyId, companyName, provider, odataName, version}`. Separate from `BaseStore` because links are per env and per user, bases are per machine.
- Desktop: company picker + "Link to cloud" on `InfobasesPage`; provider radio `unisoft|venkon`; version from the edge `GET /v1/bases/{b}/version` → `major.minor`; `odataName` = the base's name as the old adapter knew it (see open question 4). Before creating, list the company's records and reuse one whose `odataName` matches (avoids the 409).
- Periodic re-list (30 s like `C:hooks/queries/infobases.ts:52-54`): drop links whose record is gone or `deleting`.

Test without prod
- `CloudStub`: `GET/POST /api/v2/onec`, `GET /api/v2/onec/{id}`, `PATCH /api/v2/onec/connection/{id}` with the real rules: 403 when `userId != sub`, 409 on duplicate `(companyId, odataName, name)`, provider pattern, 409 on PATCH while `deleting`, fastapi-pagination shape.
- `tests/OneC.Tests/CloudLinkTests.cs`: paging; create; duplicate → reuse existing; delete in stub → link dropped on next re-list; `links.json` contains no token.

### (c) Show the base as connected
Add
- `src/OneC.Cloud/PresenceClient.cs` — one `ClientWebSocket` to `WsUrl`. `hello` on open and on every link/health change; `heartbeat` every 30 s with the current `bases`; `need_hello` → `hello`; no inbound for 90 s → reconnect; backoff as §5.3. Base `status:"connected"` only when the supervisor has the base placed on a running host and its last Connect/test succeeded; else `not_connected`. Identity per §5.3 (see open question 2 about the device id).
- Command handling (minimal): `ping` → `server_pong`; `get_bases` / `list_resource{bases}` → bases result; `status_request` → `state_update`; **everything else → immediate `<action>_result {ok:false, success:false, error:"unsupported_action"}`**. Advertise in `capabilities.commands` only what is implemented. Later: map `write_to_1c`/`post_1c_document`/… onto the edge write routes (D38), `sync_now` onto the scheduler.
- `src/OneC.Cloud/StatusHeartbeat.cs` — every 60 s `PATCH …/onec/connection/{id} {status:"active"}` for linked + healthy bases (`"running"` during a pass); `"inactive"` on clean shutdown.
- `src/OneC.Supervisor/EdgeServer.cs` — new `PUT /v1/cloud/session` (behind the existing `X-AIBA-Token`, `EdgeServer.cs:75-79`): `{env, apiBase, oneCBase, wsUrl, userId, deviceId, accessToken, links:[…]}`; `DELETE /v1/cloud/session` on logout; `GET /v1/cloud` status (socket state, last ack, per-base advertised status).
- `src/OneC.Supervisor/Program.cs` — start `PresenceClient` + `StatusHeartbeat` when a session is pushed.
- `src/OneC.Desktop.Core/EdgeClient.cs` — `PutCloudSessionAsync`; re-push after every refresh and every link change.

Test without prod
- `CloudStub` WS hub on `/api/v2/connector/ws` mirroring `O:connector_ws.py` + `connector_hub.py`: `hello_ack`, `heartbeat_ack`, `need_hello` for unknown ids, route key only for `connected|ready|ok`, 90 s TTL on an injectable clock, `connection_state` computed like `O:services/connection_state.py` and returned on `GET /api/v2/onec/{id}`; a test hook to dispatch a command and await the reply.
- `tests/OneC.Tests/CloudPresenceTests.cs`: linked + healthy base → `writable`; base host down → `offline`; stub restart → `need_hello` → back to `writable`; no beats for 90 s (clock) → `offline`; dispatch `write_to_1c` → `write_to_1c_result ok:false` in < 1 s; status PATCH keeps `status:"active"` and stops on shutdown.

### (d) Sync upload with the token
(Targets the 5.9 engine, removed 2026-10-02, D48; superseded by the Sync engine's Python target.)

Change
- `src/OneC.Sync/UploadTarget.cs` — replace `Action<HttpRequestMessage> auth` with a token source (`Func<CancellationToken, ValueTask<string>> token` + `Func<CancellationToken, ValueTask<bool>> onUnauthorized`); on 401 call it once and retry once; throw typed `UploadGoneException` for 409 "being deleted" and `UploadForbiddenException` for 403.
- `src/OneC.Sync/SyncScheduler.cs` — bases from the pushed links (`oneCId`, provider, tables); `Token` no longer read from the file for real targets; on gone/forbidden: stop that base and report; on auth expiry: pause all bases with status `auth_expired` until a new session is pushed.
- `src/OneC.Supervisor/Program.cs` — build `HttpUploadTarget` with `BaseAddress` = the OneC host root (`https://1c.aiba.group/`) and the in-memory token from the cloud session; keep `--sync-config` only for the local stub (D39).
- Optional: `GET /api/v1/onec/{id}/sync-tables` for the table list; refuse records with `connectionId` or `isShared` until multi-org is ported.

Test without prod
- Existing `SyncTests` against `StubBackend` stay green.
- New cases: stub returns 401 once mid-run → token source refreshed → run completes, no rows lost; 409 deleting → base stops, others continue; 403 → base stops; no token string in any file under `%LOCALAPPDATA%\AIBA` or the sync state dir; log contains no `Bearer`.
- Going to a real backend is a separate, user-approved step: first `aiba-1c-dev.aiba.uz` with a test company, then prod. The agent does not log in anywhere.

---

## 9. Open questions / risks

1. **Real target** — backend/1c (`1c.aiba.group`) or aiba-next? D39 is still open. Everything above is backend/1c; aiba-next differs at login, `/me`, companies and has no refresh (§1).
2. **Running beside the old connector.** Same user + same MachineGuid → same connectorId: both sockets get every command and both execute it (`O:connector_ws.py:56-85`). Different device id → two connectors claim one base and the route flips to whoever beat last. Decide: never run both for the same base, and use a distinct id (e.g. `…:<MachineGuid>:next`) as a guard?
3. **Advertise "connected" before writes work?** Once a base is `connected`, the cloud routes writes/reads to this app. The plan answers `unsupported_action` fast, but users will see failures instead of "offline". Alternative: status heartbeat only (legacy `status`), no WS, until writes are wired.
4. **`odataName` for existing bases.** Cloud records hold the old adapter's db name; the new app's base names may differ. Link by picking an existing record (recommended) vs creating new ones (new partition = full re-upload).
5. **Client password policy on login** (`C:schemas/auth.ts:10-18`) blocks users with older weak passwords. Keep for parity or drop?
6. **Server logout** needs `fsm_token` (`B:schemas/auth.py:206-208`); the old app never calls it. Sending `""` should validate (inferred). Confirm it is wanted.
7. **Stale main-API ref.** `B:` was read from a local `origin/production` dated 2026-07-15 (not fetched, by rule). Re-check `auth.py` login/refresh on the current prod branch before shipping.
8. **Tray / lifetime.** Supervisor exits with the desktop; the old app stays alive in the tray. Needed for presence to stay green?
9. **Multi-org and 1uz** are out of the minimal plan. A multi-org connection must not be synced single-org (all rows land in the shared partition — `C:sync-orchestrator.ts:555-579`).
10. **Prod security findings (existing, not ours to fix here):** unauthenticated WS identity; `GET /api/v2/onec` cross-tenant listing; two unauthenticated POST maintenance routes (§7).
11. **Rate limits.** Login 5/min per IP — shared office NAT can lock users out; do not auto-retry login. Refresh 30/min — single-flight refresh is required.
