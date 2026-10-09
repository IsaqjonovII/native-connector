# backend/1c — security findings (for the backend owner)

(P-1 below concerns the Rust onec module and aiba-next, not backend/1c.)

Found while mapping the Python v2 contract for the new Connector's Sync engine (audit B-3, decision D-9: record and
report; no backend change from this project). Evidence is the code on `origin/development`
(`ed32c71`, 2026-10-01 14:58), the branch the dev server runs. Nothing here was called or tested
against any server.

## F-1 — `POST /api/v2/onec/connection/rebuild` has no authentication; without `oneCId` it wipes fleet-wide data

| | |
|---|---|
| Route | `POST /api/v2/onec/connection/rebuild`, optional query `oneCId` |
| Code | `app/api/v2/routes/onec.py:255-285` (router mounted at `/api/v2/onec`, `main.py:77`) |
| Auth | none: no `Depends(get_current_user)` on the route (every neighbouring route has it, e.g. `:128`, `:139`, `:172`); the router has no `dependencies=`; `main.py` adds only CORS and the Odoo audit middleware |
| Impact | Without `oneCId`: `CachedMetric.delete_all()` and `EntityCount.delete_all()` — every connection's cached metrics and table counts, all tenants — then a fleet-wide regeneration task (`tasks.check_and_clear_cache`). With any `oneCId`: the same for that connection, no ownership check. Anyone who can reach the API can empty the counts every UI and report shows, and trigger the expensive regeneration repeatedly (cost / availability). Stored entity rows are not deleted. |
| Recommended fix | Add the service-secret dependency (`X-Service-Secret`, as the `/api/internal/*` admin routes use) or move the route under `/api/internal/admin`; if a user may trigger a rebuild of **their own** connection, require `Depends(get_current_user)` + `get_onec_with_ownership(oneCId, …)` and make `oneCId` mandatory for users. Never allow the unscoped form outside service auth. |

## F-2 — `POST /api/v2/onec/connection/backfill-total-count` has no authentication

| | |
|---|---|
| Route | `POST /api/v2/onec/connection/backfill-total-count` |
| Code | `app/api/v2/routes/onec.py:234-252` |
| Auth | none (same as F-1) |
| Impact | A "one-time" maintenance job open to anyone: loads every `OneC` record with no/zero `totalCount` across all tenants and rewrites `totalCount` from `EntityCount`. Not destructive by itself, but unauthenticated cross-tenant writes and a full-collection scan on demand. Combined with F-1 (counts emptied first) it rewrites nothing useful. |
| Recommended fix | Same as F-1: service auth, or remove the route now that the backfill has run. |

## P-1 — PRODUCTION BLOCKER for the Rust command API (recorded 2026-10-08; nothing fixed here)

Two separate gaps. The Rust command API (`/api/sync/v1/connections/{id}/commands…`, R9 / D53) must
not be reachable beyond controlled dev until both are closed.

**P-1a. aiba-next proxies users into the onec module as the service.** `ANY /api/v2/1c/*`
(`aiba-next/backend/crates/accounting/src/modules/onec_proxy.rs:144-179`, committed) accepts any
logged-in user of the tenant (`CurrentUser`, no company check), drops the user's `Authorization`
header and adds `X-Service-Secret`. The module treats that secret as the all-powerful service
principal, so the user's authority boundary is lost: every `/api/v2/*` route of the tenant's module
answers as for the service, for every company of the tenant (e.g. `DELETE /api/v2/1c/entity/oneC/{id}`,
org-binding changes, connection edits). Unverified but plausible: `upstream_path_and_query`
(`:124-138`) only strips the prefix; a `%2e%2e` segment normalised by reqwest could reach
`/api/internal/admin/*` (resolve-refs, `document/post`) with the secret. `connector_ws` (`:321-335`)
checks only the JWT signature. A separate task was raised for it.
Must be fixed before production: forward the user's own JWT (the module already enforces tenant,
audience and company access, R7) instead of the secret, or an allowlist of the routes the SPA uses plus
a company check; reject non-normalised paths; scope `connector_ws` to the user's bases. Any new
passthrough for `/api/sync/v1/*` (needed by the new Connector, DEV_RUST_CUTOVER_RUNBOOK P2) must be
user-JWT only from day one.

**P-1b. Command creation has no per-action authorization.** `POST …/commands` (`enqueue`,
`next-modules/onec/src/sync/commands.rs:126`) admits any caller that `access()` admits: the service
secret, or any user whose companies (aiba-next's `user_company_ids`: responsible employee, any
employee role, a company grant) include the connection. Such a user can queue `document.post`,
`document.unpost` (destructive), CAS `document.update` / `catalog.update` on CUSTOMER objects (D53),
for any document of that company. What exists: the kind allowlist, payload validation, the AIBA
marker for creates, CAS for updates, idempotency, and `created_by` (the principal's name, for audit
only). What does NOT exist: a permission per action (who may post, who may unpost), a separate
credential for the Connector (it signs in with the user's own JWT, so the same token can also LEASE
commands), and any approval step for destructive kinds.
**Lease tokens (2026-10-08) do not address this.** They make sure only the holder of the current
lease can report a command's outcome (no forged or stale results). They do nothing about who may
create a command, and a user with company access can still lease.
Must be fixed before the command API leaves controlled dev: (1) P-1a closed, so every request carries
the real user; (2) a per-action permission check at enqueue (role/grant per kind, unpost and
customer-object writes stricter than create/post of AIBA documents), decided by the product owner;
(3) the Connector's lease/report authenticated as the Connector (a device credential bound to the
connection), not as any user — managed-Connector work, deliberately not started now; (4) the real
user id (not a display name) stored on the command for audit.

**Status 2026-10-09 (local, uncommitted):**
- P-1b module side **fixed**: `POST …/commands` is service-only (403 `commands_service_only` for every user
  token, tenant admin included); lease returns only commands of the user's partitions; report on a command of
  another partition answers 404; a single-tenant module without `ONEC_TENANT_SLUG`/`TENANT_SLUG` refuses user
  tokens (it could not tell another tenant's token apart). Live test
  `SyncRustSecurityTests.OnlyTheServiceCreatesA1CWriteAndAUserTouchesOnlyCommandsOfTheirPartitions`.
- P-1a **partly fixed**: aiba-next `rewrite_path` refuses dot segments and encoded `.`/`/`/`\` (test
  `refuses_paths_that_could_leave_the_module_api`, `onec_proxy` 8/8), so `/api/v2/1c/*` can no longer reach
  `/api/sync/v1` or `/api/internal/admin` with the secret. Still open: any user still reaches the module's
  `/api/v2/*` as the service (product call — see V1_RELEASE_CHECKLIST blocker 3).
- Still open for production: the module branch verifies HS256 only; production aiba-next signs EdDSA
  (module master already verifies it — rebase); the legacy `secret_or_jwt` (write.rs `document/post`,
  `catalog/update`) accepts any valid HS256 token with no tenant/company check.

## Not findings, recorded for context

- `connection_state` (Redis presence of the connector WebSocket) is the authority for "a connector
  serves this base"; the new Sync engine reads it to refuse running next to the old Connector (D-1).
- The v2 upload path over-reports inserts when a duplicate key is swallowed (2 reported, 1 stored —
  pinned by `SyncPythonTargetTests`). A correctness limit, not a security one; listed in
  `MIGRATION_STATUS.md` known limits.
