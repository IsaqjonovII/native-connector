# backend/1c — security findings (for the backend owner)

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

## Not findings, recorded for context

- `connection_state` (Redis presence of the connector WebSocket) is the authority for "a connector
  serves this base"; the new Sync engine reads it to refuse running next to the old Connector (D-1).
- The v2 upload path over-reports inserts when a duplicate key is swallowed (2 reported, 1 stored —
  pinned by `SyncPythonTargetTests`). A correctness limit, not a security one; listed in
  `MIGRATION_STATUS.md` known limits.
