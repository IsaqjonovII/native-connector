# MIGRATION_STATUS.md — what is actually done

Only things that were built **and** verified are DONE. Last verified: 2026-09-25.

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
| Posting | DONE on file base; BLOCKED on KAN | — | file-base post test asserts `Проведен = true` |
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
| 4.4 Visual check | DONE (awaiting the user's visual approval) | `tools/shoot.ps1`, `measurements/shots/` | fixed: centred table, frozen column widths, form wrap, query echo in errors, stale "engine not running" empty state, invisible caption buttons in forced dark |
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
| Test-host crash `0xC0000374` (heap corruption, ntdll) in one full-suite run | OPEN | 2026-09-24 21:42, during `RegisterLiveTests.CountRulesAndErrors`, right after `MultiBaseTests.ManagersCanBeRecreated…`; the next run was clean. First crash in ~12 full runs today. The old oscript adapter crashed with the same code at the same ntdll offset (`0x117eb5`) at 20:42 and 20:43 → inside 1C, not ours. A dump needs WER LocalDumps (machine-wide registry = stop condition). **Second occurrence 2026-09-25 15:51**, same code and same ntdll offset `0x117eb5`, during `ConnectionLifecycleLiveTests.FileBaseConnectWaitsForTheMachineWideLock` — like the first, a test that builds a second `SessionManager` in the test process and opens bilim (a production host has one manager). Not reproduced in 4 targeted re-runs of those tests (24 tests) |
| Intermittent `0xC0000005` on a session thread | OPEN (mitigated, likely cause removed) | coreclr `0x2e4ff2` ×2 in the connector's IDispatch path, now bypassed (D29); test aborts were unhandled test-thread exceptions, fixed (D27); the "unexplained" `0x2703F9B7` is 1C's `rtrsrvc.dll` null read at exit (D37) |
| Processes stuck in exit, spinning a core in 1C's ImageMagick crash filter | FIXED (D37) | `OneC.Interop/NativeProcess.cs`, `ComActivator.Bind`, `ConnectorApi.Connect`, `Program.Main`, `HostProcess.StuckInExit`, `Supervisor.TickCore`, `tests/TestProcess.cs` | ten zombie test hosts found at 83 % machine CPU, one blocking bilim for ~45 min; `NativeProcessTests` (stuck-in-exit detected + killed; a native crash after reconnect ends the host; a host that loaded 1C exits 0 past an exit-time crash), `StoppingABusyHostLeavesNoProcessBehind`; `ShutdownLeavesNoOrphanHosts` now checks the process list, not `GetProcessById` (which already calls a stuck process "gone"). The monitor's `StuckInExit` branch is not exercised end to end (its detection and kill are) |
| Cold first write on the file base (46–157 s once) | OPEN — likely the same as the file-base stalls (5.7 row) | 2026-09-25: first Connects to bilim took 48 s (8.3.15), 12 s and 98 s (8.3.18), idle in `dbeng8.dll`, no other process with the file engine loaded; it failed one live test (65 s deadline) until bilim was warm |