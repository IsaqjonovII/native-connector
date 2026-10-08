# Connector control plane — design proposal

STATUS: **PARKED / FUTURE PHASE** (developer, 2026-10-07). Kept as research. None of the
decisions P-1 … P-12 is approved; blocks C0–C9 are not scheduled. Current order is in
`MIGRATION_PLAN.md` (Sync → writes → Rust backend → prove Rust against 1C → Python deprecation →
managed Connector features). References to `cloud-os` below are historical evidence of old
caller behaviour only, not a future dependency.

Original status: PROPOSED, nothing built. Date 2026-10-07.

Scope: how the new Connector is reached and operated from the AIBA cloud — remote control
channel, remote reads, remote writes, presence and fleet, command durability, the Sync Table
Manifest, and remote diagnostics.

Inputs (facts, not re-derived here): `OLD_CONNECTOR_DEEP_AUDIT.md`, `OLD_CONNECTOR_SECOND_PASS.md`
(cited as **SP §n**), `OLD_CONNECTOR_FEATURE_MAP.md`, `SYNC_ENGINE_ARCHITECTURE.md` (cited as
**SE §n**), `IPC_CONTRACT.md`, `DECISIONS.md` D38, D41, D42, D46.

Nothing in this document changes a decision in `DECISIONS.md` until the developer approves the
proposed decisions in §13 (**P-1 … P-12**). Where a choice shapes the whole design, the options
are laid out with a recommendation.

---

## 1. What must be carried forward (from the evidence)

Only capabilities with a proven production caller or a proven correctness role (SP §14–16):

| Capability | Proven caller / role | Carry forward |
|---|---|---|
| Document create (`write_to_1c`) | Odoo inbox, cloud-os avtoprovodka, aiba-next HR (payroll, roster, timesheet), kansler-bank Prixod, cloud-os AI chat | **Yes** |
| Post an existing draft (`post_1c_document`) | admin route, aiba-next kansler-bank EDO "Post to 1C" | **Yes** |
| Update / unpost / delete document | admin route; MCP (unpost/delete denied by default) | **Yes**, soft delete only by default |
| Catalog compare-and-set update (`catalog_update`) | kansler ИКПУ audit; prod incident 2026-10-05 (74 cards blocked) | **Yes** — needs a new Host op |
| Catalog create-on-the-fly (`create_ref`), lists (`get_list`, `list_resource`, `refdata_get`) | HR and sotuv dropdowns | **Yes** — `create_ref` needs a new Host op; `_postCreateMethod` **never** |
| HR register writes (holidays push/delete/check) | cloud-os legacy HR | **Only if legacy HR survives** (P-9) |
| HR hire / fire documents | cloud-os legacy HR; aiba-next payroll send hits the same forced-exchange types | **Yes**, with the `exchange` question settled (P-8) |
| Bounded live reads (`document_read`, `catalog_read`) | EDO draft import | **Yes**, narrow |
| Raw read-only query (`adapter_read` QUERY) | backend/1c VAT registry / regulated reports, primary path | **Decision needed** (P-6) |
| Targeted pull (`sync_now`, `pull_now`) and "add table + pull now" | admin, Celery 03:00 report refresh, kansler GTD panel, aiba-next pull, MCP `onec_trigger_sync` | **Yes** |
| Sync-table list per connection | connector drawer, auto-seed, kansler GTD panel | **Yes**, fixed (§9) |
| Stock snapshot | aiba-next warehouse "1UZ jonli qoldiq" tab | **Yes** (revises D-7, P-11) |
| Data coverage | aiba-next `sotuv.rs::mirror_reach_from_onec` — duplicate-document guard in avtoprovodka | **Yes, required** (revises D-7, P-11) |
| Presence: who is online, which bases, which commands | write routing, capability gating, connector pickers | **Yes**, redesigned (§5) |

Not carried forward (evidence in §12): `push_to_1c`/`create_in_1c`/`delete_in_1c`/`delete_from_1c`,
`employee_list_from_1c`, `_postCreateMethod`, catalog hard delete, document hard delete from the
cloud, the bank-signer relay (D40), retail turnover rollup (pending one external check),
`request-tables`, the second presence socket.

---

## 2. Where the control plane lives

```
 AIBA cloud (backend/1c hub, Redis)            Customer PC
 ─────────────────────────────────            ──────────────────────────────────────────────
                                               OneC.Desktop (WinUI) ── loopback edge ──┐
   wss /api/v2/connector/ws  ◄──── one outbound WebSocket ────┐                         │
                                                              │                         ▼
                                               OneC.Supervisor ─────────────────────────────
                                                 ControlChannel ─► CommandRouter ─► Policy ─►
                                                      │                 │              │
                                                      │       CommandLog (SQLite)      │
                                                      │                                ▼
                                                      │        Host ops over pipes (read / write)
                                                      │        Sync engine (pull, manifest, status)
                                                      │        Diagnostics (status, logs, test)
                                                 Presence (hello / heartbeat / state)
                                               OneC.Host × N (1C COM)
```

- **The Supervisor owns the channel**, not the WinUI app. The Supervisor already owns the Host
  processes, the sync engine and the edge (D25, D28, D42); the old Connector put command handling
  in a 15 000-line React provider, which tied remote control to the window being open and to one
  route of the UI (audit A, tray-quit and two-socket findings). The desktop app only shows what the
  channel is doing, through the existing loopback edge.
- **One outbound WebSocket. Nothing listens.** The rewrite already binds the edge to loopback only
  (IPC §7, SE §25). The control channel is a client connection; no LAN port, no firewall rule, no
  inbound tunnel (contrast: the old adapter on `[::]:55899` with a firewall allow rule, and the
  relay tunnel — SP §9).
- **Auth to the cloud** uses the signed-in user's session from `OneC.Cloud` (DPAPI
  `session.json`, refresh on 401), the same source the sync target already uses (SE §18). The
  Supervisor reads it the way `DevBackend` does today.
- **Every command ends in an existing building block**: a Host op (IPC §3), a sync-engine action
  (SE §24), or a diagnostics read. The control plane adds routing, policy, durability and
  presence — not a second 1C access path.

---

## 3. Wire protocol — the main fork (P-1)

The cloud side that exists today is backend/1c's hub: `/api/v2/connector/ws`, `hello` /
`heartbeat` / `state_update` frames, commands relayed through Redis `connector-cmd:{connectorId}`,
replies `<action>_result` / `_ack` / `_progress` through `connector-resp:{requestId}`, routing by
`conn-of-infobase:{oneCId}` (audit E §1). It has **no authentication** in the application (SP §8)
and **no durable command log** (audit E, finding 7).

| Option | What | For | Against |
|---|---|---|---|
| **A. Speak the v2 hub protocol only** | the new Connector is wire-compatible with what the old one sends and answers | every existing caller (Odoo, cloud-os, aiba-next, kansler, MCP, Celery) works on day one; no backend change (D-9) | inherits the unauthenticated hub: anyone who can open the socket can send `write_to_1c` to this connector (SP §8); no durability on the cloud side; capability and presence bugs stay on the backend |
| **B. New authenticated protocol only (v3)** | connection-scoped device token, signed commands, durable command table on the backend | fixes the security and durability problems at the root | needs a backend/1c change and every caller to move — production backend change, not approved (D-9); nothing works until it ships |
| **C. Channel abstraction; v2-compatible transport first, v3 specified** | `ICloudControlChannel` with `HubV2Channel` now and `HubV3Channel` later — the same pattern as `IBackendSyncTarget` (SE §17) | existing callers work; the client enforces everything it can on its own (§11); v3 is a written contract the backend owner can build when approved; the router, policy, log and handlers never change | until v3, the client cannot tell a legitimate sender from a forged one on the v2 hub — so write commands stay gated (P-2) |

**Recommendation: C.** It is the only option that ships something useful without a backend
change and without accepting the open hub for writes. Concretely:

- `HubV2Channel` sends exactly the frames backend/1c understands (`hello` with
  `capabilities.commands`, `heartbeat`, `state_update`; replies as `<action>_result` / `_ack` /
  `_progress` with the caller's `requestId`). It **also** sends `Authorization: Bearer <user
  JWT>` on the WebSocket handshake — a desktop client can, a browser cannot; the v2 hub ignores
  it, and it lets the backend start verifying without another client release.
- Every reply, including every failure, is a `<action>_result` frame with `ok:false` and a
  structured error. The v2 hub drops plain `error` frames and unknown replies, so callers waited
  for the full timeout (SP §2, audit E finding 4); the new client never sends a frame the hub
  drops.
- `HubV3Channel` is specified in §3.1 for the backend owner. Not built until approved.

**Cut-over rule (extends D-1).** Announcing a base on the hub moves the cloud's write route for
that `oneCId` to whichever connector announced it last (`conn-of-infobase:{oneCId}`). The new
Connector therefore announces only bases the user linked in the new app (D41 links), and refuses
to announce a base while an old Connector is live for the same `oneCId` (presence lookup before
`hello`, same check the sync engine does for D-1). A developer override exists for testing only.

### 3.1 v3 protocol — specification for the backend owner (not built)

- **Auth**: a connection-scoped connector token issued when the user links a base (not the
  day-long user JWT), revocable server-side; sent on the handshake. The hub binds the socket to
  the token's user and the token's bases; a `hello` claiming any other base is refused.
- **Commands are signed** with a per-connector key so a forged frame from another socket cannot
  be accepted, and carry `origin` (`odoo`, `mcp`, `ai_chat`, `cloud-os`, `aiba-next`, `admin`,
  `celery`) so the connector's policy and audit can tell callers apart.
- **Durable command table** on the backend: `commandId` (idempotency key, supplied by the caller),
  state (`queued → delivered → acked → done | failed | expired`), result, timestamps; a status
  route; delivery retried until acked or expired; late results stored, not lost.
- **Presence keyed by socket session**, not by connector id, so closing one socket never deletes
  another socket's state (fixes the unregister-on-close bug, SP §5, §19).
- **Real client IP** recorded from the edge's forwarded header, plus a last-seen history.

---

## 4. Identity model (P-3)

The old model is `connectorId = "<userId>:<MachineGuid>"`, self-declared, unverified, colliding on
cloned VMs and copied profiles (SP §6). Proposed:

| Identity | What | Where it comes from | Stored | Stable across |
|---|---|---|---|---|
| `installationId` | one Connector installation for one Windows user | random GUID generated on first start | `%LOCALAPPDATA%\AIBA\Connector\installation.json` | restart, upgrade, logout/login; new on reinstall-with-wipe, new Windows user, copied profile is detected (below) |
| `deviceId` | the physical/virtual machine | SHA-256 of `MachineGuid` + an app-specific salt (the raw GUID is never sent) | computed each start, never cached | everything except a new Windows install; **equal on cloned VMs** |
| `deviceBinding` | detects copied profiles and cloned VMs | `installationId` stores the `deviceId` it was created on; a mismatch on start means the profile moved | in `installation.json` | — |
| `connectorId` (wire) | the channel's identity on the hub | `installationId` | — | as `installationId` |
| `userId` | AIBA account | JWT `sub` from `OneC.Cloud` | session store | logout changes it; the channel reconnects as the new user |
| `baseId` | a local 1C base | the base list (`bases.json`, D31) | local | rename of the base in the app |
| `oneCId` / connection | the cloud record a base is linked to | D41 links (per env, user, base) | `links.json` | as the link |
| binding / partition | an organisation inside a multi-org base | backend org-bindings (SE §21) | backend | as the binding |

- `installationId` replaces `userId:MachineGuid` on the wire. On the v2 hub the field is a free
  string, so this is wire-compatible. Fleet views can still group by `deviceId` (several
  installations on one RDP server) and by `userId`.
- A `deviceBinding` mismatch (profile copied to another machine, or a cloned VM image) creates a
  new `installationId` and tells the user once; two machines never announce the same identity.
- Multiple Windows users on one PC: one installation each, one channel each. Unlike the old
  Connector, nothing is machine-wide (no fixed ports — the edge uses port 0; the old adapters
  fought over 55899 and the signer ports, audit B).
- One channel per installation: the channel takes a per-user named mutex on start, so a second
  Supervisor started by the same Windows user runs without a channel instead of announcing the
  same identity twice (verify in C1 how many Supervisors the desktop can start today).

---

## 5. Presence and fleet (P-4)

**One socket per installation.** The old Connector opened two sockets under one id; closing
either deleted presence and every route for both, and the command socket never answered
`need_hello` (SP §5, §19 — the KANSLER 74-card incident). The new client opens one, answers
`need_hello` immediately with a full `hello`, and re-sends `hello` after every reconnect.

What the new Connector sends (in `hello`; changes in `state_update`; a digest in `heartbeat`
every 30 s — the v2 hub expires records after 90 s):

| Field | Source | Why it is useful | Old Connector |
|---|---|---|---|
| `connectorId` = `installationId`, `deviceId`, `userId` | §4 | routing, fleet grouping, clone detection | partly, unverified |
| `appVersion`, `hostVersion`, `protocolVersion` | build | knowing which fixes a customer has | app version only |
| `capabilities.commands` | the command registry (§6) — only commands enabled by policy | the cloud only sends what will be accepted | yes (lost after 90 s — fixed backend-side 2026-10-05) |
| `bases[]`: `baseId`, `oneCId`, `name`, `kind` (file/server), `platformVersion`, `configuration`, `configurationVersion`, `status`, `lastError` | Host `test` op (IPC §3) + link store | support can see "which 1C, which config, connected or not" without the PC | name + status; versions only via the HTTP heartbeat |
| per base `sync`: `mode`, `pendingWork`, `deadLetters`, `lastSentAt`, `tablesDone/Total` | sync engine (SE §24) | "is this base synced" without opening the PC | not on presence |
| `hostname`, `osVersion` | Windows | support identification | never sent |
| `env` (prod / dev) | `OneC.Cloud` | a dev-switched connector is visible as such | dropped by the hub |

Not sent: LAN IP (no support use found; the backend sees the peer), credentials, connection
strings, row data, file paths of bases. `hostname` is the only personal-ish field; it is
needed to tell machines apart on support calls (P-4 asks for approval).

Backend-side requirements for useful fleet views (for the backend owner, not built here): keep a
last-seen history after a connector goes offline (today the record disappears after 90 s), store
the fields above instead of dropping them (the hub drops `role`, `tenant_id`, `session_id`,
`protocol_version` today, SP §5), take the client IP from the edge's forwarded header, key
presence by socket session (§3.1).

The legacy `PATCH /onec/connection/{id}` 60 s status heartbeat (D41) stays as long as the cloud UI
reads it, and also carries per-base `version`.

---

## 6. Command model and durability (P-5)

### 6.1 Registry

Every command the connector accepts is a row in one registry: name(s), handler, class, default
policy, deadline. Unknown actions get a `<action>_result {ok:false, error.kind:"unsupported"}`
(the old Connector queued them for a hidden page and never answered, audit A2). The registry is
what `hello` advertises.

| Command (v2 name, aliases) | Class | Handler | Default |
|---|---|---|---|
| `server_ping`, `ping` | control | channel | on |
| `connector_status_request`, `status_request` | control | presence | on |
| `sync_now`, `pull_now` | sync | engine: targeted pull (§9.4) | on |
| `document_read` | read | Host `document` (ids or number/period, ≤ 200 rows) | on |
| `catalog_read` | read | Host `catalog` by ids (≤ 200) | on |
| `get_list`, `list_resource`, `refdata_get` | read | Host `catalog` / enum read with filters | on |
| `check_exists` | read | Host `document` / `catalog` with filters | on |
| `adapter_read` (GET mode) | read | Host `catalog` / `document` / schema (allow-listed roots) | on |
| `adapter_read` (QUERY mode) | read-query | new Host `query` op (§8.2) | **decision P-6** |
| `write_to_1c`, `write_doc_to_1c` | write | Host `create` (+ `post`) | **gated, P-2** |
| `post_1c_document`, `post_doc_in_1c` | write | Host `post` | gated |
| `update_1c_document`, `edit_1c_document` | write | Host `update` | gated |
| `unpost_1c_document`, `unpost_doc_in_1c` | write-destructive | Host `unpost` | gated, off by default |
| `delete_1c_document` | write-destructive | Host `delete` with `hard:false` only | gated, off by default |
| `create_ref` | write | new Host `catalogCreate` op | gated |
| `catalog_update` | write | new Host `catalogUpdate` op with compare-and-set | gated |
| `employee_write_to_1c`, `employee_fire_from_1c` | write | Host `create` (HR doc types) | gated; P-8, P-9 |
| `holiday_push_to_1c`, `holiday_delete_in_1c`, `holiday_check_1c` | write / read | new Host info-register ops | P-9 |
| `diag_*` (§10) | diagnostics | supervisor | on (read-only) |
| `sync_config_changed` | control | engine: refetch the manifest now (§9.3) | on |

### 6.2 Envelope and replies

Incoming (v2): `{action, requestId|request_id, oneCId|onec_id, ...payload}`. Base resolution is
**exact only**: `oneCId` → the linked local base; a binding id → its connection's base with the
binding as the organisation scope. No fuzzy name matching (the old `resolveDbNameFromPayload`
tried names, slugs and descriptors, audit A; the v2 hub's P2 path even matches names fuzzily,
audit E §1.5). An unknown `oneCId` is `ok:false, error.kind:"base_not_here"`.

Replies: `<action>_ack {requestId}` within 1 s for anything that may take longer than 5 s;
`<action>_progress {requestId, stage}` every 10 s while running (the hub treats progress as
non-terminal); exactly one `<action>_result {requestId, ok, result | error}`. Errors carry the
IPC error object (IPC §4) — layer, 1C message, `retryable`, `fillDiagnostics` — so callers stop
seeing timeouts for real 1C refusals.

### 6.3 Durability on the connector side

A `commands` table in its own SQLite file next to `sync.db` (same WAL rules, SE §13):

```
commands(request_id PK, action, base_id, class, origin, received_at, state, -- received|running|done|failed|refused
         result_json, error_json, finished_at, attempts)
```

- **Dedup and replay**: a `requestId` already `done`/`failed` gets the stored result again,
  without touching 1C. A `requestId` still `running` gets `_ack` again. This makes a cloud retry
  after a lost reply safe (today a late `_result` is simply lost, audit E finding 7).
- **At-most-once execution for writes.** A write that was `running` when the process died is
  not re-executed on restart; it is answered as `failed, error.kind:"interrupted"` with the
  document's idempotency marker, and the caller's retry resolves through the marker
  (`findOwned` / `create` returns `idempotent:true` for an existing marked document, D38).
- **Results are bounded** (64 KB stored; larger read results are not stored, only their size and
  hash) and kept 7 days.
- **Audit**: every write and destructive command writes who (`origin`, `userId`), what (action,
  base, document type, ref, marker), when, and the outcome — never row data or tokens. Readable
  through `diag_commands` (§10) and the desktop Activity screen.

### 6.4 Concurrency and priority

- Remote writes use the Host's per-base write gates (D12) and deadlines (IPC §5, max 600 s).
  The old Connector's global write gate froze sync on every base for up to 90 s while any write
  ran (audit A2); here a write blocks only its own base's write lane.
- Remote reads run as **foreground** work in the sync scheduler's terms (SE §14): they get a
  session before background sync, with a per-base cap of 2 concurrent remote reads and a global
  cap from the session budget (SE §15).
- Deadlines: the command's own deadline is the caller's timeout minus 10 s, capped at the Host
  maximum; the connector answers `Timeout` itself before the hub gives up (the hub's dispatch
  cap is 90–300 s by command, audit E).

---

## 7. Remote writes (P-2, P-7, P-8)

Mapping to what the Host already does (IPC §3, D38):

| Old command | Host op | Gap |
|---|---|---|
| `write_to_1c` | `create` (+ `post` unless `post_document:false`) | the old builders (bank transaction, trade documents, payroll passthrough, `prebuilt_doc`) live in the cloud payloads; the Host takes the old create body (D38) — verify each caller's body shape against D38 before enabling (block C5) |
| `post_1c_document` | `post` | none |
| `update_1c_document` | `update` | none |
| `unpost_1c_document` | `unpost` | none |
| `delete_1c_document` | `delete` with `hard:false` | hard delete refused (P-7) |
| `create_ref` | — | new Host `catalogCreate` (whitelisted catalog, scalar fields and references, AIBA marker where the catalog has a comment field) |
| `catalog_update` | — | new Host `catalogUpdate` with compare-and-set (`expected` values must match before write, as the old CAS) |
| holidays | — | new Host info-register write/delete for the production-calendar registers only |

Rules the client enforces regardless of protocol version:

- **Ownership (D18) stays**: update/post/unpost/delete only touch documents whose comment
  carries an AIBA marker — exactly as the Host does today. A cloud command cannot unpost an
  accountant's own document.
- **Idempotency is mandatory for create**: a `write_to_1c` without `_idempotencyMarker` is
  refused (`Validation`). Duplicate prevention then rests on the marker (D38), not on retries
  being rare.
- **Hard delete is refused** from the cloud (P-7). Evidence: no real caller sends `hard:true`
  (SP §3); MCP strips it; the only path is a raw admin-route call with a shared secret.
- **`_postCreateMethod` and any "call a 1C procedure" field are never implemented.** Confirmed
  unused by every real caller (SP §3); it is arbitrary code execution in the customer's 1C.
- **`exchange` (DataExchange.Load) is refused unless the document type is on an explicit
  allow-list** (P-8). The workspace rule is "never `exchange=true`"; the old Connector forced it
  for 9 HR document types, and the adapter's own comment says such documents post with no
  register movements (audit A). This must be settled with an accountant before HR writes are
  enabled — not inherited.
- **No forced posting.** The old adapter marked incoming bank documents `Posted=True` after a
  posting failure (SP §3, `main.os:17354-17503`); the Host reports the 1C refusal instead (D38).
- **No silent auto-create of the organisation's own bank account** (the old
  `postBankDocWithFallbacks` did, audit A2). An unresolved reference is a `fillDiagnostics`
  answer the caller must act on (the existing cloud "bank account missing" dialog already does).
- **Writes are gated** (P-2): off until the developer enables them, and per base. Reason: on the
  v2 hub the client cannot verify the sender (§3), and SP §1 shows an LLM chat tool and a
  dormant fully-privileged BI token among the possible senders.

---

## 8. Remote reads (P-6)

### 8.1 Bounded reads — on by default

`document_read`, `catalog_read`, `get_list`, `list_resource`, `refdata_get`, `check_exists`,
`adapter_read` GET: mapped to Host `document` / `catalog` / enum / schema ops. Same limits as
the old ones where callers depend on them (200 rows / ids per call), plus paging (`after`,
`cursorDate`) so a caller can continue instead of hitting a silent cap. Reads are **not** gated by
the sync-table list (the old ones were not either, SP §4): they are live reads of the base the
user linked, for the user's own company. Reads are identifier-whitelisted and parameterised
(D15) like every Host read.

### 8.2 Raw query (`adapter_read` QUERY) — decision P-6

backend/1c's VAT registry and VAT-regulated reports send their own 1C query text (up to 20 000
chars) as their **primary** path, with synced data as fallback (SP §4, §19 incident 5). The
Host's rule so far is "no caller-supplied query text" (D15).

| Option | What | Consequence |
|---|---|---|
| **i. Not supported** | the command answers `unsupported` | VAT reports fall back to synced data (already implemented by the caller, and seen working on 2026-10-06) |
| **ii. Constrained query op** | new Host `query`: text must start with ВЫБРАТЬ/SELECT; refuses `ПОМЕСТИТЬ`, `УНИЧТОЖИТЬ` and any temp-table batch; parameters only through the parameter map; row cap (10 000) and byte cap; deadline ≤ 120 s; per-base enable; every call audited with a hash of the text | VAT keeps its live path; 1C query language cannot write, so the risk is data exposure of the linked base, which every bounded read already has |
| **iii. Named queries** | the connector ships a list of approved query texts (VAT ones first); the cloud sends a name and parameters | safest; every new report needs a connector release |

**Recommendation: ii**, with the op off by default and enabled per base, because the caller is
real and primary, the query language is read-only, and the caps and audit bound the cost. Option
iii is the fallback if the developer wants no caller-supplied text at all.

---

## 9. Sync Table Manifest (P-10, P-11)

### 9.1 What the evidence requires (SP §11)

1. **Three states, not two**: "never configured" (use defaults), "explicitly empty" (sync
   nothing), "this list". The v2 backend collapses `[]` and `null`; the old auto-seed then put the
   defaults back within ~60 s of an operator emptying the list, and kansler wrote its own 409
   guard against the same ambiguity.
2. **Per-table policy round-trips**: the v2 model has only `{table, reports}`; the drawer's
   `schedule` is silently dropped on every save.
3. **A revision**, so a writer cannot overwrite a newer list blindly (two independent writers
   exist: the connector UI and kansler's GTD panel; a third route, `request-tables`, is unused).
4. **Changes take effect in seconds, not on the next 60 s poll** — kansler pairs every save with
   a `pull_now`, aiba-next pulls regardless of the list; both are workarounds for the delay.
5. **Adding a table starts its first copy; removing one never deletes cloud data by itself.**

### 9.2 Manifest shape (v3 target; v2 mapping in 9.5)

```json
{
  "connectionId": "…",
  "revision": 17,
  "state": "configured",                 // unset | empty | configured
  "updatedBy": "kansler:ved_sync",       // who wrote this revision
  "tables": [
    {
      "table": "Document_ГТДИмпорт",
      "family": "document",              // from the Host's `tables` op, not guessed from the name
      "lane": "default",                 // default | reports | manual
      "from": "2025-01-01",              // documents / periodic registers
      "refreshEveryMinutes": null,       // independent information registers (D-6)
      "addedBy": "kansler:ved_sync",
      "addedAt": "2026-10-05T09:12:00Z"
    }
  ]
}
```

No night lane (D-8). `manual` replaces the ЧекККМ special case if that stays a product policy.

### 9.3 Delivery

- The engine reads the manifest at start and on every `sync_config_changed` command (pushed by
  whichever writer saved it), and polls with the revision every 5 minutes as a fallback. A writer
  no longer needs to send `pull_now` to make its change visible.
- `ensure_tables {tables[], pullNow:true}` — one command that adds tables if missing (with the
  revision check) and starts their pull immediately. This is the kansler GTD pattern made a
  first-class operation instead of three calls and a 15-minute sweep.

### 9.4 Engine behaviour

- Added table → snapshot now at the user-visible priority (the engine already does this for
  "Add table", D-3 / SE §13 `config_hash`).
- Removed table → the engine stops syncing it and keeps its local state for 30 days; cloud rows
  stay. Deleting cloud rows is a separate, confirmed action (rebuild, D-3).
- `sync_now {tables?}` / `pull_now {tables?}` → targeted snapshot of those tables at foreground
  priority, preempting background work for that base (the old Connector's preempt, without the
  global lock).

### 9.5 On the v2 backend (until v3)

The v2 route stores only `{table, reports}`. The client keeps the extra policy (`lane`, `from`,
`refreshEveryMinutes`) in its own `sync_tables` rows (SE §13) and never writes `[]`; an operator
"sync nothing" is stored locally as `state: empty`, and the engine never auto-seeds a connection
whose list it has ever seen non-null. That avoids the re-seed bug without a backend change; the
real fix (tri-state, revision, policy fields) is the v3 manifest.

### 9.6 Stock snapshot and data coverage — revise D-7 (P-11)

Both now have proven consumers (SP §12): stock feeds aiba-next's 1UZ warehouse tab; data
coverage is what stops avtoprovodka from creating a duplicate document when the mirror has not
reached a date yet. Proposed: keep both. Coverage is reported per table through the target's
`ReportStatusAsync` (SE §17) with the old semantics (per table: earliest date reached, backfill
complete or not), on the v2 target via the existing `POST /entity/data-coverage` route. Stock
stays a 1uz/BePro-base feature (`GET stock?accounts=…` → `POST /entity/stock-snapshot`) on the
main lane only (the old reports lane sent it twice, audit A2).

---

## 10. Remote diagnostics (P-12)

What support can do without the customer's PC. All read-only unless marked.

| Command | Returns | Source |
|---|---|---|
| `diag_status` | installation, versions, uptime, hosts (pid, platform, RAM, sessions), bases (status, last error, platform and config version), sync summary per base | supervisor + Host `health` / `stats` / `test` |
| `diag_base_test {oneCId}` | a fresh connection test (configuration, versions, timing) | Host `test` |
| `diag_sync {oneCId}` | per-table state, rows, pending, dead letters, last sent, coverage | sync engine (SE §24) |
| `diag_logs {lines≤500, since?, level?}` | the tail of the supervisor/host log, secrets scrubbed (connection strings, tokens, passwords) | supervisor log |
| `diag_commands {since?}` | the command audit (§6.3) | command log |
| `diag_tables {oneCId}` | the base's tables (names, families) — what "Add table" shows | Host `tables` |
| `restart_host {oneCId}` | **action**: recycle the host serving that base (the supervisor already does this on its own, D24); refused while a write is running | supervisor |
| `diag_bundle` | **not sent automatically**: builds a support bundle locally and asks the user in the app to approve the upload | supervisor + desktop |

Not offered remotely: 1C configuration changes (the old heal panel ran `UpdateDBCfg` from a
button), the 1C user list, process kills of anything the supervisor did not start, rebuild of a
table (needs the user's confirmation, D-3).

---

## 11. Security model

**Enforced by the client alone (works on the v2 hub):**

- outbound only; no listening port beyond the loopback edge;
- exact base resolution; commands only for bases this installation linked;
- command registry allow-list; writes and destructive commands off until enabled per base (P-2);
- ownership marker (D18) on every modification; idempotency marker required on create;
- no hard delete, no `_postCreateMethod`, no forced posting, no `exchange` outside an
  allow-list, no silent creation of bank accounts;
- query op (if P-6 ii) read-only, capped, per-base, audited;
- local audit of every write;
- scrubbed logs; no credentials in presence, results or logs.

**Needs the backend (handed to the backend owner; not built here, D-9):**

- authentication on `/api/v2/connector/ws` and `/ws/1c` (SP §8);
- per-caller authorisation instead of four equal shared secrets; enforce the MCP unpost/delete
  deny in backend/1c, not only in backend-mcp (SP §1, §3); retire or scope `AIBA_BI_API_TOKEN`;
- signed commands with `origin`, durable command table, presence keyed by socket (§3.1);
- forwarded client IP; last-seen history;
- tri-state manifest with revision and policy fields (§9).

---

## 12. Not carried forward

| Item | Evidence | Reason |
|---|---|---|
| `push_to_1c`, `create_in_1c`, `delete_in_1c`, `delete_from_1c` | no sender found anywhere (SP §2, §16) | unused; unknown actions are answered `unsupported` |
| `employee_list_from_1c` | no handler in the old Connector (SP §2) | broken already |
| `_postCreateMethod` | unused by every real caller (SP §3) | arbitrary code execution |
| hard delete from the cloud | no real caller (SP §3) | destructive, unused |
| bank-signer relay (`relay.aiba.uz`) | D40; SP §9 | out of scope; unauthenticated reach into payment creation |
| retail turnover rollup | no consumer found (SP §12) | pending the aiba-cloud tenant-API check (§14) |
| `request-tables` | no caller (SP §11) | replaced by `ensure_tables` |
| second presence socket | SP §5, §19 | root of the presence-wipe incidents |
| event-log JS lane, hidden documents-page writer, Ctrl+Shift+D prod/dev switch, release DevTools | audit A, B | dead or unsafe; the rewrite has its own feed (D36) |

---

## 13. Proposed decisions (for the developer)

| # | Question | Proposal |
|---|---|---|
| P-1 | Wire protocol | Channel abstraction; v2-hub-compatible transport first; v3 written for the backend owner (§3) |
| P-2 | Remote writes before hub auth exists | Off by default; enabled per base by the developer; reads, pulls and diagnostics on |
| P-3 | Identity | `installationId` (random, per Windows user) on the wire; hashed `MachineGuid` as `deviceId`; clone/copy detection (§4) |
| P-4 | Presence fields | As §5, including `hostname` and `osVersion`; no LAN IP |
| P-5 | Durability | Local command log with dedup/replay, at-most-once writes, 7-day audit (§6.3) |
| P-6 | Raw query (`adapter_read` QUERY) | Option ii: constrained read-only query op, off by default, per base, audited |
| P-7 | Hard delete from the cloud | Refused |
| P-8 | `exchange=true` | Refused except for document types on an allow-list agreed with an accountant; none until then |
| P-9 | Legacy HR (hire/fire/holidays) | Port only if the cloud-os HR product stays; decide before block C6 |
| P-10 | Sync Table Manifest | §9: tri-state, revision, policy fields, push `sync_config_changed`, `ensure_tables`; client-side workaround on v2 |
| P-11 | Stock snapshot, data coverage (D-7) | Keep both — consumers proven |
| P-12 | Remote diagnostics | §10 set; `restart_host` the only action; support bundle only with the user's approval |

---

## 14. Implementation blocks (after approval; nothing built)

Each block ends with tests, a live check on local bases and the stub hub, and status/decision
updates, as the S-blocks did. No connection to the production hub until the developer says so.

| Block | Scope | Verification |
|---|---|---|
| C0 | Hub stub (`OneC.CloudStub`): v2 hub frames, Redis-free in-memory relay, fault injection (drops, duplicates, late replies, forged frames) | stub tests |
| C1 | Identity (§4) + `ICloudControlChannel` + `HubV2Channel`: connect, `hello` / `heartbeat` / `state_update`, `need_hello`, reconnect with backoff, one socket, D-1 refusal | stub: reconnect storms, double start, profile copy |
| C2 | Command router, registry, replies (`_ack`/`_progress`/`_result`), exact base resolution, policy | every unknown/invalid input answered, never silent |
| C3 | Command log: dedup, replay, interrupted writes, audit, retention | crash tests (kill at each state) |
| C4 | Reads (§8.1) + diagnostics (§10) | live: bilim/KAN through the stub hub; secrets scrub test |
| C5 | Writes (§7) with existing Host ops, gated; caller body shapes checked against D38 | live on bilim with `AIBA_REWRITE_` markers only |
| C6 | New Host ops: `catalogCreate`, `catalogUpdate` (CAS), production-calendar register writes (if P-9) | live, marker-owned test catalog items only |
| C7 | Manifest client (§9): local tri-state, revision, `sync_config_changed`, `ensure_tables`, targeted pulls; coverage + stock reporting (P-11) | engine tests + stub |
| C8 | Query op (if P-6 ii) | caps, refusals, audit |
| C9 | Desktop: channel status, command activity, per-base remote-write switch | visual approval |

---

## 15. Facts still missing (from SP §20) and what each blocks

| Unknown | Blocks |
|---|---|
| Is `/api/v2/connector/ws` reachable from the internet without auth at the edge? | how long writes must stay gated (P-2) |
| Who calls `catalog_update` in KANSLER prod (code not found) | the exact CAS payload for C6 |
| Does the AI chat write path have its own confirmation/audit? | whether `origin=ai_chat` needs a stricter policy |
| Retail rollup consumer in the aiba-cloud tenant-API service | final drop of the rollup |
| Each `write_to_1c` caller's exact body shape vs D38 | enabling writes per caller (C5) |
| Accountant's answer on HR `exchange=true` postings | P-8 allow-list |
| Who operates `relay.aiba.uz` | nothing in this design (relay is dropped, D40); needed for the old Connector's security cleanup |
