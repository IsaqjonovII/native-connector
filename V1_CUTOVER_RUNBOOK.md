# V1 cutover runbook — one base, old Connector / backend/1c → new Connector / Rust

Per base, never fleet-wide. **Production needs the developer's explicit go per tenant** (closed by D53).
Shared dev first: `DEV_RUST_CUTOVER_RUNBOOK.md` (its §0 prerequisites apply here too).

Invariant: **one Sync writer per base, one backend per base.** The engine enforces both: it refuses to run
while the Rust module sees an old-Connector socket for the connection (`/presence`, D-1), and a run naming a
backend other than the base's bound one is refused unless `"switch": true` (`TargetBinding`, R10).

## 0. Preflight (all must hold; stop otherwise)

| Check | How | Must be |
|---|---|---|
| Rust module for the tenant up, migrated | `GET /api/sync/v1/capabilities` (user JWT) | 200, `commands` listed |
| Connection + bindings | `POST /api/v1/onec` (service); `PUT /api/v2/onec/{id}/org-bindings` | partitions = the base's organisations |
| Old Connector off for this base | `GET …/connections/{id}/presence` | `otherConnectorOnline: false` |
| Old data recorded | Python `GET /api/v2/onec/{id}/counts`; Rust counts | numbers saved in the cutover log |
| aiba-next background jobs scoped | `scoped_for` in the three jobs (PYTHON_RETIREMENT_PLAN 1.2) | deployed |
| Ids remapped where a consumer stored Python ids | id-map tool dry-run (PYTHON_RETIREMENT_PLAN 2.2) | 0 unmapped |
| Command creation path | `POST …/commands` with a user token | 403 `commands_service_only` |

## 1. Switch (explicit)

1. Supervisor `sync.json`: `"target": "rust:<module>"`, the base's `connectionId`, the table plan
   (windows as agreed), `"switch": true` **for this run only**.
2. Start. `TargetBinding` records the new backend; the first copy starts in Rust.
3. Coverage: each document/register table is stored `loading` before its first row and its real reach
   (`complete:false, dataFrom:<window>` or `complete:true`) after — sotuv never reads a half copy as whole.
4. Remove `"switch": true` from `sync.json` (a later run must not switch by accident).

## 2. Validate before anyone reads it

| Proof | Command | Must be |
|---|---|---|
| Rust = 1C, every row, every field | `OneC.Supervisor sync-verify` (Rust mode) | `PASS` |
| Counts | `…/counts` vs 1C `COUNT` (register within its window) | equal |
| Coverage | `GET /api/internal/admin/onecs/{id}` | as §1.3, on every partition |
| Online | base picker / `GET /api/v1/onec?companyId=` | `status: active` within 60 s |
| Multi-org | `sync-verify` partition column | 0 wrong partition |

## 3. Consumer smoke (the pilot's actual flows only)

avtoprovodka source-counts and onec_txn list; sotuv check-zakaz + catalog search; reconciliation summary
(`syncStale:false`); last-by-counterparty; match-index 409 → 200. Each compared with 1C for one sampled object.
A flow the pilot uses that is not supported on Rust (PYTHON_RETIREMENT_PLAN 1.7 for KANSLER) **blocks the
cutover of that base** — it is never silently degraded.

## 4. Old route off

- aiba-next tenant `onec_module_url` = the Rust module (request handlers already read only it).
- Old Connector stays uninstalled / stopped for this base (presence must stay false; the engine pauses the
  base if it ever appears).
- backend/1c keeps the base's old data read-only; nothing writes to it.

## 5. Rollback (proven locally: R10, 2026-10-08)

| From | Do | Proof |
|---|---|---|
| New Connector on Rust → old Connector on Rust | stop the Supervisor (status `stopped` → `inactive`); start the old Connector | old Connector's own sync; Rust row keys are shared (recorder_key backfill, R7) |
| New Connector on Rust → backend/1c | `sync.json` names the backend/1c target with `"switch": true` | that backend resumes from **its own** cursor and catches up (R10: a document posted while on the other backend arrived in 4.3 s); `sync-verify` PASS against it |
| Any | the base's command queue | leave `"commands"` off during a rollback; queued commands wait (leases expire, nothing runs twice) |

Abort (stop the Supervisor, roll back, report): `sync-verify` FAIL; a dead letter; presence true; any 5xx
loop; a consumer smoke mismatch; any write outside test-owned objects during the dev run.
