# Old Connector audit — raw investigator reports (2026-10-03)

Read-only reverse-engineering of the old AIBA Connector, its 1C adapter and the cloud side. These are
the raw reports of the six investigators; the reviewed, cross-checked result is in the repo root:
`OLD_CONNECTOR_DEEP_AUDIT.md`, `OLD_CONNECTOR_FEATURE_MAP.md`, `OLD_CONNECTOR_HIDDEN_FEATURES.md`.
Where a raw report and the deep audit disagree, the deep audit wins (its §17 lists the corrections).

| File | Scope |
|---|---|
| `A-frontend.md` | Connector TypeScript/React: providers, cloud commands, sync orchestration, storage, timers |
| `A2-helpers-supplement.md` | Findings of A's helper audits (app shell, sync engine) that did not reach A's report |
| `B-rust.md` | Rust/Tauri: commands, process supervision, device identity, updater, files, security |
| `C-adapter.md` | `main.os` 1C adapter: full route table, callers, cloud reachability |
| `D-backend-1c.md` | backend/1c: routes, sync-table system, upload contract, presence, admin routes |
| `E-control-plane.md` | Cloud → Connector command registry, presence, identity, admin and remote support |
| `F-discovery.md` | Cross-cutting inventories (routes, events, commands, flags, keys, timers) and history |

Checkouts: connector `release` @ `cfab3a1`; backend/1c `development` @ `8107bc0` and
`origin/development` @ `b2e3590`; backend/backend `feat/purchase` @ `53b24b35`. Nothing was run,
called or changed; secrets were redacted.
