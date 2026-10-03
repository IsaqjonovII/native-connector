# IPC contract — v1

Transport decision: DECISIONS D28 — named pipes between supervisor and hosts, one HTTP
endpoint on the supervisor. This document is the contract both sides implement. It is v1:
additive changes are fine; a breaking change bumps `v`.

```
WinUI / cloud relay / curl
        │  HTTP/1.1 JSON, 127.0.0.1 only, X-AIBA-Token
        ▼
  OneC.Supervisor  ──────── named pipe per host (per-user ACL) ────────►  OneC.Host (8.3.15)
                   └─────── named pipe per host ──────────────────────►  OneC.Host (8.3.18)
```

## 1. Pipe transport (supervisor ↔ host)

- **Name**: `aiba-onec-<hostKey>-<supervisorPid>-<nonce>`, e.g.
  `aiba-onec-8.3.15.1565-x64-4120-3f9a1c2e`. The nonce is fresh for every host instance, so a
  restarted or recycled host, a second supervisor in the same process, or a stale host from a
  dead supervisor can never be reached by mistake.
- **Who listens**: the host creates the pipe server. **ACL**: current user only (plus
  SYSTEM); `FILE_FLAG_FIRST_PIPE_INSTANCE` so nothing can squat the name first.
- **Bootstrap**: the host is started with `serve --pipe <name>`. Its first stdin line is the
  config JSON (comcntr path, bases with credentials, budget). Credentials never touch disk
  and never cross the pipe. After that stdin carries nothing; **stdin EOF = supervisor gone
  → host exits.**
- **Framing**: each message is `uint32 little-endian length` + `length` bytes of UTF-8 JSON.
  Max frame 64 MB; a larger frame is a protocol error and closes the connection.
- **Multiplexing**: one connection carries many requests concurrently. Requests carry an
  `id`; responses echo it; responses may arrive in any order. Writes are serialised per side.

## 2. Envelope

Request:

```json
{ "v": 1, "id": 42, "op": "read", "base": "kansler", "deadlineMs": 30000, "args": { } }
```

Response:

```json
{ "v": 1, "id": 42, "ok": true, "elapsedMs": 3, "result": { } }
{ "v": 1, "id": 42, "ok": false, "elapsedMs": 9, "error": { } }
```

Events (host → supervisor, `id` absent): `{ "v": 1, "event": "ready", ... }` once after the
pipe is up.

## 3. Operations

| op | base | args | result |
|---|---|---|---|
| `health` | — | — | `{ pid, comcntr, comcntrVersion, uptimeMs }` |
| `stats` | — | — | resource sample + pool stats per base |
| `version` | yes | — | `{ platformVersion }` (1C's own `ВерсияПриложения`) |
| `test` | yes | — | `{ configuration, synonym, configurationVersion, platformVersion, ms }` (metadata probe, D32) |
| `read` | yes | `entity, fields[], limit, orderBy?, desc?, from?, to?, refs?` | `{ rows: [ {field: value} ], sessionId }` |
| `catalog` | yes | `catalog, limit?=100, offset?, fields[]?, filters{}?, after?, skipTotal?` — or `catalog, id` | `{ rows, totalCount, next, ignoredFields, sessionId }` — or `{ row }` (D33) |
| `slices` | yes | `kind (information\|accumulation\|accounting\|document), name, parts?=4, from?, to?` | `{ slices: [{ from, to, rows }] }` — balanced period slices for a parallel cold read (D35) |
| `tables` | yes | — or `details: [table…]` | `{ tables: [{ table, name, synonym, family, isMovement }] }` — every catalog, document, chart of accounts and register (names + synonyms, cached like schemas; 1 656 on bilim in 3 s); with `details`, the given tables with `family` (`reg_info_recorded` / `reg_info_independent`) and `isMovement` read from 1C — Sync "Add table" (2026-10-01) |
| `register` | yes | `kind (information\|accumulation\|accounting), register, limit?=100, offset?, cursorDate?, order?=desc, to?, skipTotal?, recorderDocument?, recorderId?` (the last two: one document's movements, 5.9); sync (S3): `syncKeys?` (every recorded row gets `recorderRef`, `lineNo`, every row with an organisation `orgRef`; independent information registers page by natural key and each row gets `naturalKey`), `afterLine?` (by-recorder page after that НомерСтроки), `afterKey?` (independent register page after that key) | `{ rows, totalCount, nextCursorDate, nextCursorSkip, hasMore, sessionId, nextLine?, nextKey? }` (D34) |
| `versions` | yes | `table (Документ.X\|Справочник.X\|ПланСчетов.X), after?, limit?=5000` — or `table, ids[]` | `{ rows: [{ id, version }], next, sessionId }` — or `{ rows }` (ids missing from the answer are gone from 1C). Sync §6/§12 |
| `chart` | yes | `chart, limit?=1000, offset?` | `{ rows, totalCount, hasMore, sessionId }` — the old `/api/charts/{name}` rows (every column of `ВЫБРАТЬ *`, by Код) |
| `document` | yes | `document, limit?=100, offset?, fields[]?, filters{}?, cursorDate?, order?=desc, after?, tabular?=true, from?, to?, skipTotal?` — or `document, id` — or `document, ids[]` | `{ rows, totalCount, nextCursorDate, nextCursorSkip, next, ignoredFields, document, sessionId }` — `{ row }` — `{ rows }` (D33) |
| `create` | yes | `docType, body{}, post?=true, exchange?` — body = the old adapter's create body (D38) | `{ id, number, date, posted, created, idempotent, document, requestedDocumentName?, fillDiagnostics?, sessionId }` |
| `update` | yes | `docType, ref, fields{}, post?, autoUnpost?=false` | `{ id, updated, posted, autoUnposted, reposted, fillDiagnostics?, sessionId }` |
| `post` | yes | `docType, ref` | `{ id, posted, number, sessionId }` |
| `unpost` | yes | `docType, ref` | `{ id, unposted }` |
| `markDeleted` | yes | `docType, ref, mark` | `WriteResult` |
| `delete` | yes | `docType, ref, hard?=true` (false = deletion mark) | `{ id, deleted }` / `{ id, marked }` |
| `findOwned` | yes | `docType, prefix` | `{ refs: [guid] }` |
| `sweep` | — | — | `{ retired }` |
| `cancel` | — | `targetId` | `{ cancelled: bool }` |

`WriteResult`: `{ ref, number, kind, sessionId }`. Values: strings, numbers, booleans,
ISO-8601 dates, `null`.

`create` body (D38) — the old keys: `Date` (required, `yyyy-MM-dd[THH:mm:ss]`), `Номер`,
`Комментарий` (required, starts with the AIBA marker), `_idempotencyMarker` (`AIBA_<KIND>_<id>`,
which `Комментарий` must contain followed by `:`), any attribute, `tabularSections` or bare
section arrays. A reference is `{ "type": "CatalogRef.X" | "ChartOfAccountsRef.X" |
"DocumentRef.X" | "EnumRef.X" | …, id | ИНН/tin | Код/code | Номер | Наименование/name |
НомерСчета | Владелец/owner | value }` or a bare string (exact name or code; enum name or
synonym); `type` may be left out when the field has one reference type. A value that cannot be
used refuses the whole document: HTTP 422, `error.kind = "unprocessable"`,
`error.data.fillDiagnostics = [{ code, level, field, message }]` — nothing is written. A 1C
refusal of the write is `Runtime` (422) with 1C's messages in `error.message` and the same
`error.data`. An existing active document carrying the marker is returned with
`idempotent: true, created: false` (201), as it is — a draft is not posted. Reference columns in `read` depend on `refs`: `"text"` (default) —
1C's presentation, computed in the query; `"guid"` — the reference GUID; `"both"` —
`{ "ref": guid, "text": presentation }`. Writes accept scalar fields only (D19).

`catalog` rows have the old adapter's shape and value rules (D33): `id, code, name,
deletionMark, parent, isFolder, Владелец, <attributes>, orgRef, tabularSections`; references
as Наименование → Код → Номер → GUID, enums as value names, dates `yyyy-MM-ddTHH:mm:ss`;
an empty reference or Неопределено is `null`, while an empty date and 1C NULL (an attribute
that does not apply to the row) are `""` — as the old adapter sends them (same rules for
`document` and `register` rows).
`limit: 0` = count only. `after` = GUID of the previous page's last row (zero GUID = first
page); `next` is that GUID for the page just returned, `null` at the end. `totalCount` is `-1`
when skipped (`skipTotal`, or any filter). `fields: []` = base fields only, no tabular
sections; names that are not attributes come back in `ignoredFields`. `filters` are equality
on field paths; `_ownerInn` means `Владелец.ИНН`.

`document` rows: `id, number, date, posted, deletionMark, <attributes>, orgRef,
tabularSections`. A list row has only non-empty sections; `id` / `ids` rows have every
section (`[]` when empty) — the old routes' two shapes. Date-cursor paging: send the previous
page's `nextCursorDate` as `cursorDate` and `nextCursorSkip` as `offset` (same `order`).
Keyset paging: `after` / `next` as for catalogs. `totalCount` counts the `[from, to)` window
only (not filters), `-1` when `skipTotal`. Filters: `date` (a whole day), `period_start`,
`period_end`, `contract_number`, `contract_date`, the aliases `posted`, `deletionMark`,
`number`, or a field path. `ids` returns found documents in request order; unknown ids are
absent. Bank types answer to either configuration's name; `document` in the result is the
resolved one.

`register` rows: every column of `ВЫБРАТЬ *` in order (old keys), accounting rows plus
`СчетДтКод`, `СчетКтКод`, `recorderRef`, `lineNo`, `orgRef`. Paging for every kind: send
`nextCursorDate` as `cursorDate` and `nextCursorSkip` as `offset` (same `order`) while
`hasMore`; `to` is an exclusive upper bound on Период (a cold-read slice).

## 4. Errors

`error` mirrors `OneCException` field for field (D14):

```json
{
  "layer": "Runtime", "hr": "0x80020009", "oneCCode": 1001, "scode": "0x00000000",
  "source": "1C:Enterprise 8.3.15.1565", "message": "Failed to post: \"…\"!",
  "op": "Invoke", "member": "Записать", "base": "kansler", "platformVersion": "8.3.15.1565",
  "retryable": false, "sessionFatal": false, "hostFatal": false
}
```

With `kind: "notFound"`, `notFoundScope` says what is missing: `object` (the table exists, the
GUID does not — the only not-found a sync may turn into a delete) or `metadata` (no such table).

Extra layers for transport-level failures:

| layer | when |
|---|---|
| `Validation` | bad arguments — never reached 1C |
| `Timeout` | the deadline passed |
| `Cancelled` | a `cancel` for this id arrived first |
| `Busy` | host in-flight limit reached (backpressure); `retryable: true` |
| `Transport` | pipe broken, host gone (supervisor side only) |

## 5. Deadlines and cancellation

- Every request has a deadline (`deadlineMs`, default 60 000, max 600 000). The host starts
  a `CancellationTokenSource` with it; `cancel` trips the same source.
- A 1C COM call already running **cannot be interrupted**. Cancellation takes effect before
  the call starts, between calls (e.g. between rows of a read), and while waiting for a
  session or a write gate. A session whose work was abandoned mid-call is retired (D07
  follow-up), never reused.
- The response is still sent (`Timeout` / `Cancelled`), so the caller's `id` always resolves.
- The supervisor applies its own deadline + 5 s; if the host says nothing by then the caller
  gets `Transport` and the host is health-checked.

## 6. Backpressure

- Host: at most `GlobalMaxSessions × 4` requests in flight (32 by default). One more gets an
  immediate `Busy` error — the host never builds an unbounded queue.
- Beyond that, waits are bounded by the pool's `RentTimeout` and the write gates' timeout,
  both inside the request's deadline.

## 7. HTTP edge (supervisor)

Binds `127.0.0.1` only. Every endpoint except `GET /v1/health` requires header
`X-AIBA-Token: <token>`; the token is generated at supervisor start (or passed in by the
process that launches it, e.g. the WinUI app).

| method | path | pipe op |
|---|---|---|
| GET | `/v1/health` | — (supervisor itself) |
| GET | `/v1/hosts` | `stats` on each host + supervisor view |
| GET | `/v1/bases` | — (plan) |
| GET | `/v1/events`, `/v1/activity`, `/v1/supervisor` | — (supervisor's own log, request log, state) |
| GET | `/v1/sync` | — Sync (D42/D48, `run --sync-config f.json`): per base `mode, reason, pendingWork, deadLetters, warning, cursor, lastEventAt, active, lastError, unmappedOrgs`; 404 when not running |
| GET | `/v1/sync/bases/{b}/dead-letters` | — the base's dead letters |
| POST | `/v1/sync/bases/{b}/pause` · `/resume` | — user pause / resume (resume continues an unfinished snapshot) |
| POST | `/v1/sync/dead-letters/{id}/retry` · `/approve` · `/dismiss?who=` | — back to the queue · approve a `needs_approval` delete · accept the cloud as it is (recorded) |
| POST | `/v1/sync/bases/{b}/tables/{t}/rebuild?confirm=true` | — D-3 per-table rebuild; 409 without `confirm=true`, when the target cannot purge (Python v2 backend), or on the shared dev target (refused there, D47) |
| GET | `/v1/sync/bases/{b}/tables` | — per configured table: `table, family, state (missing / waiting / copying / synced), missing, rows, copiedSoFar, pending, failed, lastSentAt` (the base's sync screen) |
| GET | `/v1/sync/http?clear=` | — S12: the target's HTTP calls (`method, path` without ids, `status, ms, requestBytes, responseBytes`) and the armed faults; `clear=true` empties the list |
| POST | `/v1/sync/faults?fail503=&failNetwork=&badToken=&reject400=&crashOnComplete=` | — S12 failure tests, only with `faults: true` on a non-stub target (409 otherwise); injected locally, never sent |
| GET | `/v1/bases/{b}/tables[?details=T1,T2]` | `tables` — every catalog / document / chart / register of the configuration (`table, name, synonym, family, isMovement`; names + synonyms only, host-cached); with `details`, those tables with their family and movement flag read from 1C |
| POST | `/v1/bases/{base}/read` | `read` |
| GET | `/v1/bases/{base}/version` | `version` |
| GET | `/v1/bases/{base}/test` | `test` |
| GET | `/v1/bases/{base}/catalogs/{name}?limit&offset&fields&after&skipTotal&<field>=<value>` | `catalog` (old adapter's query parameters; every other parameter is a filter; `fields=-` = base fields) |
| GET | `/v1/bases/{base}/catalogs/{name}/{id}` | `catalog` with `id` |
| GET | `/v1/bases/{base}/slices/{kind}/{name}?parts&from&to` | `slices` |
| GET | `/v1/bases/{base}/changes?cursor&maxBytes` | — (supervisor reads the event log itself, D36): `{ events: [{ts, kind, metadata, ref}], cursor, reset, resetReason, more, records }`; no cursor = start at the tail; 409 when the log is not readable here (remote server, no log) |
| GET | `/v1/bases/{base}/registers/{name}` (accounting) · `/v1/bases/{base}/registers/{info\|information\|accumulation\|accounting}/{name}` — `?limit&offset&cursorDate&syncOrder&to&skipTotal` | `register` |
| GET | `/v1/bases/{base}/documents/{docType}?limit&offset&fields&cursorDate&syncOrder&tabular&from&to&after&skipTotal&<field>=<value>` | `document` (old adapter's parameters; every other parameter is a filter) |
| GET | `/v1/bases/{base}/documents/{docType}/{id}` | `document` with `id` |
| POST | `/v1/bases/{base}/documents/{docType}/batch` body `{ "ids": [guid…] }` | `document` with `ids` |
| POST | `/v1/bases/{base}/documents/{docType}?post&exchange` body = the document | `create` → 201 |
| PUT | `/v1/bases/{base}/documents/{docType}/{ref}?post&autoUnpost` body = the fields (old PUT) | `update` |
| PATCH | `/v1/bases/{base}/documents/{docType}/{ref}` body `{ fields, post?, autoUnpost? }` | `update` |
| POST | `/v1/bases/{base}/documents/{docType}/{ref}/post` | `post` |
| POST | `/v1/bases/{base}/documents/{docType}/{ref}/unpost` | `unpost` |
| POST | `/v1/bases/{base}/documents/{docType}/{ref}/mark-deleted` | `markDeleted` |
| DELETE | `/v1/bases/{base}/documents/{docType}/{ref}?hard` | `delete` (default: deletion mark, as the old route) |

Header `X-AIBA-Deadline-Ms` sets `deadlineMs`. Response body is the pipe response's
`result` on success, `{ "error": … }` on failure.

| error | HTTP status |
|---|---|
| `Validation` | 400 |
| unknown base / document not found | 404 |
| ownership refusal (not created by AIBA) | 403 |
| `kind: unprocessable` (a write value that cannot be used, D38) | 422 |
| `Runtime` (1C business error) | 422 |
| `Connector` (credentials, infobase, version) | 502 |
| `Busy`, or `retryable: true` | 503 + `Retry-After: 1` |
| `Timeout` | 504 |
| `Cancelled` | 499 |
| `Transport`, `hostFatal` | 503 |
| anything else | 500 |

## 8. Not in v1

Streaming large reads (use `limit` + paging), push notifications from hosts, remote
(non-loopback) access, authentication beyond the local token.
