# Shared DEV — Rust onec backend cut-over runbook

Prepared 2026-10-08. **Not executed.** Needs the developer's go and the access in §0. Development only:
never `*.aiba.group`, never a production tenant, never a client's base. Writes only on test-owned
objects (`AIBA_REWRITE_` / `SYNC-TEST`) in a test base. Every step lists what proves it; a step that
cannot be proven stops the run (§6).

Proven locally first (MIGRATION_STATUS, R0–R10): Rust = 1C on bilim and multi-org KAN, the document
lifecycle, normal writes and customer-owned typed writes (D53), crash/replay, per-base switch and
rollback, coverage.

## 0. Prerequisites (access the agent does not have)

| # | What | Who | Why |
|---|---|---|---|
| P1 | The onec module built from `next-modules` `feat/sync-api-v1` deployed to **one dev tenant** (module registry, `next-module` skill). Its migrations add `entity_data.source_version`, `recorder_key`, `onec.sync_batch`, `onec.command` (+ `lease_token`), `onec.sync_meta`, and run the one-time venkon type relabel | developer | the API under test |
| P2 | A way for the desktop Connector to reach `/api/sync/v1/*` on that module **with the user's own JWT** (the module checks tenant, audience and company access itself, R7). Today aiba-next's proxy only forwards `/api/v2/1c/*` → `/api/v2/*`, and it swaps the user's token for the service secret (security finding, separate task). Either a dev-reachable module URL, or an aiba-next passthrough `ANY /api/sync/v1/*` that forwards `Authorization` and `X-Tenant` unchanged and adds no secret | developer (aiba-next) | without it the new Connector cannot talk to the dev module |
| P3 | Dev tenant data: one test company; one test 1C base (a bilim copy on the test PC); a Rust connection for it named `SYNC-TEST …` (`POST /api/v1/onec`), and for multi-org two bindings (`PUT /api/v2/onec/{id}/org-bindings`) to two test companies | developer + agent | the record under test |
| P4 | aiba-next dev tenant's `onec_module_url` = that module | developer | real consumer reads go to Rust |
| P5 | The OLD Connector OFF for that base (D-1). The engine also refuses while the module's hub sees an old-Connector socket for the connection (`/presence`) | developer | one writer per base |
| P6 | aiba-next background jobs on `scoped_for` (`avtoprovodka_check_scheduler.rs:104`, `nomenclature.rs:2854`, `kansler_bank_write.rs:906`) — otherwise they read central Python during the test | developer (aiba-next) | consumer reads must hit Rust |
| P7 | Connector code: a guarded `rust-dev` target (see §0.1) | agent, after P1–P2 give the URL | today the Rust target is loopback-only by design |
| P8 | `feat/sync-api-v1` rebased on the module's `master` (10 commits ahead, incl. EdDSA verification); `sync::check_token` uses master's key set (`AIBA_JWT_PUBLIC_KEY`) | agent (rebase, local) + developer (approve) | dev/prod aiba-next signs EdDSA; the branch verifies HS256 only, so every user token would be refused |
| P9 | The Desktop signs in to **aiba-next** (today it signs in to the legacy `api.aiba.group` cloud, whose tokens the module does not accept) | developer decision + agent | the Connector's user token is what the module authorizes |

### 0.1 The `rust-dev` target to build (after P1/P2)

Mirrors the S12 `dev` target guards (`DevBackend`): the URL host must be the one dev host given by the
developer, refused if it ends in `.aiba.group`; the token is the signed-in desktop user's Development
session (`BearerCredential`, one refresh on 401), never written anywhere; the connection's name must
start with `SYNC-TEST`; `allowWithOldConnector` and table rebuilds refused; `"commands": true` only when
the run says so; exactly one base per run. `sync-verify` gets a `--rust-dev` mode reading
`/api/sync/v1/connections/{id}/rows` with the same token.

## 1. Baseline (read-only)

1. `GET /api/sync/v1/capabilities` (user JWT) → 200; `GET …/connections/{id}/config` lists the
   partitions (shared + bindings).
2. `GET …/presence` → `otherConnectorOnline: false`.
3. Record the 1C side: per table `COUNT` (DevBench `count` or the host's count op).

## 2. Direction A — 1C → Connector → Rust → real dev consumer reads

| Step | Action | Proof |
|---|---|---|
| A1 | Start the Supervisor with `target: rust-dev:<url>`, the 8 bilim tables (chart, Банки, Контрагенты, Номенклатура, ПТУ, Реализация, Хозрасчетный from a window, КурсыВалют) | Status reaches `incremental`, 0 dead letters |
| A2 | `sync-verify --rust-dev` | **PASS**: every stored row = a fresh 1C read field by field; 0 missing / extra / wrong partition |
| A3 | Counts: `GET …/counts` and aiba-next `GET /api/v2/onec/{id}/counts` | equal to 1C `COUNT` per table (register: within the window) |
| A4 | Coverage: aiba-next `GET /api/internal/admin/onecs/{id}` | windowed tables `complete:false, dataFrom:<window>`; whole tables `complete:true`; on every partition of a movement table |
| A5 | Online: aiba-next base picker / `GET /api/v1/onec?companyId=` | `status: active` within 60 s of start (heartbeat); stop the Supervisor → `inactive` at once (`stopped`), or within 180 s if killed |
| A6 | Consumer reads in aiba-next dev (UI or the same routes with a dev JWT): avtoprovodka `source-counts` (PAYMENT_ORDER totals) and the onec_txn list; `/api/v2/onec/companies/{c}/customer-orders`, `/trading-points`; sotuv catalog search / kontragent search; reconciliation summary (`lastSyncedAt` fresh, `syncStale:false`); `last-by-counterparty`; match-index (409 then 200) | each equals what 1C holds for a sampled object (name, number, amount, date); no 502 |
| A7 | Documents / catalogs / registers / movements: in 1C (test base) create and post a test-owned document by hand, rename a test-owned catalog item, unpost/repost | Rust changes within ~30 s each; its movements = exactly 1C's lines; A2 PASS after each. Known gap: renaming a catalog item does not refresh the NAME inside documents that use it (§5) |
| A8 | Multi-org: two bindings; a document of each organisation | each lands in its binding's partition; catalogs only in the shared one; by-type with a binding id returns its own movements + shared catalogs; A2 reports 0 wrong partition |

## 3. Direction B — backend command → Connector → 1C → Sync → Rust

aiba-next does not call the command queue yet (its writes go through the hub relay for the old
Connector; see the retirement plan, item W). This direction is driven with the queue API directly
(dev JWT), exactly as `tools/rust-r9*.ps1` do locally.

| Step | Command | Proof |
|---|---|---|
| B1 | refused before storing: no marker, `exchange`, `_postCreateMethod`, `document.delete`, `catalog.update` without `expected` | `enqueued:false`, nothing stored, nothing in 1C |
| B2 | `document.create` (draft, `AIBA_REWRITE_DEV_…` marker) → same key again → new key, same marker | one command; one 1C document; Rust converges; A2 PASS |
| B3 | `document.post`, `document.update` (date, `autoUnpost`), `document.unpost`, repost, `document.markDeleted` | each `succeeded`; Rust changes (Sync, not the result); A2 PASS after each |
| B4 | Customer-owned (test base only): `document.post` / `unpost` by exact ref; `document.update` with `expected` (CAS) and back; blind / stale / marker-claiming updates refused; `markDeleted` refused | as `rust-r9-customer.ps1` (D53) |
| B5 | `catalog.create` (marker) → CAS rename → stale CAS refused → CAS back | Rust converges; A2 PASS once the name is back |
| B6 | Lease safety: a report with a wrong `leaseToken` | 409 `lease_not_held`, command unchanged |
| B7 | Cleanup: the test documents and items deleted through the local edge's owned delete | Rust converges; A2 PASS |

## 4. Rollback (prove it, not just write it)

1. Stop the Supervisor → Rust shows the base `inactive`.
2. If the base was on the old Connector before: start the old Connector (NEXT mode, it uploads into the
   same Rust module through the legacy routes). Rows the new Connector wrote and rows the old one
   writes share keys (`recorder_key` backfill, R7); the old Connector re-sends what differs.
3. If the base was on backend/1c: `sync.json` naming the Python dev target with `"switch": true` —
   that backend resumes from its own cursor and catches up (R10 proven locally); then back with another
   explicit switch.
4. Proof: A2 against the backend that is active after the rollback; A5 shows the right Connector.

## 5. Known gaps carried into the test (not blockers for dev, written down)

- **Reference display names inside documents.** Document table parts store references as 1C
  presentations (`"Номенклатура": "Консалтинговые услуги"`), not refs. Renaming a catalog item changes
  what 1C shows in every document that uses it, without changing those documents, so Sync re-reads
  none of them — Rust keeps the old name until each document changes. The old Connector has the same
  row shape and the same gap. Seen in R9 (20 documents after one rename, back to PASS after the name was
  restored). A fix needs a product choice (refs next to names, or a re-read of referencing documents
  on a catalog rename).
- The new Connector does not appear in the hub-based connector roster (`/connectors`); the picker falls
  back to `status` (now correct).
- `by-type?counterparty=` scans instead of seeking (performance on very large partitions).

## 6. Abort immediately (stop the Supervisor, report, change nothing)

A2 FAIL that is not the §5 gap; any write outside test-owned objects; a 5xx loop or a dead letter;
`otherConnectorOnline: true`; any response from a `*.aiba.group` host; a refusal that changed 1C; a
command `succeeded` whose change never reaches Rust within 20 min.
