# DECISIONS.md — architecture decisions and their evidence

Each decision records what was decided, why, and where the proof lives. A decision here is
changed only when new evidence contradicts it — and then the user is asked first
(`AGENT.md`).

Sources: `C` = `DOTNET_ONEC_CONCURRENCY_RESEARCH.md`, `I` = `DOTNET_ONEC_INTEROP_RESEARCH.md`,
`P` = `DOTNET_REWRITE_PERFORMANCE.md`, `research/com-threading-probe/README.md` = `T`.

---

## D01 — WinUI 3 + .NET for the desktop rewrite

**Decision.** The next-generation connector is a native WinUI 3 / .NET desktop app talking to
1C from .NET directly. oscript is not carried forward.

**Why.** User decision (2026-09-21, "moving from research into the real WinUI + .NET
rewrite"). Supported by: oscript's COM wrapper releases RCWs on the finalizer thread
(`COMWrapperContext.Finalize`), the crash mechanism in D06 (T §4); the oscript adapter is
strictly serial with one connection per process; .NET gets real in-process concurrency
(C, T §1–2).

**Status.** Direction set. The WinUI shell exists only as a throwaway prototype
(`prototypes/winui-aiba-shell`); integration has not started.

## D02 — One OneC.Host process per (platform version, bitness)

**Decision.** Each host process binds exactly one `comcntr.dll`. Bases needing different
platform versions go to different processes.

**Evidence.** Loading a second comcntr into a process fails with win32 127
(`ERROR_PROC_NOT_FOUND`): the first load maps 69 unversioned sibling DLLs (`core83.dll`,
`backbas.dll`, …) and the second version's imports resolve against them (I §2). A loaded
comcntr cannot be unloaded, and `TYPE_E_CANTLOADLIBRARY` follows any attempt to tear it down
and recreate it (T §5). Bitness is a process property.

**Consequence.** `ComActivator.Bind` refuses a second path instead of half-loading it.
Option A of the interop brief (one host for all versions) is impossible.

## D03 — Minimise host processes: file bases join an existing host

**Decision.** `HostPlan` places server bases first (exact version), then puts each file base
on the oldest acceptable install that already has a host. A new process is created only when
no existing host can serve the base.

**Evidence.** Server bases need the exact server version; file bases open on any newer
platform (I §2: 8.3.18 client vs 8.3.15 server → "Different client and server versions";
8.3.18 opens the 8.3.15 file base). Each host costs ~35 MB before any session (P). With the
first implementation (newest install for file bases) the two test bases needed 2 processes;
after the fix, 1.

## D04 — Explicit comcntr.dll activation; no global regsvr32 switching

**Decision.** `ComActivator` loads comcntr from an explicit path with
`LoadLibraryEx(LOAD_WITH_ALTERED_SEARCH_PATH)` → `DllGetClassObject` →
`IClassFactory::CreateInstance`. The registry is never read or written for activation.

**Evidence.** With HKCR pointing at 8.3.15, a process ran 8.3.18 and 1C itself reported
`ВерсияПриложения = 8.3.18.1289` (I §2). The ProgID route silently ignored the requested
version in every case. No admin rights, no PATH change, no colocated files needed — tested
with `SetDllDirectory` disabled and a clean PATH. SxS manifests also work but require writing
into `Program Files\1cv8\<ver>\bin`, so they were rejected.

**Supersedes.** README finding 5 ("exactly one registry slot per machine") is true of the
registry but no longer a constraint for us.

**Correction (2026-09-23, see D29).** "The registry is never read" was true of activation
only. The connector's *IDispatch* loads its registered type library, so while `Connect` went
through `Dispatch`, the host still depended on the TypeLib registration — and failed with
`TYPE_E_LIBNOTREGISTERED` when it disappeared. D29 removes that dependency.

## D05 — Controlled IDispatch, no C# `dynamic`

**Decision.** All 1C calls go through `OneC.Interop.Dispatch` (hand-rolled `IDispatch` with
`PreserveSig`, reading `EXCEPINFO` directly). `dynamic` is banned from production code.

**Evidence.** The C# runtime binder throws `NullReferenceException` whenever
`EXCEPINFO.scode == 0 && wCode != 0` — which is every 1C runtime error (query, `Записать`,
`NewObject`). The same errors carry full text in `bstrDescription` (I §5). Even when it
survives, `dynamic` truncates connector errors ("User identification failed" vs "… Incorrect
user name or password"). The "textless 1C error" of the first report was this binder bug.
Typed interop is impossible: only `V83.ComConnector` has a type library; every object past
`Connect` reports `GetTypeInfoCount = 0` and `IProvideClassInfo` → `E_NOTIMPL` (I §3). The
wrapper was also 10–30% faster (I §4).

**Parity.** `LiveTests.DispatchPathReturnsTheSameRowsAsDynamic` proves identical rows.

## D06 — Deterministic COM ownership: ComRef / ComScope

**Decision.** Every COM object is owned by a `ComRef` and released with
`FinalReleaseComObject` on its creating thread, in reverse creation order (`ComScope`).
Wrong-thread release throws. Nothing depends on the GC finalizer.

**Evidence.** Leaving RCWs to the finalizer killed the process with `0xC0000005` / heap
corruption `0xC0000374` after ~40 queries; releasing deterministically ran clean and roughly
doubled throughput (T §4, C). The same fix withdrew the old "never use `sel.Get(0)`" landmine
— index access ran K=8 × 50 with zero errors once RCWs were released properly (I §7).

**Note (2026-09-24, milestone 5.2).** 1C hands out some objects as *shared identities* — `Тип`
values and metadata objects come back as the same COM pointer every time, and .NET maps one
pointer to one RCW. `FinalReleaseComObject` through one owner then separates the RCW for every
other holder (`InvalidComObjectException`, found by the catalog sweep: a cached
`Тип(УникальныйИдентификатор)` died when an attribute's type loop released the same `Тип`).
The rule stays; its corollary is now explicit: **never hold a `Тип` or metadata object across
an inner scope that may receive the same identity** (`CatalogSchemas.Shape` checks types
inside the per-type scope and skips tracking a metadata object that is the caller's own).
Switching `ComRef` to `ReleaseComObject` (one decrement per owner) would make aliasing safe by
construction; not done — it changes this decision and every release path, so it waits for a
reason beyond this one case.

## D07 — Thread-affine sessions: SessionWorker

**Decision.** Each 1C session lives on one dedicated MTA thread. `Connect`, every call, and
the final release run on that thread; callers marshal work onto it and get CLR values back.
No 1C COM object leaves the session thread.

**Why.** It makes D06's "release on the creating thread" structurally guaranteed instead of a
convention. `ThreadingModel=Both` means no COM marshalling cost (T §1); the one extra thread
per session was measured: threads return to baseline after drain and stay flat across churn
cycles (P).

**Follow-up fix.** A cancelled `Execute` marks the session broken, because its work item may
still run and would otherwise fire under the next renter.

## D08 — Lazy, bounded pools

**Decision.** `BasePool` creates nothing until first `Rent`. Size is capped at
min(`PerBaseMaxSessions`, base `MaxConcurrency`). Idle sessions past `IdleTimeout` (default
5 min) are retired; `PerBaseMinWarm` defaults to 0. K is a ceiling on useful concurrency,
never a pre-create count.

**Evidence.** Warm reuse is dramatically faster than connect-per-op (C, session pool). 42
registered bases cost 35 MB and zero sessions; using one base created exactly one pool and
one session (P, `lazy`). Idle sweep returned a K=4 server pool from 313 MB to 101 MB and a
K=4 file pool from 931 MB to 92 MB (P).

**Follow-up fix.** Disposing a session joins its thread, so it happens outside the pool lock.

## D09 — Connector PoolCapacity stays 0

**Decision.** Do not enable `V83.ComConnector.PoolCapacity` / `PoolTimeout`.
`PoolOptions.ConnectorPoolCapacity` exists but defaults to 0.

**Evidence.** Defaults are 0 and writable. With `PoolCapacity=4`, reconnect drops from
~1 s (server) / ~1.7 s (file) to 0 ms — but releasing four server sessions left the process
at 284 MB instead of dropping to 73 MB (P). It duplicates our warm pool and removes the
ability to give memory back.

## D10 — Global count budget + working-set budget

**Decision.** `SessionManager` enforces two ceilings per host: `GlobalMaxSessions` (default 8)
and `MaxWorkingSetMb` (default 1200). When either would be crossed it evicts the least
recently used idle session anywhere in the host; if none can be evicted, the renter waits
(up to `RentTimeout`) instead of failing. The first session for a base is always allowed.

**Evidence.** A server session costs ~50 MB and a file session ~210 MB (P), so a count-only
budget of 8 spans 0.4–1.7 GB. With `--maxws 400`, four threads on a file base shared one
session and completed 160 reads with zero failures at 308 MB (P).

## D11 — File-base default concurrency 2

**Decision.** `OneCBase.MaxConcurrency` is 2 for file bases, 4 for server bases.
`MaxConcurrencyOverride` raises it per base.

**Evidence.** File reads scaled to K=4 on throughput but are RAM-bound (C §B3); measured cost
is ~210 MB per file session, so K=4 is 0.9 GB for one base (P). File writes already should
stay at K=2 (C §B2).

## D12 — Per-operation concurrency limits for writes

**Decision.** `WriteGates`, per (base, operation): server create 4, file create 2, update 2,
mark-for-deletion 2, post 1, direct delete 1. Gates are taken before renting a session, so a
queued write never holds a session.

**Evidence.** C, revised recommendations: DELETE ran at 0.7–2.5 op/s with 9.1 s max,
~10× worse than create; mixed-type posting at K=4 failed 6/20 with p95 22 s.

**Status.** Implemented in 2.1 (`src/OneC.Host/WriteGates.cs`), unit-tested.

## D18 — The host only modifies documents it created

**Decision.** `WriteService` stamps every document it creates with a Комментарий starting
with its prefix (`AIBA_…`, canonical `AIBA_<KIND>_<id>`), and update / post /
mark-for-deletion / delete refuse any document whose comment does not start with that
prefix. The check runs on the session thread against the live object, before any write.

**Why.** The connector must never rewrite an accountant's own documents. This was a test
safety rule in research; it is a production rule now.

**Evidence.** `WriteLiveTests.RefusesToTouchADocumentAibaDidNotCreate` — a real KAN document
is refused for update, post, delete and mark with a Host-layer error.

## D19 — Writes clone an existing posted document

**Decision.** `CreateByClone` finds the newest posted document of the type, `Скопировать()`s
it, sets Дата / Комментарий / scalar fields, and writes. Only CLR scalars can be set; setting
references (Контрагент, Склад…) waits for a lookup layer in subsystem migration.

**Why.** It produces a valid document in any configuration without re-implementing every
mandatory attribute. Proven in research and again in 2.1 on both bases.

**Limit.** Not a production document-construction strategy on its own — real writes need
field mapping from the cloud payload. That belongs to migration block 5.7.

## D13 — No DISPID caching

**Decision.** `Dispatch` resolves the DISPID per call with `GetIDsOfNames`. There is no
cache.

**Evidence.** `probe-dispid`: the same name maps to different DISPIDs on different 1C types
(`Вставить` → 3 on Массив, 2 on Структура, 4 on ТаблицаЗначений). 1C objects carry no type
information to key a cache on. A process-wide cache would silently call the wrong member.
Russian and English names do resolve to the same DISPID on the same object (I §3), so name
language is not an issue.

## D14 — Structured errors: OneCException with a layer taken from bstrSource

**Decision.** One exception type for the 1C boundary. `Layer` comes from
`EXCEPINFO.bstrSource`: `V83.COMConnector.*` → Connector, `1C:Enterprise *` → Runtime, no
EXCEPINFO → Dispatch, our side → Host. `IsRetryable` from HRESULT first; message text only
for lock/timeout wording, contained in `Retry`. `IsSessionFatal` / `IsHostFatal` from
HRESULT.

**Evidence.** Every field was shown populatable (I §6). Runtime errors carry `wCode=1001`,
`scode=0`; connector errors `wCode=0`, `scode=E_FAIL`. Live tests assert both shapes.

**Known limit.** 1C gives only the top line for posting failures (`Failed to post: "…"!`);
`IsHostFatal` for `TYPE_E_CANTLOADLIBRARY` is carried over from research, not reproduced.

## D15 — Reads are parameterised and identifier-whitelisted

**Decision.** `ReadService` puts table and field names into query text only after
`ValidateIdentifier` (letters, digits, `.`, `_`); dates go in as query parameters
(`&From`, `&To`), never as literals. Transient COM values are released per row.

**Why.** Query text is 1C's injection surface; per-row scopes keep a large read from holding
thousands of live RCWs.

## D16 — Resource targets

**Decision.** 16 GB RAM is the supported minimum, 32 GB recommended for heavier clients. The
host budget defaults (D10) are sized for 16 GB with room for 1C, the UI and Windows.

**Why.** User requirement (2026-09-21). "16 GB does not mean AIBA may waste memory."

## D20 — One process-lifetime connector, never released

**Decision.** `ComActivator.SharedConnector()` creates the process's `V83.ComConnector` once
and never releases it. `SessionManager` uses it and does not dispose it. Managers can be
created and disposed freely; sessions are still released normally.

**Evidence.** Milestone 2.2: after one manager disposed its connector, a second manager's
first query died with `0xC0000005` in `IDispatch::Invoke`; the same phase in a fresh process
ran clean; with the shared connector the full four-phase run completed in one process. Same
family as research's `TYPE_E_CANTLOADLIBRARY` teardown landmine (T §5). The connector alone
costs ~5 MB.

**Consequence.** Replaces D06's "connector released on its creating thread" for the
connector only. A host that needs a clean comcntr must restart the process.

## D21 — Budget admission is serialised and reserves memory until connected

**Decision.** `TryTakeBudget` runs under one admission lock and adds the new session's
`EstimatedSessionMb` to a pending reservation, released when `Connect` finishes (success or
failure). The memory check is `working set + pending + estimate`.

**Evidence.** Before: concurrent renters each read the working set before any new session
had grown it, and a 600 MB ceiling let the host reach 753 MB with zero evictions. After:
peak 396 MB under the same scenario (P §2.2 C).

## D22 — Eviction never takes the requesting base's own idle session

**Decision.** Under budget pressure, `SessionManager` evicts the least recently used idle
session from any *other* base. The requesting base waits for its own idle session instead.

**Evidence.** Before: the file base evicted its own idle session to open a second one —
net zero sessions plus a ~2 s reconnect. After: the file base's two readers shared one
session (`Created = 1`, `Evicted = 0`). `MultiBaseTests.PressureEvictsTheQuietBaseNeverTheRequester`.

## D23 — Reads and writes share one pool per base (no reserved read lane, for now)

**Decision.** Keep one pool per base for reads and writes. Revisit when IPC defines
per-request latency targets.

**Evidence.** Four server writers at the pool cap: reader p50 1 ms, p95 2 ms, max 835 ms,
vs max 11 ms alone (P §2.2 D). Research D found shared vs split lanes a wash except read p95.
The tail is real but sub-second; a reserved read slot would cut it at the cost of one more
session per active base. That trade is for the IPC milestone, where request SLAs are set.

## D25 — Supervisor: separate process, never loads comcntr, owns host lifecycle

**Decision.** `OneC.Supervisor` turns `HostPlan` into one `OneC.Host serve` child per host
key. It references `OneC.Interop` only for `PlatformCatalog` (disk reads) and never calls
`ComActivator`. Each host gets its bases as the first stdin line — credentials never touch
disk — and exits when its stdin closes, so hosts cannot outlive the supervisor. The monitor:
restarts a dead host with exponential backoff (1 s … 60 s cap) and recycles a host that has
**zero sessions** and a working set above `RecycleIdleAboveMb` (default 400 MB). A host
holding warm sessions is never recycled.

**Evidence** (P §2.4). Supervisor 35 MB with 0 comcntr mapped; two versions from one plan,
each confirmed by 1C; killed host back in 2.6 s; recycle 145 → 34 MB; 0 orphans after
dispose. `SupervisorLiveTests` (5 tests).

**Why zero sessions, not "none in use".** An earlier version recycled any host with nothing
in flight, which would kill an active base's warm sessions every monitor pass — the churn
D24 exists to avoid. The retained residue D24 targets only matters once a host's bases have
all gone quiet.

**Not decided here.** The stdin/stdout line-JSON channel is a diagnostic stand-in; the real
host ↔ supervisor ↔ UI transport is milestone 3.

## D26 — File-base "sharing violation" on Connect is retryable

**Decision.** `Retry.IsRetryable` treats a Connector-layer "sharing violation" (and its
Russian form) as retryable; `BasePool` retries a retryable Connect up to 3 attempts with
250 ms × attempt backoff. Every other connector error still fails on the first attempt.

**Evidence.** Concurrent Connects to `bilim` hit `Database sharing violation
'D:\1C\bilim/1Cv8tmp.1CD'` 2 of 240 times; retrying the same Connect succeeds (P §2.4).

## D27 — Crashes: isolation first; what the evidence now says

**Decision.** Treat a host crash as an expected event: process isolation (D02) plus
supervisor restart (D25). No speculative locking.

**Evidence, updated 2026-09-23 from the Windows Application log** (events 1000/1026):
- The "Test Run Aborted" runs were **not** native crashes. The one on record is
  `e0434352` — an unhandled .NET exception (`OneCException 0x8002801D`) on a raw test thread
  in `PoolStopsAtThePerBaseCeiling`, which terminates the process. Test threads now capture
  exceptions; production timer callbacks (sweeper, supervisor monitor) now catch and log.
- The native crashes are `OneC.Host.exe` `c0000005` in **coreclr.dll at the same offset
  `0x2e4ff2`**, twice, both with `IDispatchRaw.Invoke` on the *connector's* IDispatch from
  `SessionWorker.Pump` — the IDispatch that depends on the registered type library, while
  something on this machine was changing that registration. D29 took that call path out.
- A third native crash (11:04, address `0x2703F9B7`, not coreclr) is unexplained.
  *(2026-09-24: explained — a null read in 1C's `rtrsrvc.dll` during process exit, also hit
  by the old oscript adapter; D37.)*
- 0 crashes in 690 targeted Connect/release overlap cycles (before D29).

**Status.** Plausible but unproven that D29 removed the main crash path. The original crash
condition was re-run after D29 with no crash (P §3, "Churn re-run after D29"). Still open: one
`0xC0000374` heap corruption inside 1C's ntdll path, seen once in the suite and also in the old
oscript adapter (MIGRATION_STATUS, known limits).

**Update 2026-09-24 — a crash path found and closed.** The suite aborted inside
`HostSurvivesABrokenBaseAndKeepsServingOthers` (a failing Connect to a missing file base) with
no Windows crash record. Reproduced with `OneC.Host badconnect --churn`: failing Connects on
one thread while other threads open and release good sessions → native crash (exit 139, stack
ending in `ConnectorApi.Connect`) in 1 of 3 runs; a failing Connect alone never crashed (900
tries). D29's first vtable declaration used `out object` with PreserveSig, so .NET marshalled
the result slot even when Connect failed. Declared as a zero-initialised `ref IntPtr`,
converted only on success: **0 crashes in 5 × 200 failing Connects with churn**. Same run
found a second transient file-base wording, "Error creating database file '…/1Cv8tmp.1CD'",
now retryable (D26).

## D28 — IPC: named pipes inside, HTTP at the edge

**Decision.** User decision (2026-09-23), from measured options. Supervisor ↔ each OneC.Host
over a Windows named pipe; one HTTP endpoint, on the supervisor only, for the WinUI app,
cloud relay and debugging tools. Hosts never open a network port.

**Evidence** (`research/ipc-transport/README.md`). Per process: pipe +5 MB idle / ~65 MB
loaded, 32 µs small round trip; Kestrel HTTP +16 MB idle / ~135 MB loaded, 213 µs; gRPC
+17 MB / ~165 MB, 314 µs. All far below a warm 1C read (~1 ms). Hosts multiply by 1C version,
the supervisor does not — so the heavier stack is paid once.

## D29 — Call the connector through its vtable; the host needs no registry at all

**Decision.** `ConnectorApi` calls `IV8COMConnector::Connect` (IID `ba4e52bd-…`, vtable slot
7) and `IV8COMConnector2` pool properties through the vtable, and reads failure text from
`IErrorInfo`. `Dispatch` is still used for everything past `Connect` (those objects have no
type library and implement IDispatch themselves).

**Evidence.** On 2026-09-23 the machine's whole comcntr registration disappeared mid-run
(CLSID, ProgID, TypeLib — not done by this rewrite; nothing here calls regsvr32). With it
gone: explicit-path activation still created the connector, but `GetIDsOfNames("Connect")`
failed `0x8002801D` TYPE_E_LIBNOTREGISTERED. Via the vtable, with the registration still
absent: 8.3.15 → kansler and 8.3.18 → bilim both connect and read; bad-credential errors keep
full text ("User identification failed Incorrect user name or password", Connector layer,
scode E_FAIL); the full suite passed 101/101 three times in a row. Layouts were read from the
typelib embedded in comcntr.dll with `LoadTypeLibEx(REGKIND_NONE)` (research/typelib-probe).

**Consequence.** A machine where some other tool unregisters or flips comcntr no longer
affects the host. This also answers the parked "reg-free with registration absent" question.

## D30 — Reads render references inside the query (ПРЕДСТАВЛЕНИЕ), GUIDs on request

**Decision.** `ReadService` selects every field twice — `f КАК v{i}, ПРЕДСТАВЛЕНИЕ(f) КАК t{i}`
— and uses the text column only when the value turns out to be a reference. `refs=guid`
skips the text columns and returns `XMLСтрока()` GUIDs; `refs=both` returns
`{ref, text}`. `OneCValue` never stringifies a reference client-side.

**Evidence** (milestone 4.0, `OneC.Host refbench`). The old path called the global `Строка()`
on the connection for every reference — that member **does not exist** there
(`DISP_E_UNKNOWNNAME`), so every call failed and fell back to `Номер`/`Наименование`: a
register's `Регистратор` came back as `00000000106` instead of `Отчет о розничных продажах
00000000106 dated 08.02.17 18:00:06`. Per row, 5 000 KAN rows: old 938–1 921 µs, query
ПРЕДСТАВЛЕНИЕ 90–322 µs, XMLСтрока GUID 22–27 µs, bare RCW floor 7–14 µs. A 100 000-row
register read went from 87.7 s to 31.0 s with correct text. `ПРЕДСТАВЛЕНИЕ()` is valid on
unlimited-length strings (unlike `МАКСИМУМ`). Guarded by
`LiveTests.ReferenceColumnsComeBackAsPresentationGuidOrBoth`.

**Note.** Presentation text is in the session's language — English on these bases
("dated"), because that is how the connection's interface language resolves. Russian
presentations need the session language set; not done yet.

## D31 — Desktop app: talks only to the edge; owns the base list; restarts the stack on change

**Decision** (engineering defaults, reversible). `src/OneC.Desktop` (WinUI 3, unpackaged,
self-contained x64) never loads 1C. It keeps the infobase list in
`%LOCALAPPDATA%\AIBA\Connector\bases.json` with passwords protected by DPAPI for the current
Windows user, starts `OneC.Supervisor run --bases-stdin --port 0` and hands it the bases (with
plain passwords) over stdin only, then reads port + token from the ready line. Any edit to
the list restarts the supervisor — simplest correct behaviour until 5.1 designs live
reconfiguration. All tables use one code-built control so header and rows cannot misalign.
UI language is English for now (product decision pending: RU/UZ).

## D32 — Connection lifecycle follows the old adapter's proven rules (milestone 5.1)

**Decision.** Ported from `connector/.../main.os`, behaviour and constants unchanged:
1. **File-base pre-check** (`:1935`): `1Cv8.1CD` must exist before COM is called; missing →
   Host error, category `path`, answered in milliseconds.
2. **Machine-wide file-connect queue** (`:2098`): the *same* lock file
   `%TEMP%\aiba-1c-connect.lock`, exclusive open, 90 s max wait then connect anyway — so old
   and new engines on one machine queue behind each other. Server bases never wait.
3. **Error categories** (`:1951`): `auth/license/path/version/permission/network/unknown`,
   RU + EN phrases, same order — on connection-layer errors only (the "not found → network"
   rule would mislabel query errors).
4. **Circuit breaker** (`:2065`): 3 failed Connects in a row → fail fast for 60 s → one trial.
   Fast failures keep the real category and are retryable (HTTP 503).
5. **Connection test with a metadata probe** (`:1998`): new `test` op / `GET /v1/bases/{b}/test`
   returns configuration name, synonym, version, platform version.
6. **Liveness** (`:882`, `:2782`): the old adapter probed on every request; the pool probes an
   idle session only if it sat idle longer than `ValidateIdleAfter` (10 s) and replaces a dead one.
7. **Old config import**: `%APPDATA%\aiba.uz\1c-adapter\config.json` → the app's DPAPI store,
   read-only, HTTP/cloud/OData skipped (COM only), same server/file mapping rule.

**Why the file queue matters here too.** This rewrite opened file sessions concurrently and
got the "…/1Cv8tmp.1CD" sharing/creation errors (D26) — the contention the old adapter had
already measured (~64 s vs ~1.7 s per connect) and fixed with this queue.

**Not ported.** HTTP/OData base types; the per-request probe (replaced by idle-age probing);
the old LRU session cap (replaced by the budget, D10).

## D33 — Sync reads keep the old adapter's row shape and value rules; the query does the work (milestones 5.2, 5.3)

**Decision.** Catalog reads (`catalog` op, `GET /v1/bases/{b}/catalogs/{name}[/{id}]`) return
rows in the old adapter's shape — `id, code, name, deletionMark, parent, isFolder, Владелец,
<attributes>, orgRef, tabularSections`, same key order — because the cloud backend and the
connector's projection hashes consume exactly that. Values follow the old
`ПреобразоватьЗначение` (main.os:2871): a reference becomes null when empty, else its
Наименование, trimmed Код, Номер, GUID, XML form; dates `yyyy-MM-ddTHH:mm:ss`, the empty date
`""`. This is a *different* consumer from D30: browse keeps ПРЕДСТАВЛЕНИЕ; sync keeps the old text
so the backend's stored rows do not all change on cut-over.

**How.** The old code paid one COM call per step per value (and a server round trip for each
dereference). Here the query selects `f`, `f.Наименование`, `f.Код`, `f.Номер` — only the ones
some type in the attribute's type set has, read from metadata once per base and catalog
(`CatalogSchemas`, 10 min TTL). A NULL dereference means empty, broken or another type; only
then is the raw value read and turned into a GUID with one `XMLСтрока` call. Three refinements,
each measured (P §5.2):
- single-type references carry the empty test in the first deref column
  (`ВЫБОР КОГДА f = ЗНАЧЕНИЕ(<type>.ПустаяСсылка) ТОГДА ЛОЖЬ ИНАЧЕ f.Наименование КОНЕЦ`);
- single-enum attributes select `f.Порядок` and take the value name from metadata;
- the row loop resolves column DISPIDs once per `Выборка` (`DispatchMemo` — per object, so it
  does not break D-rule "no DISPID cache across objects").
Per value: ~2 µs instead of ~24 µs. Attributes whose type set includes
`УникальныйИдентификатор` (a zero GUID is a value there) or more than 4 reference types (the
deref joins every table: 25 s for 20 rows) are rendered per value — and since 5.3 not by
reading the reference's fields over COM (that leaks: 1C caches every object it loads, P §5.3)
but by `RefBatch`: the page's references grouped by XML type, one `ГДЕ Ссылка В (&refs)` query
per type.

**Parity proof.** `LegacyCatalogOracle` is a literal port of the old JSON page (names selected
as-is, values read by column index and rendered step by step, tabular sections through
`ВЫБРАТЬ *`). Metadata walk shared with documents: `MetadataShapes`, cache `SchemaCache`.
`CatalogLiveTests.RowsMatchTheOldAdapterAlgorithm` and `OneC.Host catparity` (every
catalog of both configurations) compare the two byte for byte, key order included: **682 of
682 catalogs identical** (bilim 465, KAN 217; 2 555 rows, 880 tabular lines). The sweep found
two real bugs on the way (value storage rendering "" vs null; the D06 shared-identity crash).
The old adapter itself cannot run on this machine (its ProgID registration is gone — stop
condition), so the oracle stands in.

**Deliberate differences** from the old code, each a bug there:
1. Errors are errors. The old code caught everything and returned an empty `data` with a
   `total_count` — the "0 rows at total 550" class. A bad filter is a 422, an unknown catalog
   a 404, a bad cursor a 400; a tabular-section failure fails the page instead of silently
   dropping the sections.
2. Filter keys are validated identifiers bound to positional parameters (`&f0`); the old code
   pasted keys into the query text. Control words are matched exactly, not by substring.
3. Count-only (`limit=0`) with filters counts the filtered set; the old code ignored the filters.
4. Offset pages order by `Наименование, Ссылка` (stable) instead of `Наименование` alone.
5. By-id returns tabular sections, i.e. the same row the list returns. The old by-id row was
   narrower, and it replaces the stored row in the cloud.
6. `fields=-` (an empty projection) means base fields only, as the old route's own comment
   says it should (main.os:21775); the old list code actually read every attribute for it.

**Documents (milestone 5.3)** follow the same rules — `DocumentReadService`, row
`id, number, date, posted, deletionMark, <attributes>, orgRef, tabularSections` — and keep the
old route's two shapes on purpose: a list row carries only tabular sections that have rows; a
by-id / batch row carries every section, empty ones as `[]` (main.os:8413), because the
connector fingerprints each route's rows as they are. Kept as well: the date cursor
(`next_cursor_date` / `next_cursor_skip`), the count of the `[from, to)` window only, the
contract filters applied in memory, the timesheet day rule, the bank-document name alias.
Parity: `docparity`, **525 of 525 document types identical** (bilim 325, KAN 200; KAN alone
3 969 rows and 93 176 tabular lines through the batch route).

A list is read in two queries: a plain one picks the page's refs in order, a second renders
only those. In one query the rendered columns' LEFT JOINs made PostgreSQL sort the whole table
instead of walking the `Дата` index — 4.6 s → 0.7 s for a 20-row page of KAN sales.

Further deliberate differences for documents:
7. The old code's `ВЫБРАТЬ *` on tabular sections fails with «Ambiguous field …Ссылка» for
   АктСверкиВзаиморасчетов.ПоДаннымОрганизации and ВводНачальныхОстатков.РасчетыСКонтрагентами;
   it swallowed that and shipped those documents without the sections. Explicit columns work.
8. A date filter, `cursorDate`, `from` or `to` that does not parse is a 400. The old code
   filtered a bad `date` on TODAY and silently dropped a bad cursor or window bound.
9. By-id and batch accept any GUID case; the old code compared text and missed uppercase ids.

**Correction 2026-09-25 — found against the live old adapter.** Once the machine's comcntr
registration was back, the running old adapter (router :55899, same `main.os` stamp as the
`connector` release) was compared read-only with the new edge on KAN: 4 of 13 pages identical.
Two value rules were wrong in `LegacyValue.Scalar`, and the oracle sweeps above could not see
it because the oracle renders scalars through the same function:
- 1C's empty date arrives over COM as 0100-01-01 (the OLE date floor), not `DateTime.MinValue`;
  it was sent as `"0100-01-01T00:00:00"`, the old adapter sends `""`.
- 1C NULL (an attribute that does not apply to the row, e.g. on a catalog group) is `""` in the
  old adapter — its value chain ends in `XMLСтрока(Null)` (main.os:2953) — not `null`.
  Неопределено (VT_EMPTY) is still `null`, empty references are still `null`.
After the fix: 19 of 19 pages identical (catalogs, document lists, a document by id with its
tabular sections, information / accumulation / accounting register pages). Lesson: an oracle
that shares a helper with the code under test is not independent for that helper — keep one
live comparison against the real old adapter in the verification set whenever it can run.

## D34 — Register reads: the old rows and the old accounting window, stable paging everywhere (milestone 5.4)

**Decision.** `RegisterReadService` (`register` op, `GET /v1/bases/{b}/registers[/{kind}]/{name}`)
returns the old rows: every column of `ВЫБРАТЬ *` in its order, values by `LegacyValue`
(D33); accounting rows add `СчетДтКод`/`СчетКтКод` (query-side `.Код`), `recorderRef`,
`lineNo`, `orgRef` right after their columns, as before. The accounting read keeps the old
measured design unchanged: `.ДвиженияССубконто` with the period as a virtual-table parameter,
and a window sized by row count from the base table (main.os:18357; 592 s → 3.4 s on KAN).
Columns and their types come from the result of `ВЫБРАТЬ ПЕРВЫЕ 0 *` (cached per base).
Wide composite columns (Регистратор, Субконто) go through `RefBatch`.

**Found on the way.** The column probe on `.ДвиженияССубконто` *without* period parameters ran
for over 10 minutes on KAN: the virtual table builds the subconto join over all 3 M rows
before anything else. Probes use an empty window (`ДАТАВРЕМЯ(3999,12,31)` both ends).

**Deliberate differences** (each fixes an old defect):
1. Information and accumulation registers order by `Период, Регистратор, НомерСтроки` when
   they have a recorder. The old order was `Период` alone, so rows of one second had no
   defined order and page boundaries could lose or repeat rows (memory: "the durable fix is a
   tie-break … still not done").
2. Every register kind returns `nextCursorDate` / `nextCursorSkip` / `hasMore`; the old code
   returned them for accounting only, and the connector derived them itself for the rest.
3. `skipTotal` is honoured for every kind; the old information/accumulation reads counted the
   whole register on every page.
4. Errors are errors. The old reads put `{ "error": … }` into `data` as a row, with HTTP 200.
5. Accounting registers without the virtual table read the base table with the plain offset;
   the old fallback rungs mixed the window's intra-period skip with a WHERE that had no window.
6. `to` is exclusive for accounting reads too: the virtual table's upper bound is `to − 1 s`
   (inclusive bounds, whole seconds). The old code passed `to`, so rows of that exact second
   belonged to two neighbouring cold-read slices.

**Parity.** `regparity` / `RegisterLiveTests.RowsMatchTheOldAdapterAlgorithm`: accounting pages
identical row by row (same order); information/accumulation pages identical as sets, minus the
last `Период` of a full page (where the old order was undefined).

## D35 — Cold reads: balanced period slices on parallel sessions; no XDTO bulk path (milestone 5.5)

**Decision.** A cold read (first sync of a big register or document type) is split by
`SlicePlanner` into K period slices of about equal row count (base table grouped by day) and
the slices are walked concurrently, one pool session each, with the normal cursor reads and
an exclusive upper bound (`to`). Server bases only; file bases stay sequential (15× worse in
parallel, earlier research). The old adapter's bulk path (`Выгрузить` + `СериализаторXDTO`,
one COM crossing per page, `bulk=1`) is **not** ported.

**Evidence** (P §5.5, KAN `Хозрасчетный`, June 2025, 26 504 movements):
- K = 1 / 2 / 4 sessions: 593 / 1 072 / 1 737 rows/s (1.8× / 2.9×), the same 26 504 rows
  every time, slices balanced (6 868 / 6 812 / 6 558 / 6 266); +~80 MB private per session.
- Warm 1 000-row accounting page: `Выполнить` ~350 ms; this host's per-cell walk (with
  `RefBatch`) +~390 ms; `Выгрузить` + XDTO + parsing the XML in C# +~330 ms — before
  rendering references, which the XML carries only as type + GUID. ≤ 7% and 4 MB of XML per
  page. The old path won because oscript's per-cell work cost ~3.5 ms/row; here it is 0.39.

**Compatibility.** A client asking the old way (`bulk=1`) gets JSON rows; the old contract
already fell back to JSON whenever the bulk path failed (main.os:22205).

## D36 — Change feed: the supervisor reads the event-log files; the caller owns the cursor (milestone 5.6)

**Decision.** `OneC.EventLog` (no COM) reads a base's `1Cv8Log` — `1Cv8.lgf` dictionary +
`*.lgp` records, the connector's verified format (Rust `eventlog.rs`) — and the supervisor
serves it at `GET /v1/bases/{b}/changes?cursor=`, without a host or a 1C session. Pull, not
push: each call takes the last cursor (`lgfGuid|file|offset`) and returns the data changes
after it (`New/Update/Post/Unpost/Delete`, metadata, object GUID), the next cursor, `more`,
and `reset` when the log was recreated/truncated/rotated past the cursor (the caller must
resync). A first call starts at the tail. Reads are capped (8 MB). Server bases resolve through
the running `ragent` service and `1CV8Clst.lst` on this machine only (ported with the
connector's fail-closed host check). The connector's watcher/debounce/ack machinery is not
ported: its push-with-ack design lost events whenever an emit had no listener; a pull cursor
held by the caller cannot.

**Found: transaction status.** A data record's own letter is `U` for committed and
rolled-back writes alike. The outcome is on the transaction's marker record: its
`_$Transaction$_.Begin` carries `R` when rolled back, a `_$Transaction$_.Commit` record carries
`C`. The feed drops data records whose transaction ID was marked `R` earlier in the same read
(the marker precedes the data); a rollback split across two reads yields a harmless extra
change, never a missed one. The connector skipped data records lettered `R`, which never
occurred for a real rolled-back write here (bilim, 2026-09-24).

**Oracle.** 1C's own `ВыгрузитьЖурналРегистрации` over the same window (`OneC.Host logparity`,
`ChangeFeedLiveTests`): bilim 97/97 and KAN 32–36/32–36 data events identical, including a
test-owned document written, posted and deleted in the window (its GUID comes out New → Post →
Delete), and a write cancelled inside a transaction (1C: RolledBack; feed: nothing).

## D37 — A process that loaded 1C never exits normally; native crashes must be real crashes

**Found (2026-09-24).** Ten test hosts were left running after their runs, each burning one
core (machine at 83 % CPU) and one holding the file base bilim so that every new connection
to it hung for ~45 minutes. Each had one thread left, looping in 1C 8.3.15's bundled
ImageMagick 6.9.3 (`CORE_RL_magick_.dll`). Disassembly: `NTWindowsGenesis` does
`prev = SetUnhandledExceptionFilter(NTUncaughtException)`, and `NTUncaughtException` ends with
a tail jump to `prev`. 1C runs that genesis again when a base is opened after its last session
closed (idle timeout, then a new request; tests' short idle timeouts), so `prev` becomes the
filter itself — measured with a memory watcher on the saved pointer. From then on, any native
exception nobody handles spins forever instead of crashing. The exception that started the
loop in the test hosts: `0xC0000005` reading address 0 at `rtrsrvc.dll+0x5F9B7`, during process
exit (1C's own DLL detach). That address is D27's "unexplained" `0x2703F9B7`, and the Windows
log shows the old oscript adapter crashing at the same place: it is 1C's, not ours.

**Decision.**
1. `NativeProcess.CaptureCrashFilter()` before comcntr loads, `RestoreCrashFilter()` after
   every Connect: the process keeps the crash filter it had before 1C (coreclr's). A native
   crash on a 1C thread now ends the host (exit `0xC0000005`, supervisor restarts it) instead
   of a silent spinning thread. Probe `OneC.Host nativecrash`: before, the host lived on at
   101 % of a core; after, it exits in 4 s.
2. A host that loaded 1C leaves through `NativeProcess.Exit` (TerminateProcess) once its own
   cleanup is done — sessions are released by `SessionManager.Dispose` before that, so 1C sees
   the same orderly disconnect; only DLL detach code is skipped, which is where the crash
   lives. The old adapter was always ended by TerminateProcess anyway. Tests: the test host
   does the same at `ProcessExit` (module initializer).
3. The supervisor never trusts `Process.HasExited`: a process stuck in its exit already has an
   exit code, so `HasExited` is true, `Exited` never fires, the pipe stays open, and the old
   code skipped `Kill` for it. "Gone" is the process handle signalled; `StuckInExit` hosts are
   killed by the monitor and by `Stop` (Kill does finish them; only `Stop-Process`/`taskkill`
   refuse them, because of their own pre-checks).

**Not solved.** Why `rtrsrvc.dll` reads null at exit, and the heap corruption `0xC0000374` seen
once in a full suite (MIGRATION_STATUS). Both are inside 1C; D27's isolation still applies.

## D38 — Document writes: the old routes and markers, without the old write quirks (milestone 5.7)

**Decision (user, 2026-09-24).** Keep the old adapter's write routes, payloads and
`AIBA_<KIND>_<id>: <fullName>` markers so callers do not change, but do not port its
data-damaging behavior: no exchange-mode (`ОбменДанными.Загрузка`) posting, no catalog items
auto-created from a name-prefix search, never override a value the caller sent with autofill
(БезНДС, ВидОперации, …), idempotency respects the deletion mark, and a retry that already
succeeded answers success, not 422. Every behavioral difference from the old adapter is listed
here as it is implemented. A caller that depended on a dropped quirk gets an explicit error.

**Verification rule (user, 2026-09-24).** Write tests run only against the local copies on this
PC (bilim file base, the KAN restore), `AIBA_REWRITE_` marker, cleaned up. Never a client's base.

**Implemented (`WriteBody.cs`, `RefResolver.cs`, `DocumentWriter.cs`), difference by difference**
(quirk numbers from the adapter map, main.os @3ea6a81):

| old | now |
|---|---|
| unparseable body → empty map → 422 "Date missing" / PUT no-op 200 (Q14) | 400 before any COM call |
| any string with `-` at position 5 written as a date (Q12) | a string field gets text; dates, numbers, booleans parsed strictly by the field's type |
| name search = platform prefix match, first hit, any owner / deletion mark (Q2, Q16, Q20) | exact match, active, non-folder, owner-scoped; the strongest key given (ИНН, account number, code, document number) decides and a miss on it is a miss; two matches = `ambiguous_reference` |
| unresolved catalog ref → auto-created item (Q2, Q10, Q21–Q23), or raw string set (Q11) | `unresolved_reference`, document refused; create only on `СоздатьНовый`/`forceCreate` (or `allowCreate` for a bank account), with only the fields given, never nested |
| unresolved non-contract ref → WARN, field left empty, document written | every value that fails is an ERROR; the document is refused, nothing written |
| ChartOfAccountsRef had no id lookup; DocumentRef id unchecked, overridden by number (Q17, Q18) | id first and checked for existence for every kind |
| БезНДС / СуммаНДС / ВидОперации / rows / Комментарий overwritten (Q4) | a sent value is never changed; fill-empty only: ВидОперации of ПТУ/Реализация from its rows (Товары / Услуги / ТоварыУслуги), a bank document's accounts when the owner's ОсновнойБанковскийСчет or only active account gives a single answer |
| not ported | VAT from company history, accounts / subconto from registers and name similarity, default Склад / Подразделение, Заполнить(Основание), SF back-link re-post, waybill aliases, `_monthlyUpsert` (400) |
| `exchange=true`, and the forced exchange-mode post (Q1) | refused (400) |
| failed post → draft left, retried against a document found by number, 422 even on success (Q6–Q8) | one Записать; a failure writes nothing and returns 1C's own messages (`ПолучитьСообщенияПользователю`) |
| response id/posted from FindByNumber (Q9) | from the written object |
| idempotency ignored the deletion mark (Q5) | active documents only; the marker must be in Комментарий before the write (checked); same-marker creates serialised in the host |
| too-long strings cut by 1C, numbers rounded silently (Q13) | refused (`value_too_long`, `value_changed_by_1c`) |
| PUT: any document, tabular sections ignored, partial failures 200 (Q25) | AIBA documents only (D18); tabular sections 400; failures refuse; a posted document is changed and re-posted in one write |
| DELETE: 200 `success` even when not found (Q24) | 404 / 403; `?hard=true` deletes, default marks |
| no create ownership rule | Комментарий must start with the AIBA marker (D18) |

## D39 — Cloud sync is built against a local stub first (milestone 5.9)

**Decision (user, 2026-09-25).** The sync pipeline (poller, change detection, upload) is built
and verified against a local stub of backend/1c's `/entity/upload` contract. Nothing leaves
this PC; the real target (backend/1c or the aiba-next module) and its credentials are chosen
later. The stub implements the contract as the backend code has it (mapped from source, not
from memory) so the switch is a configuration change.

**Design, and how it differs from the connector's pipeline.** The sync runs in the supervisor
(`OneC.Sync`, no COM), next to the change feed. The connector had no feed, so it gated on row
counts, kept per-table hash maps, and needed two consecutive full sweeps to believe a delete;
here the feed names every change (D36), so: a cold read once per table (resumable, progress
saved only after the backend accepted a page), then per pass the feed's events — changed
objects re-read by id and upserted, a posted/unposted/deleted document's register rows re-read
by recorder and reconciled (`reconcile-recorder` drops that recorder's rows no longer live),
deleted objects pruned (`prune-missing`), and a feed reset re-reads everything. The tail
cursor is taken before the cold read, so changes made during it are replayed, not lost. Rows
keep the connector's `__rowKey`/`__rowHash` and are written as JavaScript would write them, so
rows the connector already stored are not rewritten by a different number spelling. Not yet:
organisation partitions, chart of accounts, bases whose event log records no data.

## D40 — The rewrite stops at 1C: no bank integrations (plan 5.10)

**Decision (user, 2026-09-25).** Bank sync and other integrations are not part of this
rewrite; the cloud bank service and bank-connector keep them. Also: the local stub is enough
as the sync target for now (D39 stays open for the real target).

## D17 — The per-session-cycle creep is 1C's, not ours

**Decision.** Superseded in detail by D24; the conclusion stands: it is inside comcntr, it is
per session cycle not per operation, and threads/handles do not leak.

**Evidence.** Phase 1 `churn`: ~3 MB per cycle with 10 or 60 reads per cycle alike (P).

## D24 — Keep sessions warm; reclaim native memory by recycling the host process

**Decision.**
1. Warm sessions are kept, not recycled by operation count (`RecycleAfterOperations` stays 0).
2. Session churn is minimised: `IdleTimeout` stays at 5 minutes; do not lower it to "save
   memory" — every close/reopen cycle leaves memory behind.
3. The mechanism that returns comcntr's retained native memory is **ending the host
   process**. The supervisor (milestone 2.4) recycles a host that is idle (no session in
   use) when its working set is above a threshold, and replaces any host that crashes.
4. Idle retirement (D08) still matters — it returns most of a session's memory and frees the
   budget for other bases — but it is not a full reclaim after long churn.

**Evidence** (P §2.3). Warm sessions are flat: 300 server create+delete pairs +4 MB, 100 file
pairs +3 MB, 3 600 reads +8 MB. Churn is not: drained WS 78 → 285 MB over 120 server cycles
(still +0.96 MB/cycle), 82 → 542 MB over 90 file cycles (+2.1 MB/cycle). Holding one keeper
session flattens the file-base curve (second-half slope −0.48 MB/cycle) — the growth is tied
to the base being fully closed and reopened. One non-reproducible `0xC0000005` on the session
thread after ~65 file-base churn cycles.

**Scale check.** With a 5-minute idle timeout a base churns at most ~12 times an hour;
120 cycles is ~10 hours of the worst case. A threshold-based host recycle handles that.

**Not chosen.** `PerBaseMinWarm = 1` (keeper) would flatten file-base growth but costs
~210–460 MB per active file base permanently; revisit only if recycling proves disruptive.
