# Old AIBA Connector — Second-pass audit (final review)

Synthesizes six targeted second-pass reports (A–F, in
`C:\Users\SANJAR~1\AppData\Local\Temp\claude\D--aiba\2f7863d6-b4e3-4c49-b87a-605bfc5fcf27\scratchpad\audit2\`)
against `OLD_CONNECTOR_DEEP_AUDIT.md`, `OLD_CONNECTOR_FEATURE_MAP.md`, and
`OLD_CONNECTOR_HIDDEN_FEATURES.md`. Read-only review; no code run, no network calls, no edits
outside this file and `OLD_CONNECTOR_FEATURE_MAP.md`. Refs used by the six reports: `backend/1c`
`origin/development` @ `b13e47d` (first pass used `b2e3590` — 10+ commits newer), `connector`
`release` @ `2fa385d`/`cfab3a1` (first pass `cfab3a1` — ~1 week newer for B), `aiba-next/backend`
@ `2060486`, `aiba-next/backend-mcp` @ `153ad89`, `kansler/backend`+`frontend` current checkout.

---

## 1. Executive conclusions

1. **The command surface is bigger and more real than the first pass could prove.** Investigator
   A and C traced actual, file:line callers for nearly every command the first pass had listed by
   inference or docstring only — `write_to_1c`, `post_1c_document`, `sync_now`, `unpost/update/
   delete_1c_document`, and `catalog_update` all now have confirmed production callers, several
   of them in aiba-next's native HR and kansler-bank modules that the first pass never looked at.
2. **A 26th command, `adapter_read`, was born and shipped (both sides) in the four days between
   the first pass and this one** — and it is the functional reincarnation of the old adapter's
   raw `/query` escape hatch, now reachable from the cloud side by any of four shared secrets,
   with two real production callers (VAT reporting) already using it as their *primary* data
   path, not a fallback.
3. **Two presence bugs, not one.** The "overwrite" half of the two-socket wipe bug (V6/C2) is
   **now fixed** in backend/1c. The "unregister deletes everything unconditionally" half is
   **unchanged and is now the dominant failure mode** — and, newly traced this pass, it is **not
   self-healing by heartbeat** the way the first pass's model assumed.
4. **The rewrite's multi-org deletion bug does NOT exist in the old connector.** Investigator F
   proved this architecturally: the old connector always reads a base once, whole, and only
   partitions on upload — never on the delete/reconcile key-set computation that the rewrite's
   bug corrupted.
5. **Two real production incidents surfaced as hard evidence, not theory**: KANSLER lost
   `catalog_update` capability for hours (74 ИКПУ cards blocked, 2026-10-05) due to a presence-TTL
   bug (now fixed), and a prior incident on `1c.aiba.group` (2026-09-29) showed the *opposite*
   failure mode of the same bug class (stale routes outliving a dead adapter).
6. **Data coverage (`OneC.dataCoverage`) is accounting-correctness load-bearing**, not a reporting
   nicety — it gates duplicate-document prevention in aiba-next's avtoprovodka write pipeline.
   This single finding should flip the rewrite's D-7 classification of coverage from "drop unless
   consumer" to "required."
7. **A natural-language AI chat agent is a live, unaudited writer into 1C.** cloud-os's
   `AiChatController.php` exposes six business-area `write_to_1c` tools (timesheet, leaves,
   premiums, deductions, payroll, journal entries) plus HR hire, all with `post_document:true`,
   callable from whatever the user types to the chat assistant.
8. **The MCP "no unpost/delete" rule is real but is enforced client-side only.** backend-mcp
   refuses those two tool calls server-side (tested), but backend/1c itself has zero concept of
   "which tool is calling" — a raw curl with the shared `MCP_API_TOKEN` against
   `/document/unpost` or `/document/delete` succeeds unconditionally.

---

## 2. Active cloud commands (~26, full table)

| Command | Real caller | Business feature | Status | Evidence |
|---|---|---|---|---|
| `sync_now` | admin route; aiba-next `onec_pull.rs`; MCP `onec_trigger_sync` | manual/triggered full resync | **ACTIVE** | A: `backend-mcp/crates/api/src/modules/mcp_ext/onec.rs:456-520,1590,1731` |
| `pull_now` / read-table | admin route; Celery `tasks.refresh_report` 03:00; kansler `onec_pull.rs::dispatch_read_table`; aiba-next `onec_pull.rs::pull_table` | targeted table refresh | **ACTIVE** | A (MCP deliberately excluded, tests `onec.rs:2375-2391`); D §1c/1d |
| `write_to_1c` | Odoo inbox; cloud-os avtoprovodka JS; aiba-next HR (`employees.rs` payroll/sync/timesheet); kansler-bank "Prixod" (`kansler_bank.rs`); cloud-os AI chat (`AiChatController.php`) | document create (incl. HR exchange=true docs) | **ACTIVE**, broader than first pass | A §write_to_1c; C "HR documents"/"new finding" |
| `post_1c_document` | admin route; aiba-next kansler-bank EDO "Post to 1C" button | post an existing draft | **ACTIVE** | A: `kansler_bank.rs:8135-8230` |
| `unpost_1c_document` | admin route; MCP `onec_unpost_document` (confirm-gated, default-denied) | unpost | **ACTIVE**, default-denied on MCP only | A: `mcp.rs:146`, `onec.rs:697-723` |
| `update_1c_document` | admin route; MCP `onec_update_document` (header-only, not denied) | header edit | **ACTIVE** | A: `onec.rs:666-696` |
| `delete_1c_document` (`hard`) | admin route (any of 4 tokens); MCP `onec_delete_document` (confirm-gated, default-denied, soft-only) | delete | **ACTIVE**, hard-delete only via raw admin route | A/C: `onec.rs:725-746`, `reject_hard_delete` tested |
| `catalog_update` (CAS) | kansler `ikpu_audit/write.rs:547` | ИКПУ classifier-code repair | **ACTIVE**, prod incident confirms it (74 cards blocked 2026-10-05) | A/B/C cross-confirmed |
| `document_read` | EDO draft-import feature only (connector comment) | bounded live document read | **ACTIVE**, narrower caller than first pass assumed | F §2.2; A downgraded "ACTIVE"→caller-confirmed-only-for-EDO |
| `catalog_read` | EDO draft-import feature only | bounded live catalog read | **ACTIVE**, same caveat | F §2.2 |
| `adapter_read` (NEW, 26th command) | backend/1c `vat_registry.py:355`, `vat_regulated.py:24,114` | VAT/QQS report primary data path | **ACTIVE** — but zero caller found for the admin-API route itself outside these two backend/1c-internal services | A (no caller in aiba-next/MCP/cloud-os) + F (two real backend/1c callers) — see §18 contradiction note |
| `employee_write_to_1c` (hire) | cloud-os `employees.js:6850-6875` | HR hire | **ACTIVE** | C |
| `employee_fire_from_1c` | cloud-os `employees.js:4158-4197` | HR dismissal, exchange=true | **ACTIVE** | C |
| `employee_list_from_1c` | none | — | **BROKEN** (no handler), unchanged | A |
| `holiday_push_to_1c`/`holiday_delete_in_1c` | cloud-os `payroll.js:6466,6477` | leave register writes | **ACTIVE** | C |
| `holiday_check_1c` | legacy NC only, no aiba-next caller | — | **ACTIVE (legacy only)** | A |
| `push_to_1c` / `create_in_1c` / `delete_in_1c` / `delete_from_1c` | **no real caller found** anywhere (literal-string sweep across cloud-os, web ×14, kansler, aiba-next, backend/backend) | — | **ACTIVE CAPABILITY, NO CURRENT SENDER** (narrowed from first pass's flat "ACTIVE") | C §HR documents; A §159-167 |
| `department_write_to_1c` / `department_delete_in_1c` / `refdata_get` / `server_ping` / `connector_status_request` / `check_exists` | legacy NC `aiba_employees`/`aiba_integration` only, zero aiba-next/MCP reimplementation | — | **ACTIVE (legacy-only) / UNREACHABLE** per first pass, reconfirmed dead-end for any modern caller | A §159-167 |
| `list_resource` / `get_list` / `create_ref` / `refdata_get` | HR + sotuv (`aiba-next/backend/crates/accounting/src/modules/sotuv.rs`, `kansler/backend/.../sotuv.rs`) | dropdown resolution, create-on-the-fly refs | **ACTIVE**; `create_ref`'s `_postCreateMethod` RCE-shaped field confirmed **never set** by any real caller | C §create/update/post |
| `get_bases` (connector branch) | none (hub answers instead) | — | **UNUSED**, unchanged | first pass, not re-opened |

---

## 3. Active remote writes/actions

- **create (`write_to_1c`)**: Odoo inbox (create+post only, allow-listed `kind`), cloud-os
  avtoprovodka, aiba-next HR (3 routes), kansler-bank Prixod (3 routes + 1 more call site), cloud-os
  AI chat tool (6 business areas). Never unpost/update/delete from Odoo. C.
- **post (existing draft)**: admin route + aiba-next kansler-bank "Post to 1C" EDO button
  (120s timeout, deliberately longer than backend/1c's own 90s dispatch cap so it never
  false-reports a timeout on a post that actually succeeded). A.
- **unpost / update / delete**: admin route only reachable to the 4 shared secrets
  (`SERVICE_SECRET_KEY`, `ODOO_API_TOKEN`, `MCP_API_TOKEN`, `AIBA_BI_API_TOKEN`), all **equal
  peers** — backend/1c has no per-tool authorization concept. MCP's own dispatcher refuses
  unpost/delete by default (tested) and strips `hard`, but that refusal is **not** enforced by
  backend/1c itself — a direct call bypassing backend-mcp's JSON-RPC layer with the same
  `MCP_API_TOKEN` succeeds. `AIBA_BI_API_TOKEN` is provisioned, documented, equally powerful,
  and has **zero real sender anywhere in the workspace** — a dormant live-fire credential. C.
- **`catalog_update` CAS**: kansler `ikpu_audit/write.rs:547`, confirmed active by a dated
  production incident (74 cards blocked). C/A.
- **`_postCreateMethod`**: exploitable primitive (`Рефлектор.ВызватьМетод` on any common module
  name, no allow-list) confirmed real in the adapter, confirmed passed through unfiltered by
  both the connector and backend/1c's relay — but confirmed **unused** by every real
  `create_ref` sender found (`employees.js`, `sotuv.rs` ×2). Latent, not exercised. C.
- **FORCE-posted bank docs**: real, line range corrected to `main.os:17354-17503` (first pass's
  `17194-17252` was a different diagnostic-fallback block, not the FORCE path). C.
- **HR exchange=true docs**: 9 document types forced into `DataExchange.Load` mode regardless of
  caller. Real senders confirmed for hire, fire, holiday push/delete (cloud-os legacy JS) **and**
  a second, previously-unknown production entry point: aiba-next's native payroll "send-1c" route
  hits the same forced-exchange document type. C.
- **Catalog hard-delete**: exists in the adapter (`main.os:23028-23037`) but the connector's only
  catalog-delete caller never appends `?hard=true` — reachable only via direct LAN/adapter access,
  never via any cloud command today. C (new finding).
- **Document hard-delete**: fully wired end-to-end through the cloud command path, `hard` taken
  verbatim from payload with no restriction. Confirms first pass V12. C.

---

## 4. Remote reads

| Read | Live or stored? | Row/size cap | Table restriction | Real caller |
|---|---|---|---|---|
| `document_read` | **live** (fresh adapter/COM call) | 200 rows (connector-enforced) | must start `Document_`; no sync-config check | EDO draft-import only |
| `catalog_read` | **live** | 200 ids/call (enforced both sides) | any catalog name; no sync-config check | EDO draft-import only |
| `adapter_read` GET (NEW) | **live** | **none** — caller's own `limit`/`offset` passed verbatim | path root restricted to `catalogs/documents/enums/schema/metadata/calcTypes` | none found as a cloud-command caller |
| `adapter_read` QUERY (NEW) | **live** | **none** (row count); 20,000 chars of query text | must start ВЫБРАТЬ/SELECT, otherwise arbitrary 1C query language, bypasses org-binding routing entirely | **backend/1c's own `vat_registry.py`/`vat_regulated.py`** — real, primary-path, production VAT reporting |
| `/by-type` entity reads (stock/coverage) | **stored** (as-fresh-as-last-sync) | — | — | aiba-next `warehouse.rs` (stock), `sotuv.rs` (coverage) |
| raw adapter `/query` (old-style, fixed internal text) | **live** | none; swallows all errors, HTTP 200 on failure | none | superseded in significance by `adapter_read` QUERY, which exposes the same route to **caller-controlled** text, not just fixed internal strings |

The first pass's §8 claim "`/query` used only with fixed internal texts" is **superseded**:
`adapter_read`'s QUERY mode is exactly a caller-controlled `/query` proxy, reachable by any of
the four shared secrets, with two real production callers already treating it as primary.

---

## 5. Presence model (field table)

| Field | Classification | Notes |
|---|---|---|
| `connectorId`, `deviceId`, `userId`, `appVersion` | **STORED** (90s TTL) | — |
| `capabilities.commands` | **STORED, two tiers**: 90s record + a new 30-day fallback key (`connector-commands:{id}`) | Added by commit `2c91c95`, fixing the KANSLER capability-loss incident |
| `1C base list` (`bases[]`) | **STORED** (90s, inside `infobases`) | command socket only |
| per-base `status` | **STORED**, defaults to `"not_connected"`, never silently `"connected"` (fixed) | — |
| per-base `version` (1C config version) | **STORED (HTTP side only)**, not on the WS record | `PATCH /onec/connection/{id}` every 60s |
| `lastSeen` | **STORED while online only** — gone instantly on delete/expiry, no history | — |
| `ip` (socket peer address) | **OBSERVABLE, STORED 90s, NOT NECESSARILY REAL** | no XFF handling on this path at all |
| `app_mode`/`role`/`tenant_id`/`session_id`/`protocol_version`/`connected_base_count` | **DROPPED** — accepted into payload, never written to the Redis record | — |
| `hostname`, OS, LAN IP, 1C platform version (live), `adapter.version`, `connectedSince`, offline history | **NOT AVAILABLE** | never sent, never stored, anywhere |

---

## 6. Device identity model

Unchanged since the first pass — re-verified against newer refs, zero commits touched the
relevant files. `deviceId = machine_uid::get()` → `HKLM\...\MachineGuid`, no salt/hash, cached in
zustand `persist` and never re-read from Rust once cached. `connector_id = "${user_id}:${device_id}"`,
unverified by the server (client-declared, no cross-check against owned bases). Stability matrix:
stable across restart/upgrade/reinstall/different-Windows-user-same-PC; **colliding** across
cloned VMs (shared registry key), multiple processes/sessions same user, and copied user profiles
(stale cached GUID never revalidated against the new machine).

---

## 7. IP / network visibility

`ws.client.host` (raw ASGI peer address, no header inspection) is the only IP source for the
presence/command socket. The one XFF-aware function in the repo (`odoo_audit.py`) is wired **only**
into admin-API partner-token auditing, never into `connector_ws.py`/`cloud_router_ws.py`. Whether
`ws.client.host` is the real end-user IP in production is **unknowable from code**: no
`--proxy-headers` flag is passed anywhere committed, the Dockerfile has no CMD/ENTRYPOINT, and the
actual swarm service-create command (where any proxy override would live) is not in any repo.
LAN IP: never sent, never stored, anywhere.

---

## 8. Production socket exposure

**Proven**: the FastAPI app (`main.py`) applies zero authentication to `/api/v2/connector/ws` or
the legacy `/ws/1c` route — no `dependencies=[...]` on either router, only global CORS
(`allow_origins=["*"]`) and an audit-only middleware that doesn't gate anything.
**Unknowable from code**: whether an edge/reverse-proxy layer in front (confirmed to exist, by a
code comment about WS-upgrade-header handling differing by path) adds IP allowlisting or mTLS —
no nginx/Traefik/ingress config exists in `backend/1c`, `connector`, or `aiba-next/infra`.
**Strong circumstantial conclusion, not proof**: cloud-os's PHP controllers hand this exact WS URL
directly to end-user browsers, and a browser cannot attach a custom auth header to a WS handshake
— so for those flows to work, the socket must accept unauthenticated connections from the public
internet. Resolving this fully requires `docker service inspect` on the real swarm manager, which
is outside any checked-out repo.

---

## 9. Relay architecture (relay.aiba.uz)

**Not implemented anywhere in this workspace** — confirmed by an exhaustive grep across all ~15
most-likely checkouts. It is operated outside the audited repos ("Qo'lda backend" / hand-run, per
the connector's own config comment). The connector generates its own bearer token (not issued by
the relay) and the operator pastes it into the relay's own Key Manager UI — binding happens
entirely outside this workspace. Once connected, the connector trusts **any** inbound frame with
no second credential check. The HTTP-relay path is scoped to exactly two fixed local targets
(`:7777` bank-signer admin, `:6210` crypto helper) — not an arbitrary port — but within those two,
method/path/query/body are fully unauthenticated and unrestricted. `:7777` is bank-connectro's own
~100-route admin API, including `create-payment` for all 10 integrated banks, protected only by
"whoever can reach loopback" (which the relay satisfies by construction) plus one state
precondition (an already-active bank login session — not a security control). The tunnel is
unconditionally spawned in the `release` build and idles only until a token + "Bank key manager"
switch are both configured. General tunnel in effect for those two services, not a payment-scoped
channel by design — payment reachability is real whenever the operator keeps the bank connected.

---

## 10. Multi-org behavior — does the old connector have the rewrite's bug?

**No, proven architecturally, not by luck.** The rewrite's bug (`DECISIONS.md` D46): a per-org
partition read combined with a connection-wide (`scope=connection`) delete, so a narrow key set
deleted other orgs' rows. The old connector reads each physical base **exactly once per cycle**,
always keyed by the connection's own id, and only ever partitions rows **after** the delete/
reconcile comparison (for upload-destination routing only). Every reconcile/prune/refids/refhashes
call sends a base-wide key set with `scope=connection` — matching exactly the precondition
backend/1c's own guard (`_scoped_onec_ids`, HTTP 400 on a binding id with `scope=connection`)
requires. A real but *different* limitation remains: org-less movement rows and re-mapped
documents can leave stale/invisible copies in the wrong partition — a staleness bug, not a
cross-org deletion bug, and backend-acknowledged already (`_stranded_movement_scan`).

---

## 11. Sync table operational behavior

**One write door** (`PUT /api/v1/onec/{id}/sync-tables`, `onec.py:898` is the only Mongo write
site in the whole repo), but **three real callers** reach it: the connector's own drawer (manual,
no follow-up pull — tells the user to wait ~60s), the connector's own auto-seed (automatic,
compiled-defaults-only, once per base per app session), and — **new, not in the first pass** —
kansler's GTD-sync panel (`ved_sync.rs`, a real accountant-facing customs-declaration feature)
which merges 6 GTD tables into the list and immediately pairs the PUT with a high-priority
`pull_now`, then a 15-minute background sweep re-dispatches at low priority until rows land. A
fourth, generalized version (`request-tables`) is built and reachable but has **zero UI caller**
anywhere — an unaudited write surface into any connection's sync config.

**The `[]`-vs-`null` bug is confirmed at the exact Python-truthiness level**
(`stored if stored else None` collapses both "never configured" and "deliberately emptied" to the
same `{"tables": null}` response) and confirmed to bite from both directions in production:
the connector's auto-seed silently reinstates the full default list within ~60s of an operator
emptying it, and kansler's `ved_sync::enable` independently had to build a 409-refusal defense
against the identical ambiguity — i.e. two unrelated codebases have already hand-rolled the same
workaround, which is itself evidence this is a repeatedly-hit production issue, not theoretical.

**`schedule` is confirmed dropped on every single PUT** — `SyncTableEntry` has no `schedule`
field at all; Pydantic silently discards it before any sync-table logic runs. The connector's
drawer caches it client-side to mask this within one browser session, but any fresh GET (reopened
drawer, poller auto-seed, kansler's merge) reverts every table to its prefix-based default.

**Fetch cadence**: 60s main poller floor per base; cloud `sync_now`/`pull_now` and kansler's
`dispatch_read_table` re-read sync-tables config fresh, no cache; the event-log driver's 60s cache
is real code but dead in production (`isEventLogSyncEnabled()` hardwired `false`).

---

## 12. Stock/coverage/retail/report consumers

| Feature | Status | Real consumer |
|---|---|---|
| Stock snapshot (`STOCK_BALANCE`) | **REQUIRED** | aiba-next `warehouse.rs::stock_1uz` + shipped frontend tab "1UZ jonli qoldiq" (`stock-1uz.tsx`), gated to 1uz/BePro-base companies, full i18n |
| Data coverage (`OneC.dataCoverage`) | **REQUIRED — LOAD-BEARING FOR ACCOUNTING CORRECTNESS** | aiba-next `sotuv.rs::mirror_reach_from_onec` — decides whether "document absent from mirror" is trustworthy before avtoprovodka creates a new document; without it, a partial backfill could cause duplicate accounting documents |
| Retail turnover rollup (`retail_daily_rollup`) | **DEAD-LEANING / UNKNOWN** | zero caller found anywhere audited (aiba-next frontend/backend, cloud-os, web ×14, backend/report, backend/backend) — the machinery (ingest, Mongo collection, indexed reader, admin rebuild, Celery refresh) is real and maintained but answers no found question; the one unchecked gap is the separate production `aiba-cloud` tenant-API service, not a local checkout |
| Raw retail tables (register + document) | **LIKELY REQUIRED** | wired into aiba-next's External API / BI data-export product (`onec_entities.rs` → `extapi.rs`/`extapi_catalog.rs`) as grantable endpoints, generated from the same list driving real per-tenant grants; no dedicated dashboard found but it's a live generic product surface |

Data coverage is not surfaced as any user/support-visible health indicator today — the one place
it matters is invisible to anyone debugging a sync issue.

---

## 13. Operational desktop features

No second-pass investigator was tasked with or surfaced new desktop-feature findings beyond what
the first pass already covered — nothing to add here.

---

## 14. Features DEFINITELY required in new Connector (real production dependency)

- **`write_to_1c` / document create**, across at least 5 independent real production surfaces
  (Odoo, cloud-os avtoprovodka, aiba-next HR, kansler-bank Prixod, cloud-os AI chat).
- **`post_1c_document`**, confirmed by two independent real UI flows.
- **Data coverage (`OneC.dataCoverage`)** — accounting-correctness duplicate-prevention guard,
  proven load-bearing by `sotuv.rs::mirror_reach_from_onec`.
- **Stock snapshot** — proven load-bearing for a real, shipped warehouse feature for 1uz-base
  customers.
- **Multi-org upload partitioning + base-wide delete/reconcile key-set discipline** — proven to be
  the exact mechanism that prevents the rewrite's known cross-org data-loss bug; must be preserved
  precisely (read-once-whole, partition-only-on-upload), not just "multi-org support" in general.
- **`catalog_update` CAS** — confirmed real caller, confirmed real production incident dependency
  (KANSLER ИКПУ, 74 cards blocked).
- **`sync_now`/`pull_now` fast-targeted-pull pairing** (the kansler GTD-sync pattern) — proven to
  be the only mechanism that makes a newly-added sync table usable in seconds rather than hours;
  two independent, differently-shaped implementations of this already exist in production
  (kansler's merge-then-PUT-then-pull_now, aiba-next's pull-regardless-of-list) and should be
  unified, not dropped.

## 15. Features LIKELY required (real but less certain evidence)

- **`adapter_read` QUERY mode** — two real backend/1c-internal production callers (VAT reporting)
  depend on it as a *primary*, not fallback, data path; but the admin-API route itself has zero
  cloud-side caller, so whether the rewrite needs an equivalent cloud-reachable raw-query escape
  hatch is a product decision, not a proven requirement.
- **Raw retail entity slugs** (`retail-sales`/`retail-revenue`) — wired into a real generic
  product surface (External API) but no dashboard confirmed consuming them yet.
- **`document_read`/`catalog_read`** — real caller (EDO draft import) but narrow/single-purpose;
  worth preserving for that one feature, not as a general escape hatch.
- **`employee_write_to_1c`/`employee_fire_from_1c`/`holiday_push/delete_to_1c`** — real legacy
  callers (cloud-os NC apps), but zero modern (aiba-next/MCP) reimplementation exists — "maintained
  blind" per investigator A; likely required only if the legacy HR product surface itself survives
  the rewrite.

## 16. Features CONFIRMED DEAD (exhaustive grep, zero caller)

- `push_to_1c` / `create_in_1c` / `delete_in_1c` / `delete_from_1c` — capability live end-to-end,
  zero sender found anywhere (literal-string sweep cannot rule out a dynamically-built action
  name, so treat as "not found," not "proven absent").
- `employee_list_from_1c` — BROKEN, no handler at all (unchanged from first pass).
- `_postCreateMethod` — exploitable primitive, confirmed unused by every real caller.
- Catalog hard-delete via cloud command — exists in the adapter, unreachable via any current cloud
  command (the connector's only catalog-delete caller never appends `?hard=true`).
- Retail turnover rollup read side — real machinery, zero found consumer (see §12 caveat on the
  unchecked aiba-cloud tenant-API service).
- `request-tables` generic sync-table route — built, reachable, zero UI caller.
- `get_bases` (connector branch) — unused, hub answers instead (unchanged).

---

## 17. New discoveries this pass made that the first pass missed entirely

1. **`adapter_read`** — an entire 26th command, two-sided (backend/1c + connector), shipped four
   days after the first pass, with real production callers (VAT reporting) already treating it
   as a primary data path. This supersedes the first pass's "`/query` used only with fixed
   internal texts" claim.
2. **cloud-os's AI chat tool-call write path** (`AiChatController.php::toolOnecWrite`) — a
   natural-language agent that can trigger `write_to_1c`/`employee_write_to_1c` with
   `post_document:true` across six business areas. The first pass never looked at this surface.
3. **aiba-next's native HR module as a `write_to_1c` caller** (`employees.rs`: payroll send-1c,
   roster sync, timesheet write) — including a second, previously-unknown production entry point
   into the forced-`exchange=true` document-type list.
4. **aiba-next's kansler-bank "Prixod" flow as a `write_to_1c` caller** (`kansler_bank.rs`), and
   as a `post_1c_document` caller via its EDO reconciliation "Post to 1C" button.
5. **kansler's GTD-sync panel (`ved_sync.rs`) as a real second writer of `syncTables`**, with its
   own independent 409-refusal defense against the `[]`-vs-`null` ambiguity, and its own 15-minute
   background re-dispatch sweep (which itself had a production incident — see §19).
6. **The `request-tables` generic route** — built, reachable, completely unused by any UI.
7. **Data coverage's accounting-correctness load-bearing role** (`sotuv.rs::mirror_reach_from_onec`)
   — the first pass left this as UNKNOWN; this pass found the specific duplicate-prevention
   mechanism that depends on it.
8. **Stock snapshot's real consumer** — a shipped, fully-built warehouse page
   (`stock-1uz.tsx`, "1UZ jonli qoldiq" tab), gated correctly by base type.
9. **The exact mechanism and bypass condition of the MCP unpost/delete exclusion** — enforced only
   inside backend-mcp's own dispatcher, not by backend/1c, and overridable by a manually-inserted
   `km.mcp_grants` row.
10. **`AIBA_BI_API_TOKEN` is a dormant, fully-privileged, unused credential** on two services
    (backend/1c, backend/report) — provisioned and documented, zero real sender anywhere in the
    workspace.
11. **The exact precision of the presence-TTL capability bug** and its own real production
    incident (KANSLER, 74 ИКПУ cards blocked) and its fix (`connector-commands:{id}`, 30-day
    parallel key) — the first pass's model predates this bug and its fix entirely.
12. **The unregister-on-close presence bug is not self-healing via heartbeat** — a materially
    worse characterization than the first pass's model, which assumed the surviving socket's next
    heartbeat would restore the record within ~30s.
13. **`relay.aiba.uz`'s HTTP-relay path is scoped to exactly two fixed local targets, not an
    arbitrary port** — correcting an ambiguity in how broad the first pass characterized this.
14. **The multi-org architecture proof** — the first pass raised the question; this pass is the
    first to trace the full read-once/partition-on-upload-only mechanism and prove the rewrite's
    bug class cannot occur in the old connector.

---

## 18. Corrections to the first-pass audit

- **`document_read`/`catalog_read`**: first pass classified ACTIVE based on the route's position/
  shape in the admin router, with "EDO draft import" as an inferred caller. This pass's
  investigator A found **zero caller anywhere** by direct grep and downgraded to
  "UNKNOWN / caller not found," while investigator F, reading the connector-side code comments
  directly, found the caller named explicitly in the connector's own source
  (`onec-sync-provider.tsx:13448-13450,13653-13656`, "the EDO draft-import feature"). **F is
  correct and more thorough here — A's negative grep swept the wrong side of the wire (backend/1c
  server code, where there is no caller comment) rather than the connector's own handler
  comments.** Correct classification: ACTIVE, narrow single-purpose caller (EDO draft import),
  not a general-purpose read path.
- **FORCE-posted bank docs line range**: first pass cited `main.os:17194-17252`; that range is a
  different diagnostic-fallback block. The actual `[CREATE][FORCE]`-tagged block is
  `main.os:17354-17503`.
- **`/query` "used only with fixed internal texts"**: superseded — `adapter_read`'s QUERY mode is
  a caller-controlled raw-query proxy that did not exist at first-pass time; the claim was
  accurate for the code as it stood on `b2e3590` and is no longer accurate on `b13e47d`.
- **"MCP excludes unpost/delete" (workspace memory `project_mcp_server_local.md`, cited generally
  by the first pass)**: sharpened from "enforced only in aiba-next" (inferred) to an exact
  mechanism — `DEFAULT_DENIED_TOOLS` in `backend-mcp/crates/api/src/modules/mcp.rs:146`, which is
  a default applied only to grants the dispatcher itself mints, overridable by a manually-created
  `km.mcp_grants` row, and **not enforced by backend/1c at all**.
- **`push_to_1c` (departments/schedules)**: first pass tagged flatly ACTIVE. This pass narrows
  that to "reachable end-to-end, not currently exercised by any found sender" — a materially
  weaker claim than ACTIVE implies for a rewrite-priority decision.
- **Stock/coverage/retail turnover**: first pass left all three as UNKNOWN pending a proven
  consumer. This pass resolves two of three (stock: REQUIRED; coverage: REQUIRED and
  load-bearing) and narrows the third (retail rollup: DEAD-leaning, not UNKNOWN — a real search
  was done and came back empty, which is a stronger claim than "nobody looked yet").
- **Two-socket presence wipe bug (V6/C2)**: first pass described one mechanism (overwrite). This
  pass found the overwrite half is now fixed, and the previously-undescribed unregister-on-close
  half is the one that actually dominates in current code, with a materially different
  (non-self-healing) recovery characteristic than the first pass's model implied.

---

## 19. Production incidents found

1. **KANSLER, 2026-10-05**: every `catalog_update`-class capability check failed
   `connector_unsupported` for hours; comment states "74 cards ready to write blocked." Root
   cause: `capabilities.commands` lived only in the 90s presence record, and a presence-only
   heartbeat from the other socket (or a hub restart) recreated the record without it. Fixed by
   commit `2c91c95` (parallel 30-day `connector-commands:{id}` key). This is the single clearest
   piece of field evidence that `catalog_update` is live in production, even though no
   investigator could locate its calling frontend code.
2. **`1c.aiba.group`, 2026-09-29**: a connector reporting all 11 bases dead still owned all 11
   routes for some period — the inverse failure mode of the overwrite-bug fix, itself also fixed
   by making a heartbeat's own base list win when present.
3. **Kanstik, undated (fixed, referenced in a code comment)**: an unescaped `[` in a retail
   idempotency marker like `[AIBA:36498]` turned a SQL-LIKE wildcard into a character-class match,
   hitting 543 unrelated creates in one run. Now escaped — a defensive fix, not a live bug, but
   evidence the idempotency-marker matching logic has already caused real incorrect-match damage
   once.
4. **MIRL, 2026-09-12**: kansler's GTD-sync 15-minute background sweep kept aborting a live,
   human-triggered pull every 15 minutes. Fixed by making the sweep's re-dispatch low-priority so
   it never preempts a live operator-triggered pull.
5. **KANSLER, 2026-10-06**: a busy connector timed out a single VAT-registry `adapter_read` QUERY
   call (`purchase_goods: timeout`); the month's VAT report fell back to synced documents instead
   of the live read. Evidence the live-query-as-primary-path design for VAT reporting has a real,
   already-observed failure mode, with a working fallback.

---

## 20. Remaining unknowns and what would resolve each

1. **Who/what calls `catalog_update` in KANSLER prod?** No investigator found the caller despite
   exhaustive grepping of every checked-out repo (including `.js`/`.vue`-only kansler frontend
   paths this pass's grep may have under-covered). Resolve by: grepping the kansler frontend's
   actual deployed bundle for the literal string `catalog_update` (not just `.ts`/`.tsx` source),
   or asking whoever owns the ИКПУ card-review tool directly.
2. **Does an edge/reverse-proxy in front of `1c.aiba.group` add IP allowlisting, mTLS, or any
   auth to the connector WS?** Not resolvable from any checked-out repo. Resolve with
   `docker service inspect aiba_1c --format '{{json .Spec.TaskTemplate.ContainerSpec}}'` on the
   real swarm manager, or a targeted grep of the ~15 sibling checkouts not yet searched
   (`web/*`, `landing/`, `odoo/`, scratch dirs) for a config naming this host/path.
3. **How often does the unregister-wipe presence bug actually fire in production, and for how
   long does each occurrence leave a connector's bases unroutable?** Resolve by grepping
   `aiba_1c` container logs for `"connector disconnected: id="` paired with how long the matching
   `connectorId` stays absent from `GET /api/internal/admin/connectors`, and whether writes in
   that window returned `connector_offline`.
4. **Does the 30-day `connector-commands:{id}` fallback ever go stale in a way that matters** —
   e.g. a downgraded connector build still reporting an old capability as supported for up to 30
   days? Needs either a deliberate downgrade-and-observe test, or a log grep for
   `connector_unsupported` immediately followed by a successful call from the same `connectorId`
   within 30 days of a known downgrade.
5. **Does the retail turnover rollup have a real consumer in the separate production `aiba-cloud`
   tenant-API service** (not a local checkout — its MCP tools are live and were correctly not
   queried under this audit's read-only rule)? Resolve by asking whoever owns that service whether
   it calls `GET /api/v2/reports/retail-revenue`.
6. **Does the AI chat write path (`AiChatController.php::toolOnecWrite`) have its own
   confirmation gate, rate limit, or audit trail equivalent to the human UI's confirm dialogs?**
   Not examined beyond confirming the call sites exist. Needs a dedicated read of
   `AiChatController.php`'s surrounding request-handling code.
7. **Who operates `relay.aiba.uz`, and what is its own auth/logging/tenant-binding integrity?**
   Genuinely outside this workspace — the relay's source is not checked out anywhere under
   `D:\aiba`. Needs access to that service's own repo or its operator.
8. **Is `relay.aiba.uz` reachable from the public internet?** Same as above — unresolvable from
   code in this workspace.
