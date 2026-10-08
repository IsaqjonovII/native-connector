# C — Write path map: backend → Connector → 1C (normal primitives)

Read-only audit, 2026-10-07. Scope: create document, update document, post, unpost,
soft delete (deletion mark), catalog create, catalog compare-and-set (CAS) update.
Out of scope: fleet, diagnostics, scripts, arbitrary procedures, `_postCreateMethod`, HR
exchange-mode documents.

Checkouts read:
- New Connector: `D:\aiba\1c-arch` (docs + `src/OneC.Host`, `src/OneC.Supervisor`, `src/OneC.Ipc`)
- Python: `D:\aiba\backend\1c` on `development` @ `919479b`
- Rust: `D:\aiba\next-modules\onec` on `master` @ `6c46165`. `_wt-onec-catupd`
  (`feat/catalog-update`, `4c03a4e`) is **already merged into master**. `_wt-onec-read-relay`
  only has uncommitted read routes (`document_read` / `catalog_read`), no write code.
- Old connector (history only): `D:\aiba\connector` on `release`, plus
  `OLD_CONNECTOR_SECOND_PASS.md` and `OLD_CONNECTOR_FEATURE_MAP.md`.

---

## 0. Top findings

1. **No durable command store anywhere on the normal write path.** Python has only one
   durable write record, `OdooInbox` (Odoo create+post only). Rust `onec` has no
   command or write table at all (`src/store.rs:34-353` lists every table). The new
   Connector has no queue either: pipe request ids are per-connection integers
   (`IPC_CONTRACT.md:29-30`).
2. **Python `write_idempotency.py` is dead code.** `reserve_write` / `mark_written` have no
   caller in `app/`, and the `WrittenDoc` model it imports does not exist
   (`app/services/write_idempotency.py:27`; no `class WrittenDoc` in the repo). Any doc
   that says "(oneCId, externalId) dedup exists" is wrong.
3. **The command socket has no authentication in Python, and none by default in Rust.**
   Python: `app/api/connector_ws.py:9-11`, mounted at `main.py:101`. Cloud commands
   (`write_to_1c`, `delete_in_1c`, …) are accepted on the same socket with no check
   (`app/api/cloud_router_ws.py:704-760`). Rust: `ws_secret_ok` lets everyone in unless
   `connector_ws_require_secret` is set. The default is OFF (`src/connector.rs:1114-1153`),
   and `via` is used only for logging (`:1151`). No command is signed anywhere. `eskey`
   in Rust is a config value plus a TODO (`src/config.rs:35-39`, `src/onec.rs:22-30`). It
   is meant for document signing, not command signing.
4. **The new Connector's ownership rule (D18) conflicts with a live Python flow.** The new
   host refuses update / post / unpost / mark / delete on any document whose
   Комментарий does not start with `AIBA_` (`WriteService.cs:218-241`, 403 via
   `Operations.cs:442-448`). Python's `post_1c_document` exists to post drafts made by a
   **1C user** (`app/services/onec_post.py:1-24`; Odoo `kind=post_document`,
   `mongodb_models.py:432-436`). Before the Rust contract fixes this, the product has to
   decide which one is right.
5. **The new Connector has no catalog create and no catalog update.** Its op list
   (`OneC.Ipc/Envelope.cs:9-38`, `IPC_CONTRACT.md:52-74`) has only document writes.
   Catalog items can be created only as a nested reference inside a document create, and
   only on `СоздатьНовый` / `forceCreate` (`RefResolver.cs:216-234, 326-350`). Catalog CAS
   is live in production through the old connector (`OLD_CONNECTOR_SECOND_PASS.md:96-97`),
   so it has to be built.
6. **The new Connector cannot receive cloud commands yet.** This is on purpose (D41,
   `DECISIONS.md:769-773`). Its writes are reachable only on `127.0.0.1` with
   `X-AIBA-Token` (`IPC_CONTRACT.md:167-171`). The cloud → Connector leg that Rust needs
   does not exist on the new side.

---

## 1. Layer summaries

### 1a. New Connector (OneC.Host + Supervisor edge)

- **Pipe envelope**: `{v, id, op, base, deadlineMs, args}` → `{v, id, ok, elapsedMs,
  result|error}` (`IPC_CONTRACT.md:32-45`). `id` is a per-connection integer. It is not a
  durable command id and is not deduplicated.
- **Write ops**: `create`, `update`, `post`, `unpost`, `markDeleted`, `delete`, plus
  `findOwned` for cleanup (`IPC_CONTRACT.md:66-73`). Dispatch is in `Operations.cs:136-147`
  and handlers are in `Operations.cs:346-390`.
- **Edge routes**: `POST/PUT/PATCH/DELETE /v1/bases/{base}/documents/{docType}[/{ref}[/post|/unpost|/mark-deleted]]`
  (`EdgeServer.cs:219-256`, `IPC_CONTRACT.md:199-205`). `X-AIBA-Deadline-Ms` sets the
  deadline (`EdgeServer.cs:320`).
- **Target**: `base` is the local infobase name. There is no organisation argument. The
  organisation is just a field in the create body. Post / unpost / mark / delete take
  `docType + ref` only.
- **Idempotency**: create only. The body must carry `Комментарий` starting with `AIBA_`,
  and `_idempotencyMarker` = `AIBA_<KIND>_<id>`, which must appear in Комментарий followed
  by `:` (`WriteBody.cs:55, 91-117`). Before writing, `FindByMarker` looks for the newest
  **active** (not deletion-marked) document that carries `<marker>:`. If one exists, it is
  returned with `idempotent:true, created:false`, and a draft is not posted
  (`DocumentWriter.cs:100, 262-288`). Two creates with the same marker are serialised by
  64 in-process stripes. A second concurrent one is refused, not queued
  (`DocumentWriter.cs:60-80`). The scope is one host process, so this is not a
  cross-machine lock.
- **Ownership (D18)**: update, post, unpost, markDeleted and delete load the object and
  refuse if Комментарий does not start with `AIBA_` (`WriteService.cs:218-241`,
  `DECISIONS.md:180-191`). Update may not remove the marker (`DocumentWriter.cs:202`).
- **Write semantics (D38)**: one `Записать` per call. A failed post writes nothing and
  returns 1C's user messages (`DocumentWriter.cs:444-470`). Exchange mode is refused
  (`Operations.cs:348-349`). Unresolved or ambiguous references refuse the whole document
  with 422 and `fillDiagnostics` (`IPC_CONTRACT.md:84-90`, `DECISIONS.md:666-686`).
- **Concurrency**: per-(base, op) write gates: create 4 (server) or 2 (file), update 2,
  mark 2, post 1, delete 1. Gates are taken before renting a session, with a 2-minute gate
  timeout (`DECISIONS.md:169-178`, `WriteService.cs:28, 203-204`). Unpost shares the
  **post** gate (`WriteService.cs:109`).
- **Deadlines**: default 60 s, max 600 s (`Envelope.cs:65-69`). A running COM call cannot
  be interrupted. The response always resolves as `Timeout` / `Cancelled`. The supervisor
  adds 5 s, then reports `Transport` (`IPC_CONTRACT.md:148-158`).
- **Error model**: `{layer, hr, oneCCode, message, op, member, base, retryable,
  sessionFatal, hostFatal, kind, notFoundScope, data}` (`IPC_CONTRACT.md:122-146`). HTTP
  mapping: Validation 400, notFound 404, forbidden (not AIBA's) 403, unprocessable / Runtime
  422, Connector 502, Busy / retryable 503 + `Retry-After`, Timeout 504, Cancelled 499
  (`EdgeServer.cs:336-348`). The `kind` for forbidden and notFound is derived from
  **message text** (`Operations.cs:442-448`), which is fragile.

### 1b. Python backend/1c

- **Transport**: Redis pub/sub. A command goes to `connector-cmd:{connectorId}` and the
  reply comes back on `connector-resp:{requestId}`. The caller subscribes before it
  publishes, and publish-count 0 means offline (`app/core/connector_hub.py:519-532,
  587-669`). Routing goes through `conn-of-infobase:{oneCId}` (TTL 90 s).
- **Envelope**: `{action, requestId, request_id, oneCId, onec_id, ...payload}`. Base keys
  (`base_key`, `infobase_id`, `onec_connection_id`, `target{}`) are duplicated by each
  caller (`connector_hub.py:619-631`, `onec_post.py:210-224`, `odoo_write.py:711-723`).
  `requestId` is `uuid4().hex`, used only to correlate the reply. Nothing persists it.
- **Result handling**: `*_progress` frames are not terminal, while `*_ack` and `*_result`
  are (`connector_hub.py:578-584`). The result is the connector's loose dict. Failures are
  plain strings (`connector_offline`, `timeout`, `bad_result`).
- **Timeouts**: 120 s for admin commands (`connector_hub.py:40`), 300 s for `write_to_1c`
  (`:51`), 90 s for post (`onec_post.py:40-58`). The cloud relay waits 120 s from dispatch,
  45 s with no progress frame, and 600 s at most (`cloud_router_ws.py:26-50`).
- **Admin write routes** (service-secret, `/connectors/infobase/{onec_id}/…`,
  `app/api/internal/routes/connectors_admin.py`): `document/post` :168,
  `document/unpost` :227, `document/update` :239, `catalog/update` :257,
  `document/delete` :384. Request models are at `:96-149`. There is **no admin create
  route**. Creates go through `write_to_1c`: the Odoo inbox, or a cloud client on the WS
  relay.
- **Durability**: only `OdooInbox` (`mongodb_models.py:416-510`). It has states
  `received → writing → written | failed | dead`, a unique `odooDocId`, a lease
  (`leaseOwner` / `leaseUntil`, TTL = write timeout + 120 s), `attempts` (max 8),
  `lastDispatchedPayloadHash`, `idempotentReuse`, `lastStage`, `refKey`, `number`.
  `odoo_write.py:19-32` describes four idempotency layers: unique id, lease, 1C number,
  and the Комментарий marker `AIBA_ODOO_<id>_<sha8>`. The marker lookup directive is sent
  only on a blind retry (`odoo_write.py:93-128, 535-537`). Post, unpost, update, delete
  and catalog update store **nothing**.

### 1c. Rust next-modules/onec

- **Transport**: an in-memory hub, one process per tenant. `dispatch_to_infobase` pushes a
  frame into the socket's mpsc and waits on a oneshot (`src/connector.rs:443-530`). It
  stamps the same routing keys as Python (`:487-508`). `request_id` lives in memory only.
  A process restart loses every in-flight command.
- **Cloud relay**: the actions in `CLOUD_RELAY_ACTIONS` (`:832-845`) are forwarded verbatim
  from aiba-next to the connector, and replies come back verbatim. The relay keeps no
  state and does not interpret anything. Pipe limits: 120 s idle, 660 s hard
  (`:123, 130`).
- **Write routes** (`src/write.rs:1250-1262`, secret or JWT `:78-80`):
  - `document/post`: fully ported. It always answers HTTP 200, and the verdict and
    `retryable` are in the body (`write.rs:941-1014`, reply parse `:1016-1100`).
  - `catalog/update` with CAS: ported. It checks the capability first and refuses
    `connector_unsupported` before dispatch. The reply is the connector's own, passed
    through (`write.rs:1280-1370`). The capability check uses `picked_advertises`, which
    the read-relay worktree says can pick a different connection from the one dispatch
    uses (`_wt-onec-read-relay` diff, `chosen_advertises`).
  - `write_to_1c`: a sketch only, `#[allow(dead_code)]` (`write.rs:1107-1130`). Marker
    helpers (`:1136-1190`) and the error taxonomy (`:1205-1216`) are real. The lease /
    backstop / inbox state machine is a TODO (`:1218-1246`).
  - **Missing**: update, unpost and delete routes (no match in `src/`), and catalog create
    (only relayed raw as `create_ref`).
- **Storage**: no command, write or result table (`src/store.rs`).

### 1d. Old connector (history only — cite, do not re-derive)

- Live senders: `write_to_1c` (Odoo, cloud-os avtoprovodka, aiba-next HR, kansler-bank,
  cloud-os AI chat); `post_1c_document` (admin, kansler EDO button); unpost / update /
  delete (admin + MCP); `catalog_update` CAS (kansler ИКПУ audit); `create_ref` (sotuv,
  HR) (`OLD_CONNECTOR_SECOND_PASS.md:82-104`, `OLD_CONNECTOR_FEATURE_MAP.md:46-66`).
- `create_ref` has no idempotency. It POSTs the catalog item blindly
  (`connector/apps/app/src/providers/onec-sync-provider.tsx:7226-7310`), so a retry
  creates a second item.
- CAS lives in the adapter: `ОбновитьЭлементСправочника` with `_expected` returns
  `precondition_failed` plus the current values (`main.os:7228-7290`; connector
  `utils/catalog-update-cas.ts:23-97`).
- Socket exposure: `OLD_CONNECTOR_SECOND_PASS.md:170-186`.

---

## 2. Per primitive

Legend: **C** = new Connector today, **Py** = Python today, **Rs** = Rust onec today,
**Need** = what the Rust contract needs.

### 2.1 Create document

- **C**: `create` with `docType, body, post?=true` (`Operations.cs:346-367`). Result:
  `{id, number, date, posted, created, idempotent, document, requestedDocumentName,
  fillDiagnostics, sessionId}`. The marker is required, and idempotency comes from the
  marker lookup on active documents. Refusal is 422 with `fillDiagnostics`, which is
  all-or-nothing. Deadline is 60 s by default.
- **Py**: no generic create. `write_to_1c` sends a high-level "provodka result" envelope,
  and the **connector** builds the 1C body (`odoo_write.py:1-17, 700-728`). Durable only
  for Odoo (`OdooInbox`). Cloud-relay creates are fire-and-relay with no record.
- **Rs**: `write_to_1c` is a dead-code sketch. Production creates pass through the
  transparent relay from aiba-next (`connector.rs:832`). Nothing is stored.
- **Need**: `command_id` (UUID, made by the backend, persisted **before** dispatch);
  `idempotency_key` = caller's stable source id, unique per (tenant, connection, kind);
  a marker derived from it, `AIBA_<KIND>_<id>`, that fits the ПОДОБНО rules (no `:` or
  whitespace, ≤64 chars, `odoo_write.py:107-118`); target (tenant, connection id, base
  key, organisation ref); payload = the D38 body (resolved refs, not business facts, if
  the new Connector is the executor); a `post` flag. Result: `ref`, `number`, `date`,
  `posted`, `created` vs `idempotent_reuse`, `fill_diagnostics`, `error_class`. An
  `idempotent && !posted` reuse must be handled as "post it now", the way Python does
  (`odoo_write.py:1169-1240`).

### 2.2 Update document

- **C**: `update` with `docType, ref, fields{}, post?, autoUnpost?=false`. Result:
  `{id, updated, posted, autoUnposted, reposted, fillDiagnostics}`
  (`Operations.cs:369-384`, `DocumentWriter.cs:189-241`). AIBA documents only. Tabular
  sections are refused with 400. A posted document is changed and re-posted in one write.
- **Py**: `update_1c_document` with `{doc_type, ref_key, fields, auto_unpost}`, 120 s, no
  record (`connectors_admin.py:239-254`, model `:102-114`).
- **Rs**: missing.
- **Need**: command id; idempotency key optional (a retry with the same fields is safe;
  CAS-style `expected` is better). Target + `docType` + `ref`. Result: `ref`, `number`,
  `posted` after the write, `auto_unposted` / `reposted`, error class. Decide whether
  non-AIBA documents can ever be updated (D18 says no).

### 2.3 Post

- **C**: `post` with `docType, ref` → `{id, posted:true, number}`
  (`Operations.cs:386-391`, `WriteService.cs:93-103`). An already posted document is
  re-posted. AIBA documents only. Gate K=1.
- **Py**: `post_1c_document`, 90 s, answer always 200 + `ok/retryable/alreadyPosted/
  movements/warnings` (`connectors_admin.py:168-224`, `onec_post.py:200-260`). Cloud
  bases go through OData `Post()`. Posts **non-AIBA drafts** on purpose.
- **Rs**: ported, same shape (`write.rs:941-1100`).
- **Need**: command id; idempotency comes for free (posting twice is harmless, report
  `already_posted`). Result: `ref`, `number`, `posted`, `movements` count if available,
  warnings, error class. Resolve the D18 conflict (top finding 4).

### 2.4 Unpost

- **C**: `unpost` with `docType, ref` → `{id, unposted:true}`. No number and no posted
  flag in the result (`Operations.cs:139-142`). AIBA documents only. Uses the post gate.
- **Py**: `unpost_1c_document` `{doc_type, ref_key}`, 120 s, no record
  (`connectors_admin.py:227-236`). Reachable with any of four equal shared secrets
  (`OLD_CONNECTOR_SECOND_PASS.md:88-94`).
- **Rs**: missing.
- **Need**: command id; naturally idempotent. Result: `ref`, `posted:false`, error class.
  Permission must be per action, not per shared secret. MCP already excludes this
  primitive from minted grants.

### 2.5 Soft delete (deletion mark)

- **C**: `markDeleted` with `docType, ref, mark` → `WriteResult{ref, number, kind}`, or
  `delete` with `hard:false` → `{id, marked:true}`. Default `DELETE` is a mark, and
  `?hard=true` deletes for real (`Operations.cs:143-147`, `EdgeServer.cs:247-256`). There
  is **no unpost before the mark**, so a posted document keeps its movements while marked.
- **Py**: `delete_1c_document` `{doc_type, ref_key, hard=false}`. The connector unposts
  first (`connectors_admin.py:384-395`, model `:117-120`).
- **Rs**: missing.
- **Need**: command id; idempotent (mark twice = same state). Contract field
  `unpost_first: bool`, because the two sides behave differently today. Result: `ref`,
  `deletion_mark:true`, `posted` after the call, error class. Keep `hard` out of the
  normal contract. Hard delete is out of scope.

### 2.6 Catalog create

- **C**: missing as a standalone op. Only nested inside a document create on an explicit
  `СоздатьНовый` / `forceCreate` (or `allowCreate` for a bank account), with only the
  fields given (`RefResolver.cs:216-234, 326-350`).
- **Py**: no admin route. Only the raw `create_ref` relay (`cloud_router_ws.py:67, 85`).
- **Rs**: only the raw relay (`connector.rs:836`).
- **Old**: `create_ref` → adapter catalog POST, no dedup
  (`onec-sync-provider.tsx:7226-7310`).
- **Need**: a new Connector op (`catalogCreate`) **and** an idempotency rule. Catalog items
  have no Комментарий in many configurations, so the marker approach needs a per-catalog
  decision: exact natural-key lookup first (ИНН / Код / Наименование + owner, like
  `RefResolver`'s rules), then create. Result: `ref`, `code`, `name`, `created` vs
  `existing`, error class (`ambiguous_reference`, `unprocessable`).

### 2.7 Catalog CAS update

- **C**: missing.
- **Py**: `catalog_update` `{catalog, ref_key, fields, expected?}`. Before dispatch it
  checks the capabilities `catalog_update` and `catalog_update_cas`. There is no ownership
  check, so it edits accountants' cards (`connectors_admin.py:123-149, 257-300`).
- **Rs**: ported in master (`write.rs:1280-1370`). The reply passes through.
- **Old**: adapter `_expected` → `precondition_failed` + current values
  (`main.os:7228-7290`). Unknown attributes go to `updateErrors` and the write still
  succeeds.
- **Need**: a new Connector op (`catalogUpdate`) with `expected{}`, compared on the session
  thread before `Записать`. Result: `ref`, `updated`, `precondition_failed` +
  `current{}`, `attribute_errors[]`, error class. The idempotency key is `expected`
  itself: a retry after success fails the precondition, so it must be reported as
  "already applied" when `current == fields`. Decide whether partial success (unknown
  attribute) is allowed. D38's direction says refuse.

---

## 3. What the Rust contract needs (common shape)

**Command (persist before dispatch):**
- `command_id` UUID. One per logical command, reused on every retry and attempt.
- `idempotency_key` text, unique per (tenant, connection_id, primitive). For creates it is
  also the source of `AIBA_<KIND>_<id>`.
- `primitive` enum: `doc_create | doc_update | doc_post | doc_unpost | doc_mark_deleted |
  catalog_create | catalog_update`.
- Target: `tenant`, `connection_id` (bigint), `base_key` (the string the connector
  advertises), `org_ref` (nullable; one base can serve many organisations — see the
  multi-org memory), `connector_id` actually picked (filled at dispatch).
- `doc_type` / `catalog`, `ref` (nullable for creates), `payload` jsonb, `payload_hash`
  (tells a blind retry from a corrected resend, as in `OdooInbox`), `expected` jsonb for
  CAS, `post` flag, `deadline_ms`.
- `envelope_version`.

**Result (typed columns + raw jsonb):**
- `ref`, `number`, `date`, `posted`, `deletion_mark`, `created` / `idempotent_reuse`,
  `movements`, `warnings`, `fill_diagnostics`.
- `error_class` enum: `validation` (never reached 1C, do not retry),
  `unprocessable` / `business_refusal` (1C or the resolver said no, do not retry),
  `forbidden` (not AIBA's), `not_found`, `precondition_failed`, `connector_unsupported`,
  `offline` (never sent, retry and refund the attempt), `busy` (retry), `timeout_unknown`
  (may have landed, reconcile before any retry), `transport`. Plus raw `error` jsonb
  (layer / hr / oneCCode / message) from the IPC error.
- Do not reuse "HTTP 200 always + `retryable` bool" as the stored model. It loses the
  difference between `offline` (never sent) and `timeout_unknown` (maybe sent). Python and
  Rust need that difference (`write.rs:1192-1210`) and do not keep it.

**Command row states (durable):**
`queued → dispatched → running (progress seen) → succeeded | failed (terminal class) |
unknown (timeout after dispatch) → reconciling (marker / ref lookup) → succeeded | queued
(proven not written) | dead`. Also `cancelled`. There is a separate **attempts** table
(attempt no, connector_id, request_id, started / finished, outcome) because one command
can be sent many times.

---

## 4. What the Rust data/API model must NOT block

1. **A later durable command table.** Do not make the HTTP handler the owner of the
   outcome. Today every route waits synchronously on an in-memory oneshot
   (`connector.rs:443-530`). The API must allow `202 + command_id` and then
   poll or subscribe, even if v1 still waits inline. Keep `request_id` (per attempt)
   separate from `command_id` (per command).
2. **Signed commands.** The envelope needs room for `{alg, key_id, issued_at, expires_at,
   nonce, sig}` over a canonical form of `(command_id, primitive, target, payload_hash,
   expected)`. Do not let the hub rewrite signed fields: today it injects routing keys
   into the payload (`connector.rs:487-508`, `connector_hub.py:619-631`). Routing keys
   should go in an outer frame, not inside the signed body. The new Connector should then
   check the signature and that the target base matches before any COM call.
3. **Result storage.** Keep `ref / number / posted / error_class` typed, not only a
   passthrough blob. Today `catalog/update` returns the connector's reply verbatim
   (`write.rs:1361-1368`). Store raw jsonb next to the typed columns.
4. **Idempotency across processes.** The new Connector's marker stripes are per host
   process (`DocumentWriter.cs:60-80`). The backend key + marker lookup must stay the real
   guard, so the key must be stable across retries and never derived from `request_id`.
5. **Org scoping.** Do not key the target by `oneCId` string alone. Keep
   `connection_id + base_key + org_ref` (D46 partition lesson, `DECISIONS.md:922-935`).
6. **Capability negotiation per connection**, chosen with the same pick the dispatch uses
   (the `picked_advertises` vs `chosen_advertises` gap).
7. **Per-primitive authorization.** Do not store "has the service secret" as the only
   permission. Unpost and delete need their own grants (`OLD_CONNECTOR_SECOND_PASS.md:88-94`).
8. **Deadline layering** must stay strictly increasing: Connector op deadline < backend
   dispatch wait < row lease (Python's chain: 300 < 330 < 360 < 420,
   `odoo_write.py:134-146`; new Connector max 600 s + 5 s).
