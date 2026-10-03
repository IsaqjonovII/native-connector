# AGENT.md — operating rules for the AIBA .NET / 1C rewrite

Read this first in every session. It overrides habit; `D:\aiba\CLAUDE.md` and the user's
global rules still apply on top.

## Start of every session

1. Read, in order: `AGENT.md` (this), `MIGRATION_STATUS.md`, `MIGRATION_PLAN.md`,
   `DECISIONS.md`.
2. `git -C D:\aiba\1c-arch status --short` and `git diff --stat`. Uncommitted work is
   normal here — the user commits. Do not assume a clean tree means nothing was done.
3. Build and run the tests before changing anything, so you know the starting point:
   ```
   dotnet test D:\aiba\1c-arch\tests\OneC.Tests -c Release
   $env:ONEC_TEST_BASES = "D:\aiba\1c-arch\bases.local.json"   # live 1C tests
   dotnet test D:\aiba\1c-arch\tests\OneC.Tests -c Release
   ```
4. Continue from the first milestone in `MIGRATION_PLAN.md` that is not DONE in
   `MIGRATION_STATUS.md`.

## After every milestone

- Update `MIGRATION_STATUS.md` (DONE / IN PROGRESS / NOT STARTED / BLOCKED — never mark
  future work done).
- Add measurements to `DOTNET_REWRITE_PERFORMANCE.md` if anything touched resources.
- Record any new architecture decision in `DECISIONS.md`, with the evidence.
- Move the next milestone to the top of `MIGRATION_PLAN.md`.
- Then continue to the next milestone without waiting to be told.

## Autonomy — keep working (user rule, 2026-09-24)

Loop without asking: read state → next unfinished approved work → implement → build → test
→ live-verify → measure → review own changes → fix → update state files → next block.
A finished class, a green test run or a crossed milestone boundary is **not** a reason to
stop or to report. Never ask "shall I continue?", "say go", "want me to do X?" — the answer
is yes. Failures (build, tests, crashes, bad numbers) are investigated to root cause, fixed,
re-verified and documented, not reported and left.

No permission needed for: new source files, refactors inside the architecture, tests, bug
fixes, non-destructive tests, read-only 1C queries, approved benchmarks, RAM/CPU measurement,
docs, moving to the next approved block, restarting own test processes, internal
implementation changes that don't alter a decision in `DECISIONS.md`. Reversible
engineering judgments: make them, document them.

Report to the user only when asked, at a real stop condition, or when all approved work in
`MIGRATION_PLAN.md` is done. Keep progress in the repo files, not in chat.

## Real stop conditions — the only reasons to stop and ask

1. **Destructive real data** — modifying/deleting real client or accounting data; any cleanup
   whose scope is not guaranteed test-owned; irreversible operations on production data.
2. **Architecture fork** — evidence contradicts a decision in `DECISIONS.md`, or two
   materially different choices would shape future design.
3. **Product/business decision** — behaviour not inferable from the existing Connector or
   specs; user-facing semantics.
4. **Access** — missing credentials, permissions, certificates, hardware, external access.
5. **Machine-wide / admin change** — registry, COM registration, drivers, machine config,
   UAC/admin operations not already approved.
6. **Source control / release** — commit, push, merge, tag, publish, installer, deploy.
7. **Real safety concern** — could damage client data, the environment or machine state.
8. **Plan exhausted** — every approved milestone is genuinely complete.

## Hard rules

- **Never commit or push.** Not even locally-safe-looking commits. The user says so for
  each specific action; approving one does not approve the next.
- **Never commit `bases.local.json`, `config.json`, `config.local.json`** or any file with
  1C credentials. `*.local.json` is gitignored; keep credentials in files matching it.
- **Never use `exchange=true`** on a 1C write.
- **Test documents** carry a marker comment starting `AIBA_REWRITE_` + a run-specific id.
  Update/delete code must refuse any document whose comment does not start with the marker.
  Clean up everything created, and verify the cleanup count.
- **Never bulk-delete by a broad prefix.** On 2026-09-23 a cleanup with prefix `AIBA_`
  deleted 64 real documents on kansler and 14 on bilim — every document the production AIBA
  pipelines had created, because the canonical marker `AIBA_<KIND>_<id>` also starts with
  `AIBA_` (list: `measurements/deleted-*-2026-09-23.txt`). Cleanup is only allowed with a
  prefix inside `AIBA_REWRITE_`; the `cleanup` CLI now refuses anything else. Before any
  delete that is not a document this run created by ref, list what would be deleted and
  ask the user.
- **Do not touch** the production Connector (`D:\aiba\connector`), `main.os`, or
  `aiba-cloud-os`. This repo is the rewrite; the old system stays as-is until a subsystem is
  explicitly cut over.
- Edit source with the Edit tool (Write for new files). No sed/python patch scripts on
  source.

## Architecture constraints — already proven, do not relitigate

Full evidence in `DECISIONS.md`. In short:

- One `OneC.Host` process per (1C platform version, bitness). One comcntr per process is a
  Windows loader limit, not a choice.
- Activate `comcntr.dll` from an explicit path via `ComActivator`. Never `GetTypeFromProgID`,
  never `regsvr32`, never read HKCR for the version.
- Call the connector itself (`Connect`, pool properties) only through `ConnectorApi`
  (vtable). Its IDispatch needs the registered type library, which other tools on the machine
  remove or flip (D29). The host must work with no comcntr registration at all — and does.
- **No C# `dynamic` in production code.** All 1C calls go through `OneC.Interop.Dispatch`.
  `dynamic` destroys the error text of every 1C runtime error. The only allowed use is the
  parity test in `tests/`.
- **Deterministic COM ownership.** Every COM object crossing the boundary goes into a
  `ComRef` / `ComScope`. Nothing is left to the GC finalizer — that crashes the process with
  `0xC0000005`.
- **All work for a session runs on that session's `SessionWorker` thread**, including
  Connect and release. Never pass a 1C COM object off that thread; convert to CLR values
  first (`OneCValue.Convert`).
- No DISPID caching across objects — DISPIDs are per 1C type and collide.
- Pools are lazy and bounded. K is a ceiling on useful concurrency, never a pre-create
  count. Registering a base must cost zero sessions.
- The connector's own `PoolCapacity` stays 0.
- One connector per process, from `ComActivator.SharedConnector()`, never released.
  Releasing the last one and creating another crashes comcntr (D20). Never call
  `CreateConnector` in production code.
- Budget eviction never takes the requesting base's own idle session (D22).
- **Never read a reference's fields over COM in a row loop** (`ref.Наименование`, `ref.Номер`,
  `ref.Код`, …). Each read loads the object into 1C's client cache and nothing frees it:
  +10 MB per 250 documents, unbounded (P §5.3). Dereference in the query, or collect the refs
  and resolve them per page (`RefBatch`). `LegacyValue.Slow` is the per-value algorithm for
  oracles and single values only.
- Never hold a 1C `Тип` or metadata object across a scope that may release the same identity
  — 1C returns them as shared COM identities, one RCW, and `FinalReleaseComObject` kills it
  for every holder (D06 note).
- A process that loaded 1C ends through `NativeProcess.Exit`, never a normal exit; every new
  Connect path calls `NativeProcess.RestoreCrashFilter()` (D37). "Process gone" means its
  handle is signalled — never `HasExited`, `GetProcessById` or `taskkill`, which all call a
  process stuck in its exit gone while it burns a core and holds the base's files.
- After any live test loop, check for leftover `testhost`/`OneC.Host` processes (process list,
  thread count, CPU). A loop that only watches `dotnet test` misses them.

## Code shape

- No giant files or services. The old adapter is one 22k-line file; do not rebuild that.
  Aim for files under ~400 lines and one responsibility each. If a file passes ~600 lines,
  split it before adding more.
- New capability = new focused type (e.g. `WriteService` beside `ReadService`), not new
  branches inside an existing god-class.
- `Program.cs` in `OneC.Host` is a measurement CLI. It is allowed to be long; production
  logic does not live there.
- Match the surrounding style: XML doc comments explain *why*, cite the evidence
  (research section or measurement) when a choice looks odd.

## Verification — nothing is "done" without it

- Run the thing. A claim of done/fixed/working needs the command and its output.
- Unit tests must pass without 1C. Live tests must pass with `ONEC_TEST_BASES` set.
- **Release gate** (D45): clean Release build, then the full suite with `AIBA_TEST_GATE=1`,
  `ONEC_TEST_BASES`, and the isolated local backend/1c up (`AIBA_SYNC_BACKEND`,
  `AIBA_SYNC_SECRETS`) — in gate mode a test whose environment is missing fails instead of
  returning early as "passed" (`Gate.Skip`, the fixture's `RequireForGate`). Two consecutive
  clean runs, then 0 leftover processes and 0 `AIBA_REWRITE_` documents.
- A live test that opens a SessionManager of its own, or writes 1C while OneC.Host processes read
  the same base, runs as a `tests/OneCLiveChild` scenario (`LiveChild.Run`, D45/D49): 1C's heap
  corruption in such processes (`0xC0000374`) must fail one test, not abort the run. A writing
  scenario tags its documents per run and its test calls `LiveChild.CleanupOwned` when it fails.
  Every other live test uses the fixture's manager, as production uses one.
- Every new 1C-facing path needs: a happy-path live test, an error-path live test, and a
  COM-leak check (`ComRef.Live` does not grow; `WrongThreadReleases` delta is 0).
- Live tests share process-wide counters and a background sweeper — assert deltas and end
  states, not absolutes.
- Parity: when a slice replaces old behaviour, prove the rows/results match the old path.
  An in-repo oracle that shares helpers with the code under test is not independent for
  those helpers (D33 correction: `LegacyValue.Scalar` hid two rules from 682 + 525 identical
  sweeps). When the old adapter is running (`GET http://127.0.0.1:55899/api/status`), also
  compare a few pages against it — read-only GETs, small limits, it is the user's live app.
- Leftover test documents: `OneC.Host cleanup --dry-run --doc all --base <b>` lists every
  `AIBA_REWRITE_` document of every type without touching anything.

## Resource measurement — first-class, not optional

Measure → identify → fix → re-measure. Never optimise from assumption.

For any change touching sessions, pools, or 1C calls, record in
`DOTNET_REWRITE_PERFORMANCE.md`:

- working set **and** private bytes (1C memory is native; the managed heap is ~0 and lies),
  managed heap, threads, handles, CPU, live ComRefs, session count;
- host only, then +K=1 / +K=2 / +K=4, server and file bases separately;
- idle warm pool, active workload, after workload, after idle sweep / recycle, after
  shutdown;
- repeated-operation drift (does WS/handles grow per op or per session cycle?).

Tools: `OneC.Host mem | bench | churn | poolram | lazy`. `ResourceSampler.Take` settles the
GC before reading.

Watch for: native memory huge while managed heap small; growth per operation; disposed
sessions not returning memory; pools sized by configured base count instead of active work;
a base holding K sessions forever; host processes multiplying.

Targets: 16 GB supported minimum, 32 GB recommended. 16 GB is not a licence to waste.

## Environment facts

- Repo: `D:\aiba\1c-arch` (private GitHub `IsaqjonovII/aiba-1c-arch`, branch `master`).
- Installed 1C: 8.3.15.1565 and 8.3.18.1289, both x64 only. Server agent is 8.3.15.
- Test bases: `kansler` (server, KAN on WIN-11-2070) and `bilim` (file, `D:\1C\bilim`).
  Credentials in `bases.local.json` (gitignored), sourced from
  `D:\aiba\research\1c-adapter\csharp-worker\*.json`.
- KAN posting over COM was fixed on the 1C side in July (MIGRATION_STATUS, known limits);
  some KAN types still refuse posting by their own rules (5.8).
- bilim (file base) stalls: first Connects can take 10–100 s, idle inside `dbeng8.dll`
  (cause unknown, MIGRATION_STATUS 5.7). Never kill a stalled 1C process. A live test that
  fails with a 65 s deadline on bilim is this, until shown otherwise — re-run it once bilim
  is warm before debugging code.
- Desktop app: its own infobase list is `%LOCALAPPDATA%\AIBA\Connector\bases.json`. Launching
  it for screenshots puts a real window on the user's screen; clicks can land in it.
