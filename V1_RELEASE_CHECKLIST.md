# Connector v1 — release checklist

2026-10-09. Evidence = code + tests + live runs on this PC (local, isolated). Nothing committed or pushed;
nothing deployed. Production stays closed (D53).

## DONE (proven)

| Area | Evidence |
|---|---|
| Normal 1C reads, first copy, incremental, recovery, deletes/reposts | R4/R5 (bilim), R6 (KAN 34 145 rows), crash/replay; `sync-verify` field-by-field PASS |
| Multi-org | R6 on real KAN data: routing = 1C per organisation, 0 wrong partition |
| Data coverage | loading → final reach on every partition; consumer route checked (R6, R10) |
| Window correctness in incremental work | fixed 2026-10-08 (`CanonicalMapper.InWindow`); unit V2+V3; live: stray rows removed |
| **Normal writes round trip** (Rust command → Supervisor → Host → 1C → Sync → Rust = fresh 1C) | 2026-10-09 on `bilimsrv` (server copy of today's bilim): `rust-r9.ps1` create · same key = same command · new key + same marker = same document · post · update+repost · unpost · repost · mark deletion · refusals (no marker, `exchange`, `_postCreateMethod`, `document.delete`, foreign mark, bad date) · owned cleanup — **R9 PASS**, verify after every step |
| Catalogs | `rust-r9-catalog.ps1`: create · idempotent recreate · CAS rename · stale CAS refused · blind customer update refused (Rust) · catalog without Комментарий refused · cleanup — **PASS** |
| Customer-owned writes (D53) | `rust-r9-customer.ps1`: post · unpost+repost · blind/stale/marker-claiming updates refused · CAS update and back · deletion mark refused · catalog stale/claim refused · CAS rename and back — **PASS** |
| **Reference rename staleness** (release blocker) | D54: rename → `ReferrerSearch` → only the referencing rows re-read. Unit `SyncRenameTests` 3/3; live `LiveReferrerTests` (bilim + kansler); live R9: 20 documents showed the item → **20/20 show the new name** ~8 s after the rename, full verify PASS. Live bug found and fixed on the way (`ТипЗнч` not exposed over COM) |
| In-flight retry | a create that outlived its deadline in a bilim stall: lease retry found the marker → one document (live, 2026-10-08) |
| Command lease tokens | live `OnlyTheHolderOfTheCurrentLeaseReportsACommandsOutcome` |
| **Command authorization** (P-1b) | command creation service-only (403 for every user token incl. tenant admin); lease/report limited to the user's partitions; single-tenant module without a known tenant refuses users. Live `OnlyTheServiceCreatesA1CWrite…`; Rust 104/104 with DB tests |
| aiba-next proxy escape (part of P-1a) | `rewrite_path` refuses dot segments / encoded separators; `onec_proxy` tests 8/8; pushed to aiba-next `development` (`866c6342`), pipeline 17697 deployed dev-next (health 200) |
| Base online heartbeat | `/status` sets active/inactive on connection + bindings; live test + seen live (`active` right after start) |
| Rust consumer gaps (aiba-next) | entity-data `total`, venkon types, `ref_in`/`owner`/`number`, recon freshness — each checked live (`D-aiba-next-consumers.md`) |
| Per-base backend selection + rollback | R10 rerun on the final build 2026-10-09: refused without `"switch"` · switch to backend/1c, verify PASS · a 1C change while on Python reaches backend/1c (PASS) and not Rust (0 rows) · rollback to Rust catches up in 2.2 s from its own cursor, verify PASS · cleanup PASS. (The script's fixed 20 s wait raced the idle event-log poll; it now waits for the feed) |
| Distributable | `tools/publish.ps1`: one folder, self-contained (no .NET install), versioned `1.0.0-rc1+<commit>`, secrets scan; **found and fixed: published app crashed at launch** (publish dropped `.pri`/`.xbf`); clean-start smoke: starts, engine from `runtime\`, version in `app.log`, close stops the engine, restart works |
| **Release gate (D45)** | `tools/gate.ps1`, final build (clean Release build 02:36, no code change after): **run 3 459/459, run 4 459/459 — consecutive, 0 failed, 0 skipped**, gate mode (`AIBA_TEST_GATE=1`), live bilim (file) + kansler (server), isolated backend/1c and Rust really exercised, child-process isolation, no leftover process, 0 `AIBA_REWRITE_` documents. Before them: run 1 (pre-fix build) 457/459 — a stale D18 test (now asserts D53: marker claim 400, blind update 422, delete 403, ВерсияДанных unchanged) + a bilim connect stall; run 2 458/459 — bilim connect stall (diagnostics `measurements/gate/stall-diag-*.txt`). Logs `measurements/gate/` |
| Rust suite | `cargo test` 104/104 with DB tests (`TEST_DATABASE_URL`) |

## BLOCKED (needs access, infrastructure or a decision)

| # | Blocker | Needs |
|---|---|---|
| 1 | **The Desktop cannot reach a real Rust module.** It signs in to the legacy AIBA cloud (`api.aiba.group`); the module accepts aiba-next tokens; the app writes the stub target (D-2) and the Rust target is loopback + service secret only | aiba-next login in the Desktop + a user-JWT route to `/api/sync/v1` + a guarded non-loopback Rust target (`DEV_RUST_CUTOVER_RUNBOOK` P2/P7) |
| 2 | ~~module branch behind master / HS256-only~~ **DONE 2026-10-09**: merged onto master (`ba89766`), `sync::check_token` verifies with master's `JwtVerify` (EdDSA + legacy HS256, no lease tokens); cargo 189/189, live Connector suite 18/18 against that build; pushed to onec `development`, pipeline 17698 published `10.0.0.33:5000/onec:development`. Not yet released to any dev tenant | a dev-tenant release of `onec:development` (superadmin / fleet) |
| 3 | **P-1a**: aiba-next's `/api/v2/1c/*` still forwards any logged-in user as the service on the module's `/api/v2/*` routes | product call: forward the user's JWT (breaks nothing new) or a route allowlist + `onec.view` gate |
| 4 | Decision: `avtoprovodka.edit` for create/update/post/catalog writes and `avtoprovodka.delete` for unpost/markDeleted (the code precedent), or unpost/markDeleted service-internal in v1 (D54) | developer |
| 5 | aiba-next has no route that creates commands for a user (its writes go through the hub relay for the OLD Connector) | aiba-next route with `require_company(perm)` → module (service); `PYTHON_RETIREMENT_PLAN` W |
| 6 | Shared-dev cutover not executed | dev module deploy of the branch, dev tenant, items 1–2 |
| 7 | KANSLER consumers (~10 routes Rust lacks) | decision K (`PYTHON_RETIREMENT_PLAN` 1.6) |
| 8 | 32-bit-only 1C PCs: the Host is x64 | check the pilot PC, or an x86 Host build |
| 9 | The exe is unsigned | code-signing certificate |

## DEFERRED (next phase, by scope)

Fleet dashboard, presence redesign, remote diagnostics / data explorer, Sync Table Manifest, optional modules,
adaptive intervals, remote/maintenance scripting, the push command transport (polling stays, D53), admin
control plane, Connector device credentials (separate from the user's token), stable-ref row shape
(`<Field>_Key`), autostart/tray/single-instance, .NET 10 move (net9 support ends 2026-11-10), KANSLER ports,
Python retirement stages 2–3.
