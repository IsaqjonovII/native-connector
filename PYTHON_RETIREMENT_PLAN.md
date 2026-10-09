# backend/1c (Python) retirement plan

2026-10-08. **Nothing is removed yet.** Three stages; each has exact blockers that must be closed and
proven before the next. Sources: the consumer audits `research/rust-backend-audit/D-aiba-next-consumers.md`
(aiba-next) and `E-other-consumers.md` (KANSLER, report, partners, legacy apps); local proof in
MIGRATION_STATUS (R0–R10). Python reference = `backend/1c` `origin/production` @ 9b4a50b (local
`development` is 153 commits behind prod).

Where traffic comes from today:

- **aiba-next request handlers** → the tenant's Rust module only (no Python fallback; a tenant without a
  module gets 502). **Three aiba-next background jobs** still call central Python.
- **Old Connector** → Python (`aiba-1c.aiba.group` hub/upload) for tenants not on aiba-next, and the
  Rust module (NEXT mode) for aiba-next tenants.
- **KANSLER** (`kansler/backend`, mgi.aiba.group) → per tenant: Rust when `onec_module_url` is set,
  else Python.
- **Others:** central `backend/report` (retail-revenue, UUID company id), legacy `web/dashboard` and the
  `backend/backend` AI 1C agent (statistics / cached metrics), Odoo / AIBA BI partner tokens
  (`odoo-inbox`, `cloud-write*`, `build-write-payload`).

## Stage 1 — DEPRECATED (Python still serves; nothing new is built on it)

Done when every item is closed:

| # | Blocker | Owner | State |
|---|---|---|---|
| 1.1 | Rust must-have gaps for aiba-next: base online from the new Connector, entity-data `total`, venkon types CUSTOMER_ORDER / TRADING_POINTS / INQUIRY_SOURCES, by-type `ref_in` / `owner` / `number`, recon `syncStale` / `lastSyncedAt` fresh, lease tokens on commands | Rust + Connector | **done 2026-10-08** (cargo 104/104 incl. DB; live Rust suite 38/38) — not committed |
| 1.2 | aiba-next: the three background jobs on `scoped_for` (`avtoprovodka_check_scheduler.rs:104`, `nomenclature.rs:2854`, `kansler_bank_write.rs:906`) | aiba-next | open |
| 1.3 | aiba-next: `normalize_onec_bases` drops `isShared` / `status=deleting` rows; `kansler_bank.rs:3713` on `onec_company_key` | aiba-next | open (~4 lines) |
| 1.4 | **Production blocker P-1** (`BACKEND_SECURITY_FINDINGS.md`): aiba-next proxies users into the module as the service (P-1a); command creation has no per-action authorization (P-1b; lease tokens do not cover it). The Rust command API stays in controlled dev until both are closed | aiba-next + product owner | open |
| 1.5 | Shared-dev cut-over runbook executed and PASS in both directions (`DEV_RUST_CUTOVER_RUNBOOK.md`) | developer + agent | open: needs P1–P7 (dev module deploy, a user-JWT route to `/api/sync/v1`, dev tenant, guarded `rust-dev` target) |
| 1.6 | **Product decision K:** does KANSLER keep running on its own, or move to aiba-next? If it stays, Rust must serve its live features (1.7); if it moves, those ports are not needed and KANSLER keeps Python until it is retired | developer | **open — decision** |
| 1.7 | (only if K = KANSLER stays) Rust routes KANSLER calls that Rust lacks: reconciliation `document/{number}`, `document-contracts`, `register-contracts`, `contract-acts`; `reports/kirim-chiqim`; connector relays `document/update`, `adapter/read`, `capabilities/monthly-existing-only`, `catalog/read` + `documents/read` (unmerged `feat/onec-read-relay`); `write-metadata/operation-kinds` + `/documents` | Rust | open (≈10 routes, ported from prod Python) |
| 1.8 | Production approval for the Rust module per tenant | developer | **closed by decision (D53): production stays closed** |

## Stage 2 — READ-ONLY / FALLBACK (Python receives no new data; reads only for history)

Python stops receiving uploads and writes. Done when:

| # | Blocker | Owner |
|---|---|---|
| 2.1 | Every Connector (old and new) of every tenant uploads to a Rust module; Python's upload / hub routes see no traffic for 14 days (access log) | developer |
| 2.2 | **Id migration tool**, run per tenant at its cut-over: Mongo ObjectId connection ids → Rust `i64`; chat2 UUID company ids → int company ids, wherever a consumer stored them (`km.nomenclature_sync`, `km.onec_catalog_sync`, local base overrides, didox `provodka_result` infobase ids, KANSLER `km.onec_catalog` …). Dry-run report first; reversible map table kept | agent (tool) + developer (run) |
| 2.3 | History: either a one-time copy of the tenant's Python `entitydatas` into Rust (only for tables whose 1C history is no longer readable — normally none, the Connector re-reads 1C) or an explicit "history starts at cut-over" per tenant | developer decision per tenant |
| 2.4 | `backend/report` retail-revenue: send the int company id (or Rust accepts the UUID through the company map); its marketplace leg ported or dropped; server-side rollup build (Python `build_retail_rollups`) replaced — Rust has only the Connector's push ingest | report owner |
| 2.5 | **Product decision P:** Odoo / AIBA BI partner surface (`odoo-inbox`, `build-write-payload`, `cloud-write*`, partner tokens + audit) — port, or retire with the partners told. Usage is provable only from prod `service_token_audits` | developer |
| 2.6 | **Product decision L:** legacy `web/dashboard` and the `backend/backend` AI 1C agent (statistics / cached-metric engine, `/api/ai/tool`, `cloud-onec`) — retire (cheaper) or port | developer |
| 2.7 | **Write path W:** aiba-next's write flows (`write_to_1c`, `check_exists`, `get_list`, `create_ref`, `document/post`) work only with the OLD Connector (through the Rust hub relay). For a base served by the NEW Connector they need either aiba-next building ready 1C bodies and using the command queue (§9 of the contract), or a Connector-side adapter that accepts the old protocol. Not a blocker for Python removal (the Rust hub already relays them); it is the blocker for retiring the OLD Connector | developer (design decision) |
| 2.8 | Known Sync gap: reference names inside documents go stale after a catalog rename (`DEV_RUST_CUTOVER_RUNBOOK.md` §5) — decide: accept, store refs next to names, or re-read referencing documents | developer |

## Stage 3 — REMOVED

| # | Blocker | Owner |
|---|---|---|
| 3.1 | 30 days with zero requests to backend/1c from any caller (access log by route and caller), after Stage 2 | developer |
| 3.2 | Final Mongo dump archived (cold storage, retention agreed); Redis keys dropped | developer |
| 3.3 | DNS / swarm service `aiba_1c` and `aiba-1c-dev` removed; `ONEC_API_URL` / `ONEC_WS_URL` removed from aiba-next and KANSLER config (a stale default must not silently reach a dead host) | developer |
| 3.4 | CI/CD for `backend/1c` archived; repo read-only | developer |

## Not ported, on purpose (no caller found anywhere)

All `/api/v1/statistics/*` and `/api/v1/entities/*`; `statistics/income/*`; qqs outgoing / incoming /
by-invoice; the reports list / refresh / jobs routes; `retail-rollup/build`; the `report-refresh`
webhook; `reconciliation-summary/*` cache routes; `reconciliation-scope`; `extra-tables`;
`backfill-total-count`; `connection/rebuild`; `connector/ping`; `/ws/1c` (cloud-os only, which is not
a future dependency); `nomenclature-index` (KANSLER falls back to a walk that now has `ref_in`);
`onecs/by-inn`, `resolve-by-inns` (partners only — decision P). Plus the 26 Python behaviours in audit
A §6 that must not be copied.
