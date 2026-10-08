# MIGRATION_STATUS.md — what is actually done

Only things that were built **and** verified are DONE. Last verified: 2026-10-08.

## Rust onec backend — R0–R10 DONE locally (Sync proven against 1C, security hardened, normal writes round-trip, per-base switch + rollback) (2026-10-08, D51, D52); not committed, no production

Order and plan: `MIGRATION_PLAN.md` (roadmap), `PYTHON_TO_RUST_MIGRATION_PLAN.md`; audit
`RUST_ONEC_BACKEND_AUDIT.md` (+ `research/rust-backend-audit/` A, B, C); contract `RUST_SYNC_CONTRACT.md`.
Nothing touched production or a shared server. Python target unchanged in behaviour (it gained the
coverage call its backend already had).

- **R0 stack:** isolated Postgres 12 cluster (1C's PG 12 binaries, own data dir under
  `%LOCALAPPDATA%\AIBA-rust-sync-test`, 127.0.0.1:55440; the 1C cluster untouched) + the Rust module
  on 127.0.0.1:18112 with test secrets made for it (`rust-secrets.json`, outside the repo).
- **R1 Rust:** `next-modules/onec` branch `feat/sync-api-v1` (worktree `_wt-onec-sync-v1`):
  `src/sync.rs` (`/api/sync/v1`: capabilities, config, rows, delete, recorder, purge, counts, rows
  read, coverage, status, presence), `entity_data.source_version` + `recorder_key` + index,
  `onec.sync_batch`. `cargo test`: 95/95.
- **R2 Connector:** `src/OneC.Sync.Targets.Rust/RustSyncTarget.cs`; `RecorderSync` is one call for all
  partitions. Sync suite incl. the live `SyncRustTargetTests` against the local module: **132/132**.
- **R3 wiring:** `sync.json` `target: "rust:<loopback>"`, `OneC.Supervisor sync-rust create|counts|bench`,
  `sync-verify` reads Rust, `tools/rust-local.ps1`, `tools/rust-kan.ps1`, `tools/rust-lifecycle-edge.ps1`,
  `tools/rust-crash.ps1`.
- **R4 bilim vs 1C:** 8 tables (chart, 3 catalogs, 2 document types, accounting register from
  2025-08-01, independent info register) into Rust connection 13. `sync-verify` **PASS** — every stored
  row equal to a fresh 1C read field by field (`measurements/rust-sync/verify-r4-snapshot.txt`).
- **R5 lifecycle (bilim, test-owned `AIBA_REWRITE_S12_` ПТУ) — PASS, all 7 steps:** create, post,
  change date + repost, unpost, repost, mark-deleted, delete; after each step Rust changed within
  2–11 s and `sync-verify` PASSED against 1C (`lifecycle-run.txt`, `verify-r5-*.txt`). The test
  document is gone from 1C and Rust. Found and fixed on the way:
  - **Connector bug (not Rust):** the host's list read leaves empty table parts out, its by-id read lists
    them as `[]`, so one unchanged document had two canonical rows. Fixed in
    `CanonicalMapper.DropEmptySections` (test `BothReadShapesOfOneDocumentMapToTheSameRow`).
  - **Environment blocker, separate from Rust:** the first mark-deleted sat inside a bilim file-base stall
    for 3 h (edge 503 after 65 s, `1Cv8tmp.1CD` written, host CPU flat, main file unchanged). Diagnostics
    `measurements/rust-sync/bilim-stall-diag-20261007.txt`; the Supervisor's bilim host was restarted with
    the developer's approval; 1C showed the mark had **not** landed (posted, no mark — Rust agreed); the
    rerun through the edge marked in 14 s and deleted in 19 s. Writes from a second process (DevBench)
    beside the host waited ~35 min each on the file base; writes through the host's own edge did not.
- **R6 multi-org on real data (KAN, read-only; services started for the test and stopped after):**
  Rust connection 30 with both KAN organisations bound (e14072fe → company 7001, 21c869e7 → 7002).
  8 tables, documents and the accounting register from 2026-06-01: 34 145 rows; routing equals 1C per
  organisation (Реализация 748 / 4, ПТУ 617 / 0, ППИ 311 / 3, Хозрасчетный 27 140 / 1), references only
  in the shared partition. `sync-verify` **PASS**, 0 wrong partition (`verify-r6-kan-multiorg.txt`).
  A second snapshot from fresh local state re-sent all 34 145 rows: every one answered `unchanged`,
  nothing rewritten.
- **R6 recovery:** `tools/rust-crash.ps1`, module killed while a 2000-row batch is held inside its
  transaction (3 rounds): 0 rows and no batch record afterwards, replay stored 2000; killed after the
  commit (3 rounds): 2000 stored, replay answered from the batch record (`replayed: true`), nothing
  written twice. Atomic recorder rollback on a refused line: live contract test.
- **Coverage — DONE, a cut-over blocker found and closed:** the engine reported none, and the consumer
  (aiba-next `sotuv.rs mirror_reach_from_onec`) reads a table with no entry as "whole history" — a
  windowed or half-copied table would have licensed duplicate documents. Now (`BaseSyncAgent`):
  "loading" (`complete:false`, no date = unknown) is stored before a document/register table's first
  row; after its snapshot `complete:false, dataFrom:<From>` for a windowed table, `complete:true` for a
  whole one; to every partition of a movement table; a failed final report is re-sent next activation
  (test `CoverageIsLoadingBeforeTheFirstRowAndTheRealReachAfter`). Live on KAN: all windowed tables
  `dataFrom 2026-06-01, complete false` in the shared and both organisation partitions, visible on the
  consumer's route `/api/internal/admin/onecs/{id}`. Semantics unchanged for consumers.
- **R8 measurements:** see `PYTHON_TO_RUST_MIGRATION_PLAN.md` §4. Rust module 10–15 MB private; Postgres
  is the write bottleneck (insert 5 537 rows/s of ~1.2 KB, unchanged re-send 37 106 rows/s, recorder
  unit p50 3.5 ms).
- **R7 security / data integrity — DONE locally (2026-10-08).** `/api/sync/v1`:
  - **Callers:** a service (`X-Service-Secret`, now a constant-time compare, also on the legacy routes
    and the connector hub), or a user with the aiba-next JWT. For a user: signature + `exp`, `aud` when
    `AIBA_JWT_AUDIENCE` is set (found: `set_audience` alone let a token without `aud` through, fixed),
    the token's `tenant` = this tenant (multi: `X-Tenant`; single: `ONEC_TENANT_SLUG`), superadmin
    refused, companies = aiba-next's own `user_company_ids` rule (responsible employee, employee role,
    company authz grant) read from the same tenant DB, tenant admins all. Outage of that check = 503.
  - **No leaks:** a connection or partition of a company the user may not touch answers exactly like one
    that does not exist (also a binding id used as a connection, and a connection being deleted); every
    auth failure is the same flat 401.
  - **Ids:** strict (1–18 digits, no sign/space/leading zero); keys, batch ids, delete keys, coverage table
    names, status text checked. **Found and fixed: a U+0000 anywhere (key or 1C text) failed the WHOLE
    batch** (jsonb cannot hold it → 503 retried forever, base paused); now that row alone is rejected
    `nul_in_data`.
  - **Maintenance:** `recorder_key` backfill for rows the legacy upload wrote (lower-cased, `<guid>#<n>` only,
    walks `id` from a stored mark — idempotent, incremental); `sync_batch` retention 7 days
    (`SYNC_BATCH_RETENTION_DAYS`), chunked; both in ticker T7.
  - **Proof:** Rust 102/102 incl. DB tests (migrations twice, backfill, retention), token/secret/id unit
    tests; live `SyncRustSecurityTests` 6/6 against a single-tenant instance (km/authz fixture) and a
    multi-tenant one (two tenant DBs from a fake loopback central): user/company/partition isolation,
    identical refusals, ten forged-credential cases, malformed ids, reconcile/delete/purge never leaving
    the named partition, a t1 token refused on t2, the same numeric id in two tenants never crossing.
    Migrations re-run on the existing data: 51 table checksums identical before/after.
- **R9 normal writes — DONE locally (2026-10-08), round trip proven:** Rust command → Supervisor (pulls;
  opens nothing inbound, D41) → OneC.Host → 1C → Sync → Rust stored state = 1C. Rust: `onec.command` (stored
  before anyone runs it; one idempotency key per connection; queued → dispatched (lease) → succeeded /
  failed, lease expiry re-dispatches, `dead` after 5 attempts), routes `commands`, `commands/lease`,
  `commands/{id}`, `commands/{id}/result`; only the normal primitives (`document.create/update/post/
  unpost/markDeleted`, `catalog.create/update`); refused before storing: unknown kinds (no delete,
  no procedures), `exchange`, any `_` directive but `_idempotencyMarker`, a create without the AIBA
  marker. Connector: `CommandRunner` (`"commands": true` in sync.json; one at a time, oldest first;
  transport/timeout/retryable errors left for the lease, the rest reported with 1C's own error).
  Host: new `CatalogWriter` (create idempotent by marker, compare-and-set update with a per-field
  `conflict` answer, only catalogs with Комментарий and only AIBA-owned items; owned delete for local
  test cleanup only). Live on bilim (`r9-run.txt`, `r9-catalog-run.txt`): document create (draft) →
  same key = same command → new key, same marker = same 1C document → post → update (date) + repost →
  unpost → repost → mark for deletion; catalog create → same marker → CAS rename → stale CAS refused
  ("expected '…', 1C holds '… (renamed)'") → foreign item refused (D18) → Банки refused (no Комментарий).
  After EVERY write Sync brought it into Rust in 0.3–23 s and `sync-verify` PASSED against 1C; refused
  commands changed nothing; test objects deleted. .NET Sync suite 151/151.
- **R10 per-base switch — DONE locally (2026-10-08), isolated backends only.** `TargetBinding`
  (`sync-targets.json` beside the sync state): one backend per base; a run naming another backend is
  refused unless `"switch": true`; each backend keeps its own local sync state (a switch copies in full
  into a backend never fed; a switch BACK resumes that backend's own cursor and catches up). Live on bilim
  (`r10-run.txt`) between the local Rust module (connection 13) and the isolated local backend/1c
  (Docker Mongo `aiba_1c_sync_test` / Redis 9, every setting explicit, `.env` unused, record
  `6ac74a64…`): refused without the switch; switch → first copy into backend/1c in 28 s, `sync-verify`
  PASS (new local-backend/1c read mode); a document created and posted in 1C while on Python reached
  backend/1c (PASS) while Rust received nothing (row count and last write unchanged); rollback → Rust
  caught that document up in 4.3 s from its own cursor, PASS; cleanup PASS. Coverage after the switch:
  backend/1c got loading → final (register `complete:false, 2025-08-01`, the rest whole).
  **Found and fixed:** a base copied before coverage existed (bilim → Rust 13) had NO coverage — the
  "whole history" default — and the engine only reported it during a copy. Now every activation sends the
  final reach of a copied table once (per window), with no new copy; verified on connection 13 (2000 rows,
  nothing rewritten). Tests: `SyncTargetBindingTests`, `ABaseCopiedWithoutCoverageGetsItOnTheNextRun`;
  Sync suite 153/153.
- **Open:** production stays closed until the developer approves; the bilim file-base stall (cause
  unknown, environment); the product decision on writes to objects AIBA did not create (D18 refuses
  them; old flows posted user drafts and updated customer catalog items); a real per-tenant switch also
  needs the old Connector off for the base (D-1) and the cloud's readers pointed at the backend that is
  active.

## Old Connector reverse-engineering — DONE 2026-10-03 (read-only)

Six read-only investigators (frontend, Rust/Tauri, `main.os` adapter, backend/1c, cloud control
plane, cross-cutting discovery) plus a reviewer that re-checked 30 key claims in code (26 verified,
4 corrected). Checkouts: connector `release` @ `cfab3a1`, backend/1c `origin/development` @
`b2e3590`, backend/backend `feat/purchase` @ `53b24b35`. Nothing was run, called or changed.
Results: `OLD_CONNECTOR_DEEP_AUDIT.md` (full audit, 18 sections, incl. command registry, presence,
device identity, adapter route table, security findings, comparison with this rewrite),
`OLD_CONNECTOR_FEATURE_MAP.md` (one row per feature), `OLD_CONNECTOR_HIDDEN_FEATURES.md`.
Biggest open item, unverified in production: the connector hub WebSocket
(`/api/v2/connector/ws`, backend/1c) has no authentication in code.

## Sync — S0–S15 LOCAL COMPLETE; shared-dev S12 BLOCKED on the developer's dev login + test company (architecture approved 2026-09-30, D42)

Plan: `SYNC_IMPLEMENTATION_PLAN.md`. Design: `SYNC_ENGINE_ARCHITECTURE.md`. Nothing uploaded to any real backend.
Built as "Sync Engine v2" beside the milestone 5.9 engine; since 2026-10-02 it is the one canonical
engine and carries the plain names (`OneC.Sync`, `/v1/sync/*`, `--sync-config`, `sync.json`); the 5.9
engine was removed (D48). Rows below use the current names.

**Rename + test isolation 2026-10-02 (D48, D49): Release gate MET.**
- Rename first: clean Release build 0 warnings / 0 errors (tests, desktop, research benches); sync
  unit + Python target tests in gate mode 122/122. Full suite run 1 416/416, then runs 2–4
  **aborted**: the test process itself died of 1C's heap corruption `0xC0000374` (ntdll+0x117eb5)
  inside `SyncLiveTests.ATestDocumentsWholeLifeReachesTheStubExactly` (run 3) and
  `…TheEngineRunsInTheSupervisorAndIsControlledThroughTheEdge` (run 4); run 3 also failed the child
  scenarios `quiet-base` ("server live 1 (want 0)") and `managers-recreated` (0xC0000374).
- Fix (D49): those two scenarios — 1C writes through a SessionManager while OneC.Host reads the same
  base — run as `OneCLiveChild` scenarios (`sync-lifecycle`, `sync-edge`, checks unchanged), with a
  guarded fallback cleanup child (`cleanup-owned`, run-specific `AIBA_REWRITE_S<n>_<tag>` only).
  `LiveChild` now waits for a child's OneC.Host processes (never kills them) and kills a hung child
  only, not its tree. `quiet-base` asserts the rule (quiet base retires within 30 s, busy base keeps
  its session, caps, budget, no read errors), not a snapshot 4.5 s after a clock that included KAN's
  cold Connect. New harness test: a broad cleanup prefix is refused (`TheFallbackCleanupRefusesABroadPrefix`).
- Child tests ×5 (11 tests each): 4 × 11/11; once `sync-lifecycle` died `0xC0000374` after "kansler
  create + post" — that test failed alone, the run went on, the fallback cleanup deleted the 1 test
  document. `sync-lifecycle` alone ×10: 10/10. So 1 crash in 15 lifecycle runs, now contained. Cause
  still inside 1C; a dump needs WER LocalDumps (machine-wide) or a debugger install — not done.
- **Gate:** clean Release build, then two consecutive full runs in gate mode with the isolated local
  backend: **417/417, 0 failed, 0 skipped, 13 m 11 s** and **417/417, 0 failed, 0 skipped, 9 m 29 s**
  (416 + the new harness test). Python target 4/4 against the backend both times (0.5–1 s each).
  After: 0 testhost / OneC.Host / Supervisor / OneCLiveChild processes; `AIBA_REWRITE_` dry-run
  bilim 0, kansler only the known Мирель copy; isolated backend stopped, `aiba_1c_sync_test`
  dropped, Redis db 9 flushed.

| block | status | verified by |
|---|---|---|
| S0 spikes | DONE 2026-09-30 — no assumption contradicted | `research/sync-spikes/S0-RESULTS.md`, raw output `measurements/sync-s0/`; `OneC.Host spike-versions / spike-lifecycle` on KAN (8.3.15) and bilim (8.3.15 + 8.3.18); 2 test-owned documents created and deleted, 0 left |
| S1 sync.db | DONE 2026-09-30 | `src/OneC.SyncState` (SyncDb, SyncTx: work queue with merge/lease/generation, cursors, slices, versions, partitions, register rows, dead letters, runs); `SyncStateTests` 22/22 ×3 (fault between items and cursor, fault before commit, merge table, lease expiry, backoff, merge-during-run survives completion, dead letter refuses newer intent, garbage file and damaged page quarantined → recovery_required, newer schema refused, kill of a child process mid-commit: cursor equals items); full unit suite 297/297. StoreBench (`measurements/sync-s0/s1-storebench.txt`): commit p95 10 ms (stop limit 20), 10 000 upserts 0.2 s, 1 M versions 128 s / 159 MB |
| S2 target + stub | DONE 2026-09-30 | `src/OneC.Sync.Abstractions` (`IBackendSyncTarget`, canonical batches/results, `TargetCapabilities`), `src/OneC.Sync.Stub/StubSyncTarget` (V2: first-wins duplicates with over-reported count, 5 % / empty-table delete refusal, no atomic recorder; V3: duplicate rejection, per-row results, stale source version, atomic recorder); `SyncTargetContractTests` 14/14. The "engine never branches on Kind" check moves to S5, when engine code exists |
| S3 host prerequisites | DONE 2026-09-30 | Register `syncKeys` (recorderRef/lineNo/orgRef on every recorded register, off by default so D33 rows are unchanged), `RegisterSchema.Dimensions`/`Independent`, independent information registers page by natural key (`RegisterKeyset`, 1C's own XML value form) with `naturalKey` per row (R-2), by-recorder paging `afterLine` (R-8), `notFoundScope` object/metadata (P6), `versions` op (keyset + ids), `withVersion` on catalog/document reads (ВерсияДанных from the same query), `chart` op. Live: `SyncKeysGive…`, `ByRecorderReadsPage…` (KAN: Хозрасчетный + НДСПродажи, ~6 pages each = the unpaged set), `IndependentInformationRegisters…` (every row once, COUNT = paged, KAN + bilim, also bilim on 8.3.18), `SyncRead*` 7/7 incl. **chart rows identical to the old adapter (60/60, `/api/db/KAN/charts`)**, version walk = COUNT, ids answer existence, no ComRef leak. Full live suite: 284/285 — see known limits below |
| S4 scheduler + budgets | DONE 2026-09-30 | `src/OneC.Sync/Scheduling` (`SyncScheduler`, `FeedStatPoller`, `SyncLeases`, `SyncBudgets`); `SyncSchedulerTests` 8/8 (30 idle bases never activate, stat cost bounded, ≤ 3 active, recorder work preempts a snapshot, slice fairness round-robin, lease caps 1/3/6 + foreground yield + write quiet window). Live idle (`research/sync-spikes/IdleBench`, 23 real bases of this PC's 1C list, 10 min): **0.009 % CPU**, 8–11 MB private, 0 sync leases, 513 stats; 63 activations all from KAN's own background jobs writing its log. Handles 277 → 353 over the 10 min: not a leak per operation — 10 000 real-log stats (`IdleBench statchurn`) 235 → 237, 1 079 activations (`IdleBench churn`) 265 → 273; the rest is runtime warm-up, re-checked in S15's long run |
| S5 snapshot pipeline | DONE 2026-09-30 | `src/OneC.Sync/Snapshot` (planner, readers → mappers → uploaders over bounded channels, byte budget, per-slice watermark checkpoints, slices on server bases only), `Upload/Uploader` (gate, retries), `Mapping` (JsJson kept from the 5.9 engine, `CanonicalMapper`), `OneC.Supervisor/SupervisorReader`; `SyncSnapshotTests` 12/12 ×5 (slices read once, file base never sliced, byte budget holds with a slow target, crash resumes from the last accepted page, stop loses nothing, auth pauses without advancing, rejected rows → dead letters, engine never branches on target Kind). Live (`OneC.Supervisor sync-snapshot`, stub v2, `measurements/sync-s0/s5-*`): KAN ДоговорыКонтрагентов 116 759/116 759 rows, ~2 200 rows/s = plain pipe reading (in-process walk 3 163; the gap is the IPC hop, D43), supervisor ≤ 145 MB; chart 432/432; Хозрасчетный 12 months 249 148/249 148 rows, **K=1 227 rows/s → K=4 1 301 rows/s (5.7×)**, supervisor 166 MB, host 497 MB private at K=4; same window raw walk 476 vs pipeline 619 rows/s (no pipeline overhead) |
| S6 feed + outbox | DONE 2026-09-30 | `Incremental/EventCoalescer`, `FeedService`; reader: start-of-log cursor (S0 Q6), `Restored` (`_$InfoBase$_.RestoreFinish` → Recovery), dictionary re-read on unknown ids; `SyncFeedTests` 6/6 (§6 table, handshake, empty log, cursor never ahead of items under fault, high/low water, reset/restore → Recovery keeps the old cursor) + reader tests 6/6. Live: KAN + bilim test document → exactly one item, merged into a delete after deletion. Replay of KAN's September log (2.7 GB, 440 575 data events) → 1 473 items in 59 s, 45 MB/s, 98 MB private, 0 unknown ids |
| S7 object executor | DONE 2026-09-30 | `Incremental/WorkExecutor` (§22 classification: transient backoff in the queue, validation/policy/missing-config dead letters, auth/gone stop), `SyncObjectHandler` (version no-op suppression, object-level not-found → delete), `DeleteObjectHandler`; `SyncExecutorTests` 8/8 |
| S8 recorder | DONE 2026-09-30 | `Incremental/SyncRecorderHandler` (paged movements of every register the type posts to — host `recorders`/`recorderOf` ops, v2 upload-then-reconcile or v3 atomic, partition history, unresolvable recorder = no movements), host `RecorderMetadata`; `SyncRecorderTests` 7/7 (post/repost fewer lines + same keys new amounts (F1)/unpost/delete in v2 and v3, org change leaves nothing in the old partition, type not synced (R-9/F2), crash between upload and reconcile repaired, 1 doc read + 1 read per register page). **Live through the Supervisor pipe** (`SyncLiveTests`): KAN and bilim test document create+post → unpost → repost → delete, stub = 1C after each step (2 / 1 Хозрасчетный lines appear and vanish), 0.1–4.9 s each; 0 test documents and 0 processes left. v2 finding: deleting one row of a table under 20 rows is over the 5 % prune cap → `needs_approval` (known v2 limit, audit) |
| S9 registers | DONE 2026-09-30 | Accumulation and recorded information registers go through `SyncRecorder` (S8); independent information registers: `RefreshRegisterHandler` (natural key walk, row hash diff in `register_rows`, only changed/new rows sent, removed rows deleted after every change was accepted, unmapped-org rows kept, per-table `RefreshEvery` — D-6), snapshot stores the hashes so the first refresh sends nothing; `SyncRegisterTests` 5/5 (incl. R-1 regression: 500 events → one refresh, no table reset). Live refresh cost (`measurements/sync-s0/s9-refresh.txt`, nothing changed → 0 sent): bilim СтатусыДокументов 68 rows 0.1 s; KAN РегламентированныйПроизводственныйКалендарь 4 749 rows 0.4 s, КурсыВалют 4 387 rows 0.4 s, ЗначенияСвойствОбъектов 23 521 rows 5.0 s (1.7 % of a session at a 5-min interval — under the 10 % stop line). For D-6: KAN's telephony register МИКО_стИсторияЗвонковCDR (1 467 891 rows, not synced by the old Connector) takes 223 s per refresh (6 600 rows/s) — such a table needs ≥ 1 h or no refresh. ДокументыФизическихЛиц does not exist on KAN |
| S10 org routing | DONE 2026-09-30 | `Routing/OrgRouter` (reference → shared, movement → partition of its org, no orgRef on a multi-org base = config error never guessed into shared, unbound org → counted in `unmapped_orgs`, not sent; `Validate` refuses a classification mismatch with the backend; `NewlyBound`), snapshot and handlers route through `IPartitioner`; `SyncRoutingTests` 4/4. **Live, KAN (2 organisations)**: ПоступлениеТоваровУслуг with KANSLER bound, Kanstik not — bound partition 40 106 = 1C COUNT, shared 0, unmapped Kanstik 3 214 = 1C COUNT. The verify pass for a newly bound org is S11 |
| S11 recovery + fallback | DONE 2026-09-30 | `Verify/VerifyRunner` (version scan in 1C's order, `seen_run` stamps, changed/new → sync items, gone → deletes, one page in memory), `Verify/Recovery` (verify every table, then adopt the parked cursor + Incremental in one transaction; lost state → Recovery, never tail; fallback = one table per turn, round-robin); snapshot keeps no version for unbound-org rows so the verify after a binding sends them; host `versions` op in the reader; `SyncVerifyTests` 5/5 (scan order ≠ GUID order, reposted doc → recorder sync, crash mid-recovery keeps the old cursor, start modes, fallback turns). **Live**: KAN РеализацияТоваровУслуг 89 988 documents verified in 6.3 s (14 300/s; a re-read with sections is ~7 min) — 0 changed, 0 gone; bilim reset on a *copy* of its log (`cursor_file_gone`) → Recovery → 0 items → Incremental with the new cursor |
| Release gate before S12 dev (developer, 2026-10-01: one completely clean full run, 0 failed, 0 unexpected skipped, no leftovers; S12 dev only after it) | **MET 2026-10-01** (developer chose: isolate + fix, then 2 clean runs) | **Final gate, after the overnight fixes — runs 7 and 8, back to back on one clean build, in gate mode (`AIBA_TEST_GATE=1`: no hidden skips), live bilim + kansler, isolated local backend/1c up: 431/431 passed, 0 failed, 0 skipped — 9 m 48 s and 9 m 33 s.** Python-target contract tests ran for real (1.4 s / 1.1 s / 0.1 s), the 6 child-process scenarios and the 4 harness self-tests passed; after each run 0 testhost / OneC.Host / OneC.Supervisor / OneCLiveChild / AIBA.Connector / DevBench; `OneC.Host cleanup --dry-run --doc all`: bilim 327 document types, 0 `AIBA_REWRITE_` documents; kansler 201 types, only the known marked `Мирель_УстановкаФактЦен` copy (the COM user may not delete that type — row "Test copy left on KAN"). Before the fixes: **runs 5 and 6, back to back on one clean build** (`dotnet clean` + build Host, Supervisor, Desktop, Tests in Release; live bases bilim + kansler; isolated local backend/1c up, so `SyncPythonTargetTests` really ran — 3.8 s / 0.3 s): **414/414 passed, 0 failed, 0 skipped — 10 m 54 s and 9 m 30 s**; after each run 0 testhost / OneC.Host / OneC.Supervisor / OneCLiveChild; then 0 `AIBA_REWRITE_` documents in bilim and kansler (`DevBench leftovers`). The fix: the 6 live tests that open a SessionManager of their own (`MultiBaseTests` ×4, `ConnectionLifecycleLiveTests` ×2) run in a child process each (`tests/OneCLiveChild`, `LiveChild.Run`) — a 1C heap crash now fails that one test with its exit code instead of aborting the run; `PressureEvicts…` checks the rule (quiet base evicted, no self-eviction, no errors, file sessions ≤ the cap of 2), not the memory-dependent count of 1. The 6 moved tests 30/30 (×5) before the runs. History of the gate — four earlier clean-build runs (`dotnet clean` + build Host, Supervisor, Desktop, Tests; `dotnet test --no-build`, live bases bilim + kansler): **run 1** aborted after 367 passed (7 m 54 s) — testhost `0xC0000374` at ntdll+`0x117eb5` during `MultiBaseTests` (known limit below); **run 2** 414/414 passed, 0 skipped, 8 m 56 s — but the 2 `SyncPythonTargetTests` returned early (no `AIBA_SYNC_BACKEND`), so not a full gate; **run 3** (isolated local backend up) aborted after 404 passed (7 m 57 s), same crash, during `SyncLiveTests.ATestDocumentsWholeLife…` — left 1 test document in bilim; **run 4** (backend up) 413/414, 8 m 51 s: `MultiBaseTests.PressureEvicts…` created 2 file sessions where it expects 1 (the count depends on the process working set after eviction). After the runs: 0 testhost / OneC.Host / OneC.Supervisor left; 2 `AIBA_REWRITE_` documents found (bilim from run 3, kansler older) and deleted, recheck 0 (`DevBench leftovers`). The crash did not reproduce outside the test runner (`DevBench stress`: sequential managers with forced finalizers 25 rounds, concurrent eviction 2 × 90 s, two concurrent managers 3 × 100 s — only 1C's known exit-time AV) |
| S12 Python v2 target | **LOCAL COMPLETE 2026-10-01 · SHARED DEV BLOCKED on the developer's dev login + dedicated test company** (approved 2026-10-01 for dev/staging only; nothing was sent to dev) | Ready to run (`DEV_SYNC_TEST_RUNBOOK.md`, `tools/s12-dev.ps1`): `DevBackend` (dev host only, `*.aiba.group` refused; the app's Development session, refresh on 401; start guard: record name **and** odataName `SYNC-TEST…`, `connection_state` offline; `allowWithOldConnector` and rebuilds refused on dev), `sync-dev companies / check / create-record` (the one write), `sync-verify` (every stored row read back per partition vs a fresh canonical 1C read: missing, stale, duplicate keys, wrong partition, every field), `HttpFaults` (timings for p50/p95; injected 503 / dropped connection / bad token → real 401 / 400; crash after upload), `DevBench lifecycle` (AIBA_REWRITE_S12_ document on bilim). Tests: `SyncDevHarnessTests` 5/5 (faults, dev host only, start guard incl. a real base's odataName, rebuild refusal). **Contract fixes from reading `origin/development` (D46), proved on the isolated local backend:** `scope=self` everywhere (multi-org reconcile wiped organisations' movements — reproduced, now passes), `sync-tables` objects / null, backend table names (`…_RecordType`), 400 "not found" retried, binding 409 retried, oversize row → dead letter; `SyncPythonTargetTests` 4/4. Local throughput re-run (`s12-local-backend-2026-10-02.txt`): KAN ДоговорыКонтрагентов **116 759 rows in 57.1 s = 2 044 rows/s, supervisor 115 MB; stored rows counted in the isolated Mongo: 116 759, distinct keys 116 759** (the 2026-09-30 "read back" figure was backend/1c's `/counts`, its own counter — corrected). Not done: anything on dev; the read-only GUID-case check (S0 Q7) | `src/OneC.Sync.Targets.Python/PythonMongoSyncTarget` (upload multipart + 413 split, prune with the backend's max(1, 5 %) cap → Policy, approved deletes sized from the backend's own count, reconcile, org-bindings + sync-tables config with backend/1c's own `is_movement_table` rule, `connection_state` presence for D-1, 401 refresh-once, §18 status mapping). Tested against an **isolated local backend/1c** (own port 18041, own Mongo database `aiba_1c_sync_test` and Redis index 9 on the local Docker services, test secrets made for it — `scratchpad/s12/start-backend.ps1`): `SyncPythonTargetTests` 3/3 with every stored row read back (idempotent re-upload, same-key overwrite, v2 duplicate pinned: reports 2 stored while 1 is, cap refusal / approved chunked delete / empty-table refusal, reconcile, expired token refreshed, lost auth = Auth). Throughput into it (`measurements/sync-s0/s12-local-backend.txt`): KAN chart 432/432, ДоговорыКонтрагентов **116 759/116 759 rows read back, 71.8 s = 1 625 rows/s, 272.6 MB**, supervisor 118 MB. Found: backend/1c files every `InformationRegister_*` as a movement table (router follows it; org-less independent rows → shared, counted); `/connection/rebuild` is unauthenticated and without `oneCId` wipes every connection's cached metrics (D-9 list). Not done: the read-only GUID-case check on dev (S0 Q7) and any dev write |
| S13 dead letters + control API | DONE 2026-09-30 | `Errors/DeadLetterService` (retry, approve only `needs_approval`, dismiss recorded in runs, timed retry of transient ones, warning over 50), `ApprovedDeleteHandler`, `Engine/BaseSyncAgent` (mode machine: snapshot tables in order with feed drains between them, incremental, recovery, fallback turns, paused; D-1 presence refusal with a developer-only override; auth/gone/config → Paused with the reason), `DbDemand`, `SyncEngineHost` (status, pause/resume, rebuild with per-table confirmation — v2 answers "no purge" and changes nothing), edge routes `/v1/sync/*`, `run --sync-config` (stub or loopback target only). Deletes are always sent (a "never sent → skip" shortcut was removed after the S15 kill test caught it leaving a stale row); v2's "nothing stored" refusal = already gone. Scheduler fix: an item due now is `MinValue`, not "now" (it was never due for a tick that read the clock first). `SyncEngineTests` 6/6 (snapshot → feed, D-1 refused/overridden, lost auth → Paused → resume, poison item → dead letter while the rest continues → retry, dismissal recorded, rebuild v2/v3, lost state → Recovery); all sync engine tests 92/92 ×3. **Live through the edge (bilim, stub)**: snapshot 60 + 68 + 35 rows → Incremental in 33 s; pause/resume; rebuild 409 unconfirmed and 409 "no purge" on v2; posted test document + movements in the stub after 12.9 s, its deletion after 1.1 s; 0 test documents left |
| S14 WinUI Sync screen | BUILT 2026-09-30 — **awaits the developer's visual approval** | Settings → Preview → "Sync" switch (off by default, `ui.json` `syncPreview`; restarts the connector) + Open; `Views/SyncPage` (card per base: state in plain words — First copy x of y / Sending n changes / Up to date / Checking / Paused and why; last change seen; tables not in this 1C; rows waiting for an organisation link; "Needs attention" list: Delete in AIBA / Keep for refused deletes, Try again for the rest; Pause/Resume; "Copy a table again…" with a confirmation that says AIBA's rows are deleted first); `EdgeClient` `/v1/sync` calls; `SupervisorProcess` `--sync-config`; the app writes `sync.json` (stub target, state in the data folder, preview tables; missing tables skipped by the engine — §22). `ui.json` keeps other keys now. Run: isolated data folder, bilim only → card "Up to date" after its first copy (`measurements/shots/2026-09-30/sync2-page-light.png`); no processes left. **2026-10-01 (user: "check the UI detailed, and yet simple; why is it not in Infobases"):** sync moved into **Infobases** — each card's sync column shows the engine's state with a "Sync details" link (local-only bases the engine syncs get a card too); `SyncPage` replaced by `BaseSyncPage` (back to Infobases; Status / Last change in 1C / Tables copied / Changes waiting / Need your decision; warnings and decisions above a zebra table: Table, Type, Status, Rows, Waiting, Problems, Last sent; "Copy this table again…" in each row's menu); **Add table** (user: "add any table I search from 1C; Latin should find Cyrillic"): every catalog / document / chart / register of the base (`tables` host op: names + synonyms, 1 656 on bilim in 3 s; details only for the picked ones), search in either alphabet (`Translit`, 15 tests), several at once, the engine restarts and copies the new ones first (Организации added on bilim → 6 of 6, 1 row). Screens: `measurements/shots/2026-10-01/sync2-{infobases,base,addtable}-{light,dark}.png`; polling checked: two screenshots 7 s apart, 0 differing pixels (light and dark). **2026-10-02 (D48):** UI wording is "Sync" / "Sync preview" / "Sync is off" (no "v2"); fresh screens `measurements/shots/2026-10-02/sync-{infobases,base,addtable,settings}-{light,dark}.png` (the older `sync2-*.png` files are kept as recorded); checked on a data folder from before the rename: `syncV2Preview` carried over as `syncPreview`, a stale `sync2.json` deleted, `sync.json` written. **2026-10-03 (D50, user):** sync is no longer a Settings switch — it always runs for the connected bases (still to the local test target); Settings → Preview removed, "Sync is off" state removed, base screen subtitle "For now sync copies to a test target on this computer. Nothing is sent to AIBA yet."; verified on a fresh data folder with no `ui.json` (engine up, bilim Up to date 5 of 5); screens `measurements/shots/2026-10-03/sync-{infobases,base,addtable,settings}-{light,dark}.png`. **Awaits the developer's visual approval** |
| S15 scale + failure | DONE on local/stub/isolated backend 2026-09-30 (not a production go — D-2) | **Kill test** (`research/sync-spikes/ChaosBench`): the engine in a child process against a file-backed 1C with a real-format event log and the isolated local backend/1c; the parent changes 1C continuously (renames, new/deleted items, reposts with fewer/more lines and new amounts, new/deleted posted documents) and kills the engine at random 50 times, then lets it converge and compares every stored row with 1C. First run found 2 bugs (start-up feed drain; a "never sent → skip delete" shortcut that left a stale document when an upload landed before a kill) — both fixed with tests; **final runs, seeds 42 and 43: 50 kills, 3 155 / 3 495 changes, backend = 1C exactly (0 missing, 0 stale, 0 wrong amounts, 0 stale movements), engine peak 41–47 MB**; a leftover-lease fix (items of a killed engine were locked 5 min): **third run, seed 44: 50 kills, 3 667 changes, backend = 1C exactly, converged in 9 s instead of 195–266 s**, peak 43 MB. In-process scenarios (`SyncEngineTests`): 5 bases with a slow backend — ≤ 3 active, ≤ 6 sessions — then an outage: work waits in the queue, no pause, no dead letter, all 5 converge; log recreated mid-first-copy → Recovery → the remaining tables still copied; lost state → Recovery. Idle: S4 (23 real bases, 0.009 % CPU). Memory: engine ≤ 49 MB, supervisor ≤ 177 MB during snapshots, host 497 MB private at K = 4. Full release suite with live bases: 390/394 — the 4: two scheduler tests fragile under load (fixed: they wait for agent state now, 8/8 ×3), the desktop engine test and the native-crash test pass when run with the runtime in place (2/2; the suite had been built into a custom output folder). Not done literally: a 2-hour real outage (compressed with a test clock), a real 16 GB machine, 30 bases through the whole engine (the scheduler alone ran 23) |
| Bugs found 2026-10-01 (self-review of the Sync engine diff + a contract review against backend/1c `origin/development`) | FIXED, each with a test | **Python target:** `scope=connection` widened reconcile/prune to every partition → multi-org movements deleted on every post (`AReconcileOrPruneOfTheSharedPartition…`, failed first on the real server code); `sync-tables` read as strings (objects / null on the server); register names missing `_RecordType`; 400 "not found" dead-lettered; a binding's 409 paused the base; one oversize row retried forever. **Engine:** a failed mapper/uploader hung the snapshot forever, holding its leases and slot (`AMappingFailureEnds…`, `AnUploadFailureEnds…` — 30 s timeouts before, ~0.1 s after); one bad row on v2 stalled a first copy (batch halved to the row → dead letter); a user Pause or a rebuild overwritten by "snapshot complete" (`APauseDuringTheFirstCopyIsKept`); versions / hashes stored for rows never stored; a document of an unbound organisation got its version (never sent after binding); organisations bound later never sent — §21 trigger not built (`AnOrganisationBoundLaterGetsItsRowsSent`); charts refreshed through the register read and threw (`AChartOfAccountsIsRefreshedByCode`); approving a refresh's refused delete deleted nothing (`ApprovingARefreshsRefusedDelete…`); Fallback never ended once the log was readable, and a base without a readable log sat in "Incremental" never activating (`FallbackEndsOnceTheLogIsReadable`); a re-created object's pending approved delete still ran (merge table); one exception ended the scheduler loop for good; a failed base re-activated every 1 s (`AFailedActivationBacksOff…`); unknown old-Connector presence taken as "allowed" (`UnknownOldConnectorPresence…`); recovery took the log end after its verify pass (a change during the pass could be skipped) — now before; snapshot dead letters retried as object syncs whatever the table. **Resources:** dead letters counted by loading them all; the stub (desktop preview) logged every call forever; upload body copied up to 3×; run history never pruned; the Sync screen re-read table counts every 3 s (now 15 s); a rebuild error from the menu was lost |

## AIBA cloud account (login + link) — BUILT, stub-verified, not committed (2026-09-30)

User request after the first checkpoint. Decision D41; contract `specs/2026-09-30-auth-and-cloud-link.md`.

| block | status | file(s) | verified by |
|---|---|---|---|
| Login (phone + password), refresh on 401, DPAPI session, env Production / Development / custom | DONE | `src/OneC.Cloud/CloudClient.cs`, `SessionStore.cs`, `CloudEnvironment.cs` | `CloudTests` |
| Companies + link infobase → cloud 1C record (reuse or create) | SUPERSEDED 2026-09-30 by the old Connector's flow (row below); `LinkDialog.cs` removed | `CloudClient.cs`, `LinkStore.cs` | `CloudTests` |
| Old Connector's infobase flow (user 2026-09-30: the link dialog was "too weird and complex, just use the existing flow"): **Infobases** = the header company's cloud records (`GET /onec?companyId`), each matched to the local 1C connection with the same name as its odataName (the old app's only key), column "On this computer" = engine status or "Not on this computer"; **Add infobase** = software (Unisoft/Venkon) + one of this computer's 1C connections (ones the company already has are disabled) + name → `POST /onec` with the header company, configuration version read from 1C (major.minor, null if 1C doesn't answer in 15 s); local connections moved to their own **1C connections** page, Remove as a visible button (user: "can't delete?" when it sat under "…"; UI Automation run removed the id-named Kanstik duplicate from an isolated copy). No edit or delete of cloud records: the old app's delete (`DELETE /onec/connection/{id}`) purges the company's synced data in prod | DONE (awaiting the user's visual approval) | `Views/InfobasesPage.*`, `Views/AddInfobaseDialog.cs`, `Views/ConnectionsPage.*`, `CloudStub` (`AddRecord`, two seeded records) | UI Automation run against the stub, isolated data folder: company A lists the seeded records (kansler Ready, OldOffice Not on this computer), Add KAN → stub record `501 / KAN / KAN / unisoft / v=8.3`, list shows it Ready; `CloudTests` 20/20; on the user's real prod session the list loaded (company with no records: empty message) after ~40–60 s |
| Legacy status heartbeat (records created here only) | DONE | `StatusHeartbeat.cs`, `MainWindow.xaml.cs` | stub: `active` after 60 s, `inactive` on close |
| Desktop: sign-in dialog, "AIBA cloud" column, Link / Unlink, account in status bar | DONE (awaiting the user's visual approval) | `Views/SignInDialog.cs`, `InfobasesPage.*` | isolated UI run (`AIBA_CONNECTOR_DATA` = a scratch folder) against the stub |
| User feedback 2026-09-30: no prod/dev choice on screen (hidden Ctrl+Shift+D + a badge only off production); company picker in the header with search by name or INN; no account page — the header account button opens a sign-in dialog, or a menu with the user and Sign out | DONE | `MainWindow.*`, `App.xaml.cs` (`LoadCompaniesAsync`, `SelectCompany`), `SessionStore` (chosen company per env + user) | UI Automation run on the user's real prod session (read-only: company list only) |
| Infobases toolbar redesign (user: "ugly"): "Add infobase" + one "Import" menu on the left; the selected base's actions (Test, Link to AIBA / Change AIBA link, Edit) as icon + label on the right, Unlink and Remove under "…"; folds into "…" on narrow windows | DONE (awaiting the user's visual approval) | `Views/InfobasesPage.xaml` | screenshots at 1360 and 900 px; both menus' items checked through UI Automation |
| Whole-app polish (user: "everything needs some love"): aiba-next's neutral surfaces; accent = the user's Windows accent colour (user 2026-09-30: AIBA has no single brand colour, no gradients — a brief brand-blue override was removed); statuses as coloured pills (Infobases status + AIBA link, Activity result + engine events, Resources state); Infobases table cut to Name / Type / Location / 1C user / Status / AIBA (engine internals moved to Resources); plain words everywhere (Resources "Parts of the connector", "Connections to 1C"; engine events "Started", "Stopped unexpectedly, restarted"; dialogs "1C password", "Accounting software: Unisoft / Venkon"); one-line status bar | DONE (awaiting the user's visual approval) | `Styles/Theme.xaml`, `Controls/ResultTable.cs` (`Pill`, `PillFor`), every page | screenshot tour, dark + light, isolated data folder |
| Theme switcher in the header (user 2026-09-30): icon button next to the account, menu Light / Dark / Same as Windows; choice kept in `ui.json` in the data folder; `--theme` still forces one for screenshots without saving it; header menus get a solid background (`SolidMenuPresenterStyle`) | DONE (awaiting the user's visual approval) | `MainWindow.*`, `App.xaml.cs` (`ThemeChoice`, `SaveTheme`), `Styles/Theme.xaml` | UI Automation run, isolated data folder: each choice picked from the menu, `ui.json` checked after each, choice kept across a restart, screenshots light / dark / system |
| **1C connections = the 1C launcher's list** (user 2026-09-30: "why should the user pick server/file… in the old connector we did that ourselves… you can't add a connection yourself, just list what the user has"): rows come from `%APPDATA%\1C\1CEStart\ibases.v8i` (the old Connector's only source), kind read from the Connect line (`File=` / `Srvr=`+`Ref=` / `ws=`), plus connected bases the list no longer has; search, Refresh; Connect / Change login, Test, Disconnect. Connect asks only 1C user, password ("No password"), optional 1C version; user and password are filled from the old Connector's `config.json` when it has the base. Before saving, a one-shot `OneC.Host probe` (stdin carries the connection string) checks the login; a version mismatch is retried once with the version 1C names, if installed (old app: filesystem.rs:3504-3666). "Add connection", the type choice and Import are gone; `BaseDialog.cs` removed. Web (`ws=`) bases are listed as "Web base: not supported yet" — the old app could not connect them either (no OData reader in main.os). Not ported: the old 1C-user dropdown (reads `V8USERS` from `1Cv8.1CD` / the cluster DB) | DONE (awaiting the user's visual approval) | `OneC.Desktop.Core/LauncherBases.cs`, `SupervisorProcess.ProbeAsync`, `BaseStore.OldConnectorLogin`, `OneC.Host` `probe` mode, `Views/ConnectionsPage.*`, `Views/ConnectDialog.cs`; `ResultTable` keeps the selected row across a theme switch | probe on signum: wrong login → `auth`, 8.3.18 → message names 8.3.15.1565; UI Automation run on an empty isolated data folder: 25 launcher bases listed (21 file, 3 server, 1 web), Connect signum with 8.3.18 typed → saved with 8.3.15.1565, user filled from the old Connector, row Ready; web row's Connect disabled; unit test `LauncherListGivesEachBaseItsKindFromTheConnectLine`; desktop + cloud tests 29/29 |
| Tables no longer flicker on polling (user 2026-09-30: "when nothing changes the whole list glitches"): `ResultTable.SetData` with the same columns and widths patches an `ObservableCollection` of stable row items — gone rows removed, new rows inserted, moved rows moved (the ListView animates these), changed rows rewritten in place, unchanged rows untouched; stripes fixed after inserts/removals; `RowKey` per page (base name, record, part, infobase) so a status change is an in-place update | DONE (awaiting the user's visual approval) | `Controls/ResultTable.cs`, `ConnectionsPage`, `InfobasesPage`, `ResourcesPage` | UI Automation row RuntimeIds over 12 s of idle polling: old build 0 of 26 rows kept (every row re-created each tick), new build 26 of 26; with the engine starting, 26 of 26 kept through Starting → Ready; search "k" → 7 rows, all the same elements, cleared → 26, stripes correct |
| Infobases: **Sync** column (read-only: `lastError` → "Error: …", `totalCount` 0 → "Not synced yet", `percentage` ≥ 100 → "Synced", else "Syncing N%") and **Delete from AIBA** (confirm, `DELETE /onec/connection/{id}`, 404/410 = already gone, link forgotten so no heartbeat) — user: "where is the sync status?", "how can I delete this?" (D41 amended). Info pill ink now the accent's own dark/light shade per theme (it read faint when the app theme differs from Windows) | DONE (awaiting the user's visual approval) | `OneCRecord` (`TotalCount`, `Percentage`, `LastError`), `CloudClient.OneCDeleteAsync`, `LinkStore.RemoveRecord`, `InfobasesPage.*`, `CloudStub` (DELETE route, sync fields, 3 seeded records), `Styles/Theme.xaml` | `CloudTests` 22 (+ delete, + sync fields); UI Automation against the stub: Synced / Syncing 37% / Error shown, Delete disabled until a row is selected, confirm → stub record `deleting`, row gone. Not run against prod |
| Table + page redesign (user: "shitty UI and table", frontend-design rules): 14 px floor (cells, headers, labels), page titles 28, rows 40 px; the name column semibold, secondary columns (type, location, 1C base, time, details) muted; neutral statuses ("Not connected", "Not on this computer") drawn quiet — ring + words, no grey pill; other statuses tinted pills, capped at 300 px (long errors trim, tooltip whole); **row actions on the row** (1C connections: Connect, or Test + "…" Change login / Disconnect; Infobases: "…" Delete from AIBA) instead of a toolbar far from the selection; rows span the panel (a fill column takes spare width) and, when narrow, text columns give way (secondary first, the name last) so actions never hide behind a sideways scroll; empty states with icon, title, text and a button (Sign in, Add infobase, Refresh); 1C connections filter bar "All 26 · Connected 6 · Not connected 19", search, icon Refresh; a filter/search re-measures columns in place without rebuilding rows; Resources tiles no longer clip their labels | DONE (awaiting the user's visual approval) | `Controls/ResultTable.cs` (`RowCommand`, `CommandsFor`, `PrimaryColumn`, `MutedColumns`, `FillColumn`, empty state, `remeasure`), `Styles/Theme.xaml`, `ConnectionsPage.*`, `InfobasesPage.*`, `ActivityPage`, `ResourcesPage.*`, `MainWindow.SignInAsync` | screenshot tours dark + light against the stub (signed out, 1C connections all / Connected, Infobases with Synced / Syncing 37% / Error, the "…" menu with Delete from AIBA, Activity, Resources); UI Automation: no horizontal scroll at 1360 px (was 83% view), idle polling 26 of 26 rows kept, search "k" and back keep the same row elements |
| Infobases as **cards** (user: "still looks empty, wrong actions, wrong looking cols"): one card per record — icon tile, name + "1C base X · Software", sync (Synced / Syncing · 37% with rows "3 375 of 9 000" and a bar / Not synced yet / Sync error with the message), this computer (engine status, or "Not connected here" when 1C's list has the base, or "Not on this computer"), a main button that fits the state (**Browse** opens Browse data on that base; **Connect** opens the Connect dialog for the launcher base of that name), "…" → Delete from AIBA; fixed action width so columns line up across cards; updated in place by record id | DONE (awaiting the user's visual approval) | `Controls/InfobaseCard.cs`, `Views/InfobasesPage.*`, `MainWindow.Go`, `BrowsePage.OnNavigatedTo` | tours dark + light against the stub (3 records, aligned), "…" menu, Browse on the kansler card → Browse data opened with `kansler` selected (UI Automation); real app: empty state for a company without records |
| Real prod login | DONE by the user 2026-09-30 (own account, 14 companies listed from api.aiba.group) | — | user's screenshot |
| Local cloud stub (prod rules, error codes) | DONE | `src/OneC.CloudStub` | used by tests and the UI run |
| Connector WebSocket presence ("connected" in new cloud UIs, cloud → app commands) | NOT BUILT — needs the user (D41) | — | — |
| Sync upload to prod | NOT BUILT — needs the user (D41) | — | — |

Tests: 272/272 without 1C (20 new).

## Final verification pass — 2026-09-25

| item | result |
|---|---|
| Full Release suite, live bases | run 1: 252 total, 251 passed, 1 failed, 0 skipped (27.7 min). The failure (`SpawnsOneHostPerPlatform…`) is the bilim first-open stall: the 8.3.18 host's first Connect to bilim took 98 s, past the 65 s deadline, idle inside `dbeng8.dll`, nothing else had the base open; passes in 7 s once bilim is warm. Run 2 after the value fix: aborted at 188 passed — the heap-corruption crash (known limits) plus one test that could not bind its port (fixed below). Run 3 (final build): **252/252 passed**, 14.6 min |
| Test bug: five edge tests bound fixed ports 55901–55999 | FIXED — port 0 (OS-picked). The running old adapter's workers listen on 55901–55903, so `ChangesRouteServesTheFeed` failed "access forbidden" |
| Processes after the suite | 0 `testhost` / `OneC.Host` / `OneC.Supervisor` / app |
| Live parity against the running old adapter (KAN, read-only) | first run 4/13 identical → **bug found and fixed** (below) → **19/19 identical**: 9 catalog pages (incl. offset paging), 4 document lists, a document by id with every tabular section, information / accumulation / accounting register pages |
| Bug: empty values rendered unlike the old adapter | FIXED — `LegacyValue.Scalar`: 1C's empty date arrives over COM as 0100-01-01 and was sent as `"0100-01-01T00:00:00"` (old: `""`); 1C NULL was sent as `null` (old: `""`, `XMLСтрока(Null)`). Every catalog / document / register read renders through it. The oracle sweeps could not see it — the oracle shares the function (D33 note) |
| Desktop app visual pass | DONE, 6 defects fixed (M4 below); screenshots `measurements/shots/2026-09-25/` |
| Stack memory | idle 266 MB (was 269), after a 100-row read 409 MB (was 412) — P §4.5 |
| Test documents left | kansler: only the known `Мирель_УстановкаФактЦен` copy (marked). bilim: one undocumented write-parity draft (`AIBA_REWRITE_WP_0925002707`, АктСверкиВзаиморасчетов) — deleted by its run marker, 0 left. Checked with the new `cleanup --dry-run --doc all` over every document type (kansler 201, bilim 327) |
| Scratch file bases / temp credential files | none (only `D:\1C\bilim` holds a 1Cv8.1CD written in the last 4 days; no rewrite-made config files in %TEMP%) |

## MACHINE STATE — comcntr registration is back (seen 2026-09-25)

On 2026-09-23 the whole `V83.COMConnector` registration disappeared from this machine (not
done by this rewrite). On 2026-09-25 it is present again, pointing at 8.3.15.1565 (TypeLib
HELPDIR still 8.3.18 — someone ran regsvr32 for 8.3.15). The rewrite never needed it (D29);
the old adapter does, and it is running again (router :55899, KAN connected), which is what
made the live parity run above possible.

## INCIDENT 2026-09-23 — real documents deleted — CLOSED (user: leave them deleted)

An agent ran `OneC.Host cleanup --prefix AIBA_` (meant `AIBA_REWRITE_`). It direct-deleted
(`Удалить()`, irreversible over COM) every ПоступлениеТоваровУслуг whose comment starts with
`AIBA_`:

- **kansler (KAN, server): 64 documents**, dated 11.07–23.09.2026 — `A0000000001–A0000000022`,
  `К0000002843–К0000002980`, including other sessions' `AIBA_CLONE_TEST-*` documents.
- **bilim (file): 14 documents**, `0000-000001–0000-000014`.

Evidence: 1C event log, exported read-only → `measurements/deleted-kansler-2026-09-23.txt`,
`measurements/deleted-bilim-2026-09-23.txt`. Restoring needs a backup (KAN: the .dt / PG
backup per `D:\1C\HANDOFF.md`; bilim: a copy of `D:\1C\bilim`) or re-running the pipelines
that created them. **User decision (2026-09-23): leave them deleted** — local test copies.
Fix: `cleanup` refuses any prefix outside `AIBA_REWRITE_`; AGENT.md hard rule added.

**First checkpoint commit 2026-09-25** (user-approved, pushed to `origin/master`): everything
below as verified in the final pass. UI visually approved by the user the same day; the
2026-09-25 screenshots are the Milestone 4 baseline. Non-blocking polish for later: friendly
1C metadata names, advanced technical columns, automatic platform-version choice.

## Test state

`dotnet test tests/OneC.Tests -c Release`
- **2026-09-25, final build, Release, live bases, `--blame`: 252/252 passed, 0 failed,
  0 skipped (14.6 min)**; 0 `testhost` / `OneC.Host` / `OneC.Supervisor` left; 0
  `AIBA_REWRITE_` ПТУ left on either base afterwards.
- without `ONEC_TEST_BASES`: all pass (live tests return early)
- with `ONEC_TEST_BASES=bases.local.json`: **206/206 passed** (2026-09-24, Debug, `--blame`,
  5 min), no test host or host process left afterwards (D37). Earlier: 77/77 (2026-09-23).
- live tests write only `AIBA_REWRITE_*` documents, and only to the local copies (bilim file
  base, the KAN restore on this PC) — never a client's live base.

50 unit tests (no 1C) + 27 live tests (real `kansler` server base and `bilim` file base,
including real child host processes). Phase 1 was 48/48; after 2.1 65/65; after 2.2 69/69.

## Research — DONE

| item | where |
|---|---|
| COM threading / finalizer crash / one connector many sessions | `research/com-threading-probe/` |
| Write + file-base concurrency, pooling, failure isolation, CRUD, posting, lanes, soak | `DOTNET_ONEC_CONCURRENCY_RESEARCH.md`, `research/onec-concurrency/` |
| Reg-free activation, mixed versions, typed interop, dynamic vs IDispatch, EXCEPINFO | `DOTNET_ONEC_INTEROP_RESEARCH.md`, `research/interop/` |
| WinUI shell visual prototype (mock data, throwaway) | `prototypes/winui-aiba-shell/` |

## Foundation — phase 1

| block | status | file(s) | verified by |
|---|---|---|---|
| Exact-path comcntr activation | DONE | `src/OneC.Interop/ComActivator.cs` | live tests; `read` with explicit `--comcntr` for 8.3.15 and 8.3.18 |
| Refuse second comcntr in-process | DONE | `ComActivator.Bind` | code path; research I §2 |
| Platform discovery from disk, exact/floor selection, PE bitness | DONE | `src/OneC.Interop/PlatformCatalog.cs` | 4 unit tests; `info` |
| Controlled IDispatch (`Call/Get/Set`, EXCEPINFO, BSTR free) | DONE | `src/OneC.Interop/Dispatch.cs` | live tests; `errors`; parity test |
| DISPID cache | REMOVED (built, measured unsafe) | — | `probe-dispid` |
| HRESULT table | DONE | `src/OneC.Interop/Hr.cs` | unit tests |
| Structured errors (`OneCException`, `OneCLayer`, `Retry`) | DONE | `src/OneC.Interop/OneCException.cs` | 6 unit + 3 live tests; `errors` |
| Deterministic RCW ownership (`ComRef`, `ComScope`, counters) | DONE | `src/OneC.Interop/ComRef.cs` | 6 unit tests; leak live test |
| Thread-affine sessions | DONE | `src/OneC.Sessions/SessionWorker.cs` | all live tests; `churn` thread counts |
| Lazy bounded per-base pool, idle retire, LRU evict | DONE | `src/OneC.Sessions/SessionManager.cs` (`BasePool`) | cap + retire live tests; `mem`; `lazy` |
| Host session manager, count + working-set budget | DONE | `src/OneC.Sessions/SessionManager.cs` | `bench --maxws 400` |
| Pool options incl. `ConnectorPoolCapacity=0` | DONE | `src/OneC.Sessions/PoolOptions.cs` | `poolram`, `probe-connectorpool` |
| Base model (file/server, K ceiling, RAM estimate, safe conn string) | DONE | `src/OneC.Sessions/OneCBase.cs` | 3 unit tests |
| Host planning (bases → fewest processes) | DONE | `src/OneC.Host/HostPlan.cs` | 3 unit tests; `plan` → 2 bases, 1 host |
| **Read vertical slice** | DONE | `src/OneC.Host/ReadService.cs`, `OneCValue.cs` | live read, parity with `dynamic`, error paths |
| Resource sampler (WS, private, managed, threads, handles, CPU, ComRefs) | DONE | `src/OneC.Host/ResourceSampler.cs` | used by every measurement |
| Measurement CLI | DONE | `src/OneC.Host/Program.cs` | modes `info plan read mem bench churn poolram lazy errors probe-dispid probe-connectorpool` |
| RAM/CPU baseline measurements | DONE | `DOTNET_REWRITE_PERFORMANCE.md`, `mem-matrix.csv` | — |
| Two hosts, two versions, concurrently | DONE (manual CLI) | — | 8.3.15→kansler + 8.3.18→bilim both returned rows |

### Fixes made during phase 1

- File bases chose the newest install → 2 processes for 2 bases. Now the oldest acceptable
  one → 1 process.
- File-base `MaxConcurrency` 4 → 2 after measuring ~210 MB per session.
- Added working-set budget; budget pressure waits instead of throwing.
- Removed DISPID cache (per-type DISPIDs collide).
- Session dispose no longer joins a thread under the pool lock.
- Cancelled `Execute` now marks the session broken.
- Live tests made robust to the background sweeper and process-wide counters.

### Key measurements (full table in `DOTNET_REWRITE_PERFORMANCE.md`)

- Host + connector: 35 MB WS.
- Server session ~50 MB (first one +129 MB incl. comcntr warmup). K=4: +278 MB.
- File session ~210 MB (first +268 MB). K=4: +896 MB.
- 3 600 server reads: WS 309 → 317 MB, flat. 1 800 file reads: 520 → 522 MB.
- Churn: threads/handles flat; WS creeps ~3 MB per session cycle (1C-internal, not per read).
- Connector `PoolCapacity=4`: reconnect 0 ms but released memory never returns.

## Milestone 2.1 — write vertical slice — DONE (2026-09-23)

| block | status | file(s) | verified by |
|---|---|---|---|
| `WriteService`: create-by-clone, update, post, mark/unmark, delete, find-owned | DONE | `src/OneC.Host/WriteService.cs` | 7 live write tests; `write` CLI on both bases |
| Ownership guard (only AIBA-created docs are modified) | DONE | `WriteService.LoadOwned` | refuses a real KAN doc for update/post/delete/mark |
| Per-(base, operation) write gates, D12 limits | DONE | `src/OneC.Host/WriteGates.cs` | 6 unit tests |
| `SessionManager.GetBase` | DONE | `src/OneC.Sessions/SessionManager.cs` | used by write gates |
| Posting | DONE on file base; KAN later RESOLVED (1C side, see known limits; 5.8) | — | file-base post test asserts `Проведен = true` |
| Write CLI + cleanup CLI | DONE | `Program.cs` modes `write`, `cleanup` | 0 marker docs left on both bases |
| Write RAM/throughput measurements | DONE | `DOTNET_REWRITE_PERFORMANCE.md` §2.1 | — |

Findings: write-active sessions cost ~2× read-only ones (server ~95 MB, file ~400–460 MB);
first cold write on the file base 46–157 s; server writers at the pool cap starve a reader.

## Milestone 2.2 — several bases in one host — DONE (2026-09-23)

| block | status | file(s) | verified by |
|---|---|---|---|
| Multi-base scenario (A busy / B quiet / C pressure / D fairness) | DONE | `src/OneC.Host/MultiBaseScenario.cs`, CLI `multi` | full A→D run in one process |
| Process-lifetime shared connector (crash fix) | DONE | `ComActivator.SharedConnector`, `SessionManager` | `ManagersCanBeRecreatedBecauseTheConnectorIsShared`; full run |
| Serialised admission + pending-memory reservation (budget overshoot fix) | DONE | `SessionManager.TryTakeBudget` | 600 MB cap: 753 → 396 MB peak |
| No self-eviction under pressure | DONE | `SessionManager.EvictOneIdleAnywhere` | `PressureEvictsTheQuietBaseNeverTheRequester` |
| Pool stats split: retired / evicted / broken / recycled | DONE | `PoolStats` | `multi` output |
| Quiet base returns its sessions | DONE (behaviour already correct) | — | phase B; `AQuietBaseGivesItsSessionsBack` |
| Read/write fairness measured; shared pool kept | DONE (D23) | — | phase D |

Tests: **69/69** live, 3 consecutive runs; 69/69 without 1C.

## Milestone 2.3 — long churn / memory plateau — DONE (2026-09-23)

| block | status | verified by |
|---|---|---|
| `churn` trend output (slopes, CSV, `--keep`) | DONE | `measurements/churn-*.csv` |
| `grow` mode (one warm session, many write pairs) | DONE | server 300 pairs, file 100 pairs |
| Verdict recorded | DONE | DECISIONS D24 |

Verdict: warm sessions are flat under long use; connect/disconnect churn leaves native memory
in comcntr (server ~+200 MB / 120 cycles, file ~+460 MB / 90 cycles); full reclaim needs a
host restart → supervisor recycling in 2.4. One non-reproducible `0xC0000005` during file
churn → the supervisor must survive host crashes.

## Milestone 2.4 — supervisor + multi-version hosts + recycling — DONE (2026-09-23)

| block | status | file(s) | verified by |
|---|---|---|---|
| `HostPlan` moved to `OneC.Sessions` (no COM, usable by supervisor) | DONE | `src/OneC.Sessions/HostPlan.cs` | existing plan unit tests |
| Host `serve` mode: config over stdin, line-JSON `ping/stats/version/read/sweep/quit`, exits on stdin close | DONE (diagnostic channel only) | `src/OneC.Host/ServeMode.cs` | supervisor tests |
| `OneC.Supervisor` project: spawn per host key, ready handshake, routing by base | DONE | `src/OneC.Supervisor/HostProcess.cs`, `Supervisor.cs` | `verify` CLI; 5 live tests |
| Restart dead host with backoff | DONE | `Supervisor.RestartWithBackoff` | kill test: back in 2.6 s |
| Recycle zero-session host above threshold (default 400 MB) | DONE | `Supervisor.TickCore` | auto + manual recycle; warm-session host never recycled |
| Two versions at once from one plan | DONE | — | 1C reports 8.3.15.1565 and 8.3.18.1289 from separate pids |
| No orphan hosts | DONE | — | 0 `OneC.Host` after dispose, after every test run |
| Connect/release overlap stress mode | DONE | `Program.cs` `connstress` | 690 cycles, 0 crashes |
| File-base sharing violation retryable + Connect retry | DONE | `Retry`, `BasePool.OpenWithRetry` | 2 unit tests; live retry firing not observed |

Tests: **77/77** live ×2, 77/77 without 1C.

**Milestone 2 (foundation completion) is DONE.**

## Next milestones

| item | status |
|---|---|
| Write vertical slice | DONE |
| Several active bases sharing one host | DONE |
| Long churn — find the memory plateau | DONE |
| Multi-version host spawning from `HostPlan` (supervisor) | DONE |
| IPC transport benchmark (pipe / HTTP / gRPC) | DONE — `research/ipc-transport/README.md` |
| IPC transport decision | DONE — pipes inside, HTTP at the edge (D28, user, 2026-09-23) |
| IPC contract + implementation (milestone 3) | DONE — see below |
| WinUI integration (milestone 4) | DONE — visually approved by the user 2026-09-25 |
| Subsystem migration 5.1–5.9 | DONE (5.9 real target PARKED); 5.10 out of scope |

## Milestone 4 — WinUI integration — DONE (visually approved 2026-09-25)

| block | status | file(s) | verified by |
|---|---|---|---|
| 4.0 Read-path references: query-side ПРЕДСТАВЛЕНИЕ, `refs=text/guid/both` | DONE (D30) | `ReadService.cs`, `OneCValue.cs`, `Operations.cs` | `ReferenceColumnsComeBackAsPresentationGuidOrBoth`; `refbench`; 100k register read 87.7 → 31.0 s |
| Bug found: `Строка()` does not exist on the COM connection — every reference rendered as a bare number | FIXED | `OneCValue.cs` | same test |
| 4.1 Supervisor `run --bases-stdin --port 0`; edge `/v1/events`, `/v1/activity` (bounded 500), `/v1/supervisor` | DONE | `OneC.Supervisor/Program.cs`, `EdgeServer.cs` | used by the desktop app |
| Bug found: pool waiters starved under memory pressure (25 ms polling vs instant re-rent) → FIFO hand-off queue | FIXED | `SessionManager.cs` (`AcquireSession`, `Park`) | `PressureEvicts…` 5/5, suite 103/103 ×2 |
| Bug found: cancel racing a finishing request could end the host's pipe loop (disposed token) | FIXED | `PipeServer.cs` (`HandleFrame`) | cancel test 12/12 alone, suite runs |
| Pipe names unique per host instance (nonce) | DONE | `OneC.Ipc/Envelope.cs`, IPC_CONTRACT §1 | unit test |
| 4.2 `src/OneC.Desktop`: DPAPI base store, supervisor launcher, edge client | DONE (D31) | `Services/*.cs` | app starts engine; closing the app leaves 0 supervisor/host processes |
| 4.3 Screens: Infobases (add/edit/remove/test/import), Browse, Activity, Resources | DONE | `Views/*`, `Controls/ResultTable.cs` | all four screenshotted, light + dark, 1360 and 900 px |
| 4.4 Visual check | DONE (approved by the user 2026-09-25) | `tools/shoot.ps1`, `measurements/shots/` | fixed: centred table, frozen column widths, form wrap, query echo in errors, stale "engine not running" empty state, invisible caption buttons in forced dark |
| 4.5 Stack measurements | DONE | `tools/measure-stack.ps1`, P §4.5 | idle 269 MB, after a read 412 MB, 0 processes left after close |
| Desktop core split out for tests (`OneC.Desktop.Core`) | DONE | base store, edge client, supervisor launcher | 8 unit + 1 live test (app launch path end to end) |
| Intermittent "Test Run Aborted" with no crash record | FIXED (likely) | `ConnectorApi.Connect` `ref IntPtr` | reproduced 1/3 → 0/5 with the fix (D27 update) |

| 4.4b Second visual pass (2026-09-25): every screen, light + dark, 1360 / 900 / 1280×720 / 1536×864 (125 % / 150 % proxies — display scaling itself is a system setting, not changed), long Russian text, error, loading, empty, engine killed + restarted, add-infobase dialog | DONE | `measurements/shots/2026-09-25/`, flows driven by UI Automation | fixed: Infobases buttons clipped at 900 px (import group wraps); failed read left the previous rows under the error; empty header band on a cleared table; status text did not trim and there was no way back after the engine died ("Restart engine" link); Infobases rows lagged "Starting…" up to 3 s behind the status bar; Activity showed a 201 create as "Failed" and raw op names; version box in the add dialog opened empty |

Milestone 4 is DONE; the user approved the screens on 2026-09-25 (baseline:
`measurements/shots/2026-09-25/`). Not implemented, so not checked: a dashboard page and a
settings page (the app has Infobases, Browse, Activity, Resources and the infobase dialog).
UI language is English only (D31, product decision pending).

## Milestone 5.1 — infobase config + connection lifecycle — DONE

| block | status | file(s) | verified by |
|---|---|---|---|
| File-base pre-check (`1Cv8.1CD`), category `path` | DONE | `SessionWorker.Pump` | `MissingFileBaseFailsFromTheFileSystemWithoutTouchingCom` |
| Machine-wide file-connect lock (same file as old adapter) | DONE | `OneC.Sessions/FileConnectLock.cs` | unit (exclusive, timeout, release) + `FileBaseConnectWaitsForTheMachineWideLock` |
| Error categories (old adapter codes) → `OneCException.Category` → `IpcError.category` → app messages | DONE | `ErrorCategory.cs`, `EdgeClient.Friendly` | 11-case parity table |
| Circuit breaker 3 × / 60 s | DONE | `BasePool.TryCreate` | `ThreeFailedConnectsOpenTheBreakerAndTheFourthFailsFast` |
| `test` op + `/v1/bases/{b}/test`, used by the app's Test button | DONE | `Operations.TestOp`, `EdgeServer` | desktop engine live test |
| Idle-session liveness probe on borrow | DONE | `SessionWorker.Probe`, `AcquireSession` | `DeadIdleSessionIsReplacedOnBorrow` |
| Import old Connector config (read-only) + app button | DONE | `BaseStore.ImportOldConnectorConfig` | `OldConnectorConfigImportsComBasesAndSkipsHttp` |
| Parity run against the live old adapter | DONE (spot check, KAN) 2026-09-25 | scratch `oldparity.mjs` (read-only GETs to the running :55899 router) | 19/19 identical after the empty-value fix; bilim is not served by the running old adapter, so file-base parity still rests on the oracle |

Tests: **135/135** live, 4 consecutive clean full-suite runs (`--blame`, 2026-09-24).
Milestone 5.1 is DONE; the live-parity spot check ran on 2026-09-25 once the registration
was back.

## Milestone 5.9 — cloud sync — DONE against the local stub; real target pending the user (2026-09-25)

User decision: local stub first (D39). Nothing has been sent to any backend.

**Removed 2026-10-02 (D48).** This engine was replaced by Sync (D42) and deleted: every file named
below (`OneC.Sync/*` of that time, `SupervisorSource.cs`, `SyncMode.cs`, `SyncTests`), its
`run --sync-config` wiring and its `GET /v1/sync` route. The names `OneC.Sync`, `--sync-config`,
`/v1/sync` and `SyncMode` now belong to the new engine. Recoverable from git commit `f81489e`.
The table is kept as history.

| block | status | file(s) | verified by |
|---|---|---|---|
| backend/1c contract mapped from source (upload, counts, prune-missing, reconcile-recorder, table names, row identity) | DONE | — | read-only map (backend/1c `development` = `production` on these files; connector `release`) |
| Table names, row key (`id` / chart code / `recorderRef#lineNo`), `__rowHash`, JSON written as JavaScript writes it | DONE | `OneC.Sync/SyncTable.cs`, `RowIdentity.cs` | 9 unit tests |
| Upload client: multipart with filename, plain JSON (backend has no gzip), 34 MB chunks, 413 split, 5 retries | DONE | `UploadTarget.cs` | unit tests against the stub incl. a 413-injecting handler |
| Local stub backend with backend/1c's semantics (upsert by key, content dedup, auth, prune guard, recorder reconcile) | DONE | `StubBackend.cs` | unit tests |
| Engine: tail cursor first, resumable cold read (catalog keyset, document/register date cursor), then change-feed replay: re-read changed objects by id, recorder reconcile on Post/Unpost/Delete, prune on Delete, re-read everything on a feed reset | DONE | `SyncEngine.cs`, `SyncState.cs` | engine test on a fake 1C (repost, delete, rename, new, resume) |
| Register read by recorder (new op args `recorderDocument`, `recorderId`) | DONE | `RegisterReadService.ByRecorder` | live test |
| Reads over the supervisor's pipe ops + feed | DONE | `OneC.Supervisor/SupervisorSource.cs` | live |
| Scheduler (per-base loop, no overlap, backoff, status) + `run --sync-config`, `GET /v1/sync`, `sync` CLI mode | DONE | `SyncScheduler.cs`, `Program.cs`, `SyncMode.cs`, `EdgeServer.cs` | scheduler unit test; `sync` mode on KAN |
| Live, KAN (local copy) | DONE | — | cold read: Банки 500/500, ПТУ since 01.09 19/19; `AWrittenPostedThenDeletedDocumentFlowsThroughTheFeed`: a posted copy reached the stub with all its accounting movements through the feed; deleting it removed both (87 s) |
| Real target (backend/1c or aiba-next) + credentials | PARKED (user, 2026-09-25: "local is enough for now") | — | user JWT needed for upload (service secret is refused there) |
| Organisation partitions (multi-org: movements per org oneCId) | NOT DONE | — | needs the backend's org → oneCId map |
| Chart of accounts (`ChartOfAccounts_*`) | NOT DONE | — | no chart read ported yet; reported as skipped |
| Bases whose event log records no data events | NOT DONE | — | the feed would see nothing; needs a periodic re-read fallback |

## Milestone 5.8 — posting — DONE (2026-09-25)

| block | status | file(s) | verified by |
|---|---|---|---|
| Posting = one `Записать(Проведение, Неоперативный)`; failure writes nothing and carries 1C's messages | DONE (5.7) | `DocumentWriter.WriteObject` | ПТУ live tests (bilim, KAN) |
| KAN blocker recognised: `posting_blocked_by_configuration` + what to change | DONE | `DocumentWriter.KnownBlocker` | 3 unit cases (English and Russian texts) |
| Per-type posting sweep on KAN (posted copies of each type's newest posted document, compared, deleted) | DONE | `writeparity --post` | **67 types: 41 posted copies identical, 0 differ, 0 refused by the writer**; 26 refused by the configuration's posting rules with 1C's own reason in the error (a copy duplicates period-keyed register records, needs stock the original used, repeats a period close, one invoice/sale per base document, manual movements, a config bug `Префикс`); nothing left behind |

## Milestone 5.7 — document writes — DONE (2026-09-25)

User decision: corrected API — old routes, body and markers, without the old quirks (D38).

| block | status | file(s) | verified by |
|---|---|---|---|
| Old write contract + callers mapped (routes, body, ref resolution, 34 quirks; live callers: Odoo webhook, avtoprovodka WS, admin verbs) | DONE | — | two read-only maps (main.os @3ea6a81, backend/1c, connector TS, cloud-os) |
| Body parser (old keys, strict dates, marker checks, directives) | DONE | `WriteBody.cs` | 20 unit tests |
| Write schema (field types by XML name, string lengths, wide sets) | DONE | `WriteSchema.cs` | sweeps |
| Reference resolver (exact, active, owner-scoped, ambiguity = error, explicit create only) | DONE | `RefResolver.cs` | sweeps; `UnresolvedReferencesRefuse…` |
| Create: idempotency (active only, serialised), convert, fill-empty, refuse-before-write, one Записать, 1C messages | DONE | `DocumentWriter.cs` | sweeps below; `RetriesAreIdempotent…`, `UnresolvedReferences…`, `ATooLongString…`, `AnOldStyleBodyRoundTrips…` |
| Update (PUT/PATCH), post, unpost, DELETE ?hard | DONE | `DocumentWriter.Update`, `WriteService` | `UpdateChangesAnOwnedDraft…` (KAN); `FileBaseCreatePostedThenUpdateUnpostAndPost` (bilim + a fresh copy) |
| Ops + edge routes, `error.data`, `unprocessable` → 422, 201 on create | DONE | `Operations.cs`, `EdgeServer.cs`, `Envelope.cs` | `WriteRoutesSpeakTheOldContractThroughTheEdge`, `ExchangeModeIsRefused…` |
| Round-trip write sweep (copy each type's newest document through the writer, compare, delete) | DONE | `WritePayload.cs`, `WriteParityScenario.cs` | **KAN 76/76 writable types identical**, 3 refused by the configuration's own rules (one invoice per base document, one sale per order, a config bug `Префикс`); **bilim 13/13 identical**, 1 refused (invoice rule). Found + fixed: bank-account fill outside bank documents; ДатаСоздания / КраткийСоставДокумента are recomputed by the configuration |
| Posting sweep (post=true copies) | NOT RUN | — | posting is blocked on KAN by its configuration; on bilim the file-base stalls (below) make a 14-type run take hours; posting is covered by the ПТУ live test |
| Test copy left on KAN | NOTE | — | one `AIBA_REWRITE_WP_` Мирель_УстановкаФактЦен copy: the COM user may not delete that type ("Access violation!"), so it is marked for deletion; `cleanup` now falls back to the mark |
| File bases stall for minutes (connect, `Записать`, `Удалить`): the session thread waits inside 1C's file-DB engine (`dbeng8.dll`) while polling the base files; no other process holds them; two unrelated bases (bilim and a fresh copy) stalled at the same moment; stalls of 10–45 min, then continue on their own | OPEN (cause unknown) | — | 2026-09-24/25, five episodes. Ruled out: another holder (Restart Manager), the new TerminateProcess exit (two back-to-back write processes on a fresh copy ran in 89 s / 6 s), HASP seats (license manager: 0 sessions), disk/RAM. Not seen on the server base. Never kill a stalled 1C process — that only adds a dead session |

## Milestone 5.6 — change feed (1C event log) — DONE (2026-09-24)

| block | status | file(s) | verified by |
|---|---|---|---|
| Format parsers (lgf dictionary, lgp records, ref hex → UUID) ported from the connector's Rust | DONE | `src/OneC.EventLog/LogFormat.cs` | the Rust tests' verbatim fixtures (6 unit tests) |
| Pull reader: tail start, cursor, cap, recreated/truncated/gone → reset, UTF-8-exact offsets | DONE (D36) | `EventLogReader.cs` | unit tests incl. 50 records through 1 000-byte reads |
| Server-base discovery (ragent command line, 1CV8Clst.lst, local-host check) | DONE | `LogLocator.cs` | unit + KAN resolved live |
| Transaction semantics (rolled-back by transaction marker, not the record letter) | DONE (D36) | reader | `ARolledBackWriteIsNotAChange` |
| `GET /v1/bases/{b}/changes` in the supervisor (no host, no session) | DONE | `Supervisor.Changes`, `EdgeServer` | `ChangesRouteServesTheFeed` |
| Oracle: 1C's `ВыгрузитьЖурналРегистрации` | DONE | `EventLogExport.cs` (diagnostic), `logparity` | identical on bilim and KAN with a written + deleted document |
| Measurement | DONE | P §5.6 | KAN 2 GB log: 98 MB/s, 119 MB private |

## Milestone 5.5 — bulk / cold reads — DONE (2026-09-24)

| block | status | file(s) | verified by |
|---|---|---|---|
| Balanced period slices (base table grouped by day), `slices` op + `GET …/slices/{kind}/{name}` | DONE (D35) | `SlicePlanner.cs`, `Operations.cs`, `EdgeServer.cs` | 2 unit + `SlicesOpAnswersThroughOperations` |
| Concurrent slice walks on pool sessions, registers and documents | DONE | (callers walk slices with existing reads) | `RegisterSlicesReadConcurrentlyEqualOneWalk`, `DocumentSlicesReadConcurrentlyEqualOneWalk` |
| Exclusive `to` on accounting reads (no second in two slices) | FIXED (D34 #6) | `RegisterReadService.Window` | slice-union test |
| XDTO bulk path | NOT PORTED (D35) — measured ≤ 7% gain | `XdtoBench.cs` (diagnostic) | P §5.5 |
| Measurements | DONE | P §5.5 | K=4: 2.9× (1 737 rows/s), identical rows |
| Zombie `testhost` (can't be terminated, still maps DLLs) after one suite run | FIXED (D37) | see "Blocked / known limits" | it was 1C's ImageMagick crash filter looping after an exit-time crash; 0 left after the 206-test suite |

## Milestone 5.4 — register reads — DONE (2026-09-24)

| block | status | file(s) | verified by |
|---|---|---|---|
| Old paths mapped (information :17911, accumulation :18078, accounting :18286, route :22487) | DONE | — | D34 |
| Columns + types from `ВЫБРАТЬ ПЕРВЫЕ 0 *` (accounting: `.ДвиженияССубконто` on an empty window) | DONE | `RegisterSchema.cs` | sweep |
| Accounting: VT period parameters + row-count window, account codes, recorderRef/lineNo/orgRef | DONE | `RegisterReadService.cs` | parity, `AccountingCursorWalkIsLossless` |
| Information/accumulation: stable tie-break, next cursor + hasMore for every kind | DONE (D34 #1–2) | same | `AccumulationCursorWalkIsLossless` |
| ДокументыФизическихЛиц.Физлицо embeds the person (old by-id shape) | DONE | `RegisterReadService.Person` | sweep (bilim/KAN) |
| `register` op; `GET …/registers/{name}` and `…/registers/{kind}/{name}` | DONE | `Operations.cs`, `EdgeServer.cs` | `RegisterRoutesWorkThroughTheEdge` |
| Parity oracle + sweep | DONE | `LegacyRegisterOracle.cs`, `RegisterParityScenario.cs` | **1 149/1 149 registers identical** |
| Found: VT column probe without period ran > 10 min on KAN | FIXED | empty-window probe | register tests 25 s |
| Found: route-point references have no Номер | FIXED | `MetadataShapes.Shape` | KAN Рецензии |
| Found: a column named `end` (keyword) breaks explicit select lists | FIXED everywhere | `QueryKit.Alias` (`Т.`) | МИКО registers; all sweeps re-run clean |
| Found (5.3, fixed for all reads): per-value reference reads leak 1C's object cache | FIXED | `RefBatch.cs` | walks flat; sweeps identical. Lookup arrays now hold each reference once (a register page repeats its Регистратор per line): re-swept 682 / 525 / 1 149 identical, 0 mismatches |
| Measurements | DONE | P §5.4 | Хозрасчетный 537 rows/s flat ~135 MB; first page 452 ms vs 2 580 ms |

Tests: **186/186** live, 2 consecutive clean full-suite runs (2026-09-24).

## Milestone 5.3 — document reads — DONE (2026-09-24)

| block | status | file(s) | verified by |
|---|---|---|---|
| Old paths mapped (list :7421, by-id :8191, batch :8317, per-section :8480, filters :7330–7419, route :22243, bank alias :583) | DONE | — | D33 |
| Metadata walk shared with catalogs; one schema cache for both | DONE | `MetadataShapes.cs`, `DocumentSchema.cs` | both sweeps |
| List: keyset, date cursor + next skip, `[from, to)` window, count rule, projection, `tabular=false` | DONE | `DocumentReadService.cs` | parity + `DateCursorWalkVisitsEveryDocumentOfTheWindowOnce` |
| Filters: aliases, bool/ISO casts, day (timesheet rule), period, contract post-filters | DONE | `DocumentFilters.cs` | 6 unit tests mirroring the old helpers; `FiltersNarrowTheRows` |
| By-id + batch (every section, `[]` when empty), request order, 200 per chunk | DONE | same | parity; `ByIdHasEveryTabularSection…` |
| Bank-document name alias | DONE | `DocumentSchemas.ResolveBankAlias` | `BankDocumentNamesOfEitherConfigurationWork` |
| `document` op; `GET …/documents/{t}`, `GET …/{id}`, `POST …/batch` | DONE | `Operations.cs`, `EdgeServer.cs` | `DocumentRoutesWorkThroughTheEdge`, mapping unit test |
| Parity oracle + whole-configuration sweep | DONE | `LegacyDocumentOracle.cs`, `DocumentParityScenario.cs` | **525/525 document types identical** |
| Found: one-query list sorted the whole table (joins) — 4.6 s per 20-row page | FIXED | two-phase list | 0.7 s |
| Found: old `ВЫБРАТЬ *` fails on 2 tabular sections, silently dropped | NOT PORTED (bug) | D33 #7 | sweep "old fails" lines |
| Shared helpers out of the catalog service | DONE | `QueryKit.cs` (`TabularSections`) | catalog tests still green |
| Cancel test re-based (row loop got ~2× cheaper, `Выполнить` dominated) | FIXED | `SupervisorTests.cs` | 6/6 alone |

## Milestone 5.2 — catalog reads — DONE (2026-09-24)

| block | status | file(s) | verified by |
|---|---|---|---|
| Old path mapped (`ПолучитьЭлементыСправочника` :5983, by-id :6455, value rules :2871, org :8161, ЭтоГруппа probe :8697, params :21767) | DONE | — | D33 |
| Catalog schema from metadata, cached per base (10 min, dropped on a 1C error) | DONE | `CatalogSchema.cs` | sweep |
| Old row shape + value rules, query-side dereference | DONE (D33) | `CatalogReadService.cs`, `LegacyValue.cs` | parity |
| Keyset paging (`Ссылка > &after`, always with WHERE), offset paging, count rules, `_ownerInn`, projection, by-id | DONE | same | `CatalogLiveTests` (10 live) |
| `catalog` op + `GET /v1/bases/{b}/catalogs/{name}[/{id}]` with the old query parameters | DONE | `Operations.cs`, `EdgeServer.cs` | `CatalogRoutesWorkThroughTheEdge`, unit mapping test |
| Parity oracle (literal port of the old JSON page) + whole-configuration sweep | DONE | `LegacyCatalogOracle.cs`, `CatalogParityScenario.cs` | **682/682 catalogs identical** (bilim 465, KAN 217) |
| Bug found: `FinalReleaseComObject` on a shared 1C identity (`Тип`) separated a cached RCW | FIXED | `CatalogSchema.cs`, D06 note | sweep |
| Bug found: value storage rendering `""` became null | FIXED | `LegacyValue.Read` | sweep |
| Bug found: "any reference" attributes joined every table — 25 s per 20-row page | FIXED | `MaxDerefTypes` | KAN sweep 92 s → 3.6 s |
| Per-object DISPID memo for row loops (also used by `ReadService`) | DONE | `OneC.Interop/DispatchMemo.cs` | ref value 24 → 2 µs |
| Session-thread exceptions keep their stack (`ExceptionDispatchInfo`) | DONE | `SessionWorker.Execute` | — |
| Measurements | DONE | P §5.2 | 1000-row page 10–27× faster than the old algorithm; 116 757 contracts walked in 34 s; no drift over 4 walks |
| Parity against the live old adapter | DONE (spot check) 2026-09-25 | — | see 5.1: found the empty-date / NULL rendering bug the oracle shared; 9 catalog pages identical after the fix |

Deliberate differences from the old code (each a bug there) are listed in D33.

## Milestone 3 — IPC — DONE (2026-09-23)

| block | status | file(s) | verified by |
|---|---|---|---|
| 3.1 Contract document | DONE | `IPC_CONTRACT.md` | — |
| 3.2 `OneC.Ipc` (envelope, errors, frames, pipe names; no COM) | DONE | `src/OneC.Ipc/` | 5 unit tests (round trip, truncated, oversize, envelope, names) |
| 3.3 Host pipe server: per-user ACL, first-instance, multiplexed, deadlines, cancel, Busy limit; stdin = config + lifetime | DONE | `src/OneC.Host/PipeServer.cs`, `Operations.cs`, `ServeMode.cs` | live tests; `verify` |
| 3.4 Supervisor pipe client (Transport errors, deadline + 5 s, cancel on caller abort) | DONE | `src/OneC.Supervisor/HostProcess.cs`, `Supervisor.cs` | live tests |
| 3.5 HTTP edge: loopback, `X-AIBA-Token`, contract status mapping | DONE | `src/OneC.Supervisor/EdgeServer.cs` | 12-case mapping unit test; 401/403/404/400/422 live |
| 3.6 End-to-end verify + measurements | DONE | `OneC.Supervisor verify` | 22/22 checks (2026-09-23) |
| Busy backpressure exercised live | DONE | `SupervisorOptions.HostGlobalMaxSessions` | 12 concurrent reads on a 1-session host: 4 served, rest Busy in < 100 ms, retryable |
| Cancel of an in-flight 1C call exercised live | DONE | — | register read cancelled at 1.5 s mid-row-loop → `Cancelled`, host keeps serving (3/3) |
| Registry-free connector calls (vtable Connect) | DONE (D29) | `src/OneC.Interop/ConnectorApi.cs` | 101/101 ×3 with the machine's comcntr registration absent |
| Timer callbacks can't kill a process; test threads capture exceptions | DONE | `SessionManager`, `Supervisor.Tick`, tests | the recorded "Test Run Aborted" was an unhandled test-thread exception (D27) |

Measured (P §3): warm read via edge p50 1.44 ms (file) / 2.10 ms (server); pipe only
0.72 / 1.83 ms. Supervisor + Kestrel edge 78 MB WS / 28 MB private. Killed host serving
again through HTTP after 3.4 s.

Tests: **101/101** live, 3 consecutive clean runs with the machine's comcntr registration
absent; 101/101 without 1C.

## Blocked / known limits

| item | status | note |
|---|---|---|
| Posting on the KAN server base | RESOLVED (1C side, 2026-07-16) | cause: a posting event subscription handled by a «Вызов сервера» module (fixed in the local KAN's configuration in July — see memory `project_kan_posting_blocker`); verified 2026-09-25: a ПТУ copy posted on KAN through the new writer. The writer now names this refusal `posting_blocked_by_configuration` when a base still has it (5.8). |
| x86 host | BLOCKED (no install) | only x64 1C installed on this machine |
| Reg-free activation with machine registration absent | DONE (D29) | full suite runs with the comcntr registration absent; connector calls go through the vtable, no typelib needed |
| `TYPE_E_CANTLOADLIBRARY` reproduction | NOT STARTED | `IsHostFatal` carried over from research |
| Test-host crash `0xC0000374` (heap corruption, ntdll) in one full-suite run | OPEN | 2026-09-24 21:42, during `RegisterLiveTests.CountRulesAndErrors`, right after `MultiBaseTests.ManagersCanBeRecreated…`; the next run was clean. First crash in ~12 full runs today. The old oscript adapter crashed with the same code at the same ntdll offset (`0x117eb5`) at 20:42 and 20:43 → inside 1C, not ours. A dump needs WER LocalDumps (machine-wide registry = stop condition). **Second occurrence 2026-09-25 15:51**, same code and same ntdll offset `0x117eb5`, during `ConnectionLifecycleLiveTests.FileBaseConnectWaitsForTheMachineWideLock` — like the first, a test that builds a second `SessionManager` in the test process and opens bilim (a production host has one manager). Not reproduced in 4 targeted re-runs of those tests (24 tests). **2026-10-01: 2 of 4 full runs (414 tests) and 2 of ~14 `MultiBaseTests`-only runs**, same code and offset. Rate grows with the suite (1 in ~12 runs at 252 tests). **Contained 2026-10-01 (D45):** the 6 tests that open a second SessionManager run in a child process each (`tests/OneCLiveChild`); a crash there fails that test with its exit code, the run continues. The crash itself is not fixed (inside 1C; not reproduced outside the test runner) |
| Sync on backend/1c v2: silent drop | OPEN (backend) | v2 counts a row as inserted before writing it and swallows a duplicate-key error, so the reported total always "matches" (`utils/entity_dedup.py`, pinned by `SyncPythonTargetTests`: 2 reported, 1 stored). Not solved client-side (a full read-back per batch would be a table scan in normal sync); live verification reads rows back instead (`sync-verify`) |
| Sync on backend/1c v2: register rebuild | OPEN (backend) | no per-table purge for a user token: a D-3 "copy this table again" on v2 is refused (it would leave old-keyed rows next to new ones); needs a backend purge route or the backend's fleet-resync runbook |
| Sync known gaps (2026-10-01 review, not fixed — no evidence they bite yet) | OPEN | sync does not yet yield to the user's own 1C work (`SyncLeases.Foreground` unwired); a verify pass over an uncopied table or after state loss queues every object; verify ignores a table's `From` date; a document's movements are held in memory while its recorder syncs; each activation re-reads the config; on a multi-org base an independent register's removed rows are deleted in every partition, so a partition holding none of them may ask for approval needlessly; a 403 for a company member (not owner) pauses the base although it can be transient on the backend; the mapper copies each row twice |
| Intermittent `0xC0000005` on a session thread | OPEN (mitigated, likely cause removed) | coreclr `0x2e4ff2` ×2 in the connector's IDispatch path, now bypassed (D29); test aborts were unhandled test-thread exceptions, fixed (D27); the "unexplained" `0x2703F9B7` is 1C's `rtrsrvc.dll` null read at exit (D37) |
| Processes stuck in exit, spinning a core in 1C's ImageMagick crash filter | FIXED (D37) | `OneC.Interop/NativeProcess.cs`, `ComActivator.Bind`, `ConnectorApi.Connect`, `Program.Main`, `HostProcess.StuckInExit`, `Supervisor.TickCore`, `tests/TestProcess.cs` | ten zombie test hosts found at 83 % machine CPU, one blocking bilim for ~45 min; `NativeProcessTests` (stuck-in-exit detected + killed; a native crash after reconnect ends the host; a host that loaded 1C exits 0 past an exit-time crash), `StoppingABusyHostLeavesNoProcessBehind`; `ShutdownLeavesNoOrphanHosts` now checks the process list, not `GetProcessById` (which already calls a stuck process "gone"). The monitor's `StuckInExit` branch is not exercised end to end (its detection and kill are) |
| Cold first write on the file base (46–157 s once) | OPEN — likely the same as the file-base stalls (5.7 row) | 2026-09-25: first Connects to bilim took 48 s (8.3.15), 12 s and 98 s (8.3.18), idle in `dbeng8.dll`, no other process with the file engine loaded; it failed one live test (65 s deadline) until bilim was warm |