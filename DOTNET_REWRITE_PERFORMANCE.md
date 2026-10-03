# .NET rewrite — resource log

Running record of what the new host actually costs. Every number here was measured on this
machine by `OneC.Host` itself; nothing is estimated. Updated as implementation progresses.

Machine: Windows 11 Pro 26200, x64, .NET 9.0.318. 1C 8.3.15.1565 (server agent + client)
and 8.3.18.1289 installed. Server base `kansler` (KAN, PostgreSQL 1C 12), file base
`bilim` (`D:\1C\bilim`). Workstation GC, concurrent GC off.

How to reproduce: `OneC.Host mem|bench|churn|poolram|lazy --bases <bases.json>`.

---

## Phase 1 — foundation, pool, one read slice (2026-09-21)

### Baseline

| point | working set | private | managed heap | threads | handles |
|---|---|---|---|---|---|
| process start | 30 MB | 9 MB | <1 MB | 8 | 228 |
| host + connector, no session | **35 MB** | 9 MB | <1 MB | 12 | 264 |

Binding comcntr and creating the connector costs ~5 MB. Registering bases costs nothing
at all — 42 registered bases still read 35 MB with zero pools and zero sessions.

### Warm sessions — server base (`kansler`)

Deltas against *host + connector*.

| point | working set | Δ vs host | private | threads | handles |
|---|---|---|---|---|---|
| host only | 35 MB | — | 9 MB | 12 | 264 |
| + server K=1 | 164 MB | **+129 MB** | 106 MB | 24 | 447 |
| + server K=2 | 213 MB | **+178 MB** | 155 MB | 25 | 461 |
| + server K=4 | 313 MB | **+278 MB** | 256 MB | 27 | 486 |
| after 160 reads | 325 MB | +290 MB | 266 MB | 27 | 486 |
| after idle sweep | 101 MB | +66 MB | 56 MB | 22 | 409 |
| after drain | 97 MB | +62 MB | 53 MB | 22 | 408 |

**Per server session: ~50 MB.** The first one costs 129 MB because it also warms comcntr;
sessions 2–4 cost 49, 49 and 51 MB.

### Warm sessions — file base (`bilim`)

| point | working set | Δ vs host | private | threads | handles |
|---|---|---|---|---|---|
| host only | 35 MB | — | 9 MB | 12 | 264 |
| + file K=1 | 303 MB | **+268 MB** | 242 MB | 17 | 424 |
| + file K=2 | 513 MB | **+478 MB** | 458 MB | 21 | 466 |
| + file K=4 | 931 MB | **+896 MB** | 886 MB | 23 | 527 |
| after 160 reads | 944 MB | +909 MB | 898 MB | 23 | 526 |
| after idle sweep | 92 MB | +57 MB | 52 MB | 18 | 375 |
| after drain | 88 MB | +53 MB | 45 MB | 18 | 374 |

**Per file session: ~210 MB — a little over four times a server session.** Four warm file
sessions for one base is 0.9 GB. This is the single most important number in this document
and it changed the design (see *Fixes* below).

Cross-checked without the pool, connecting by hand (`poolram --cap 0`):
35 → 302 → 508 → 714 → 920 MB for K=1..4, then **80 MB** after releasing all four. Same
~206 MB per session, and the memory does come back.

### Under load

`bench`, warm pool, repeated parallel reads of 100 rows.

| base | K | reads | throughput | WS after round 1 | WS after round 6 | drift |
|---|---|---|---|---|---|---|
| kansler (SERVER) | 4 | 6 × 600 | 2 256–2 459 op/s | 309 MB | 317 MB | **+8 MB, flat from round 2** |
| bilim (FILE) | 2 | 6 × 300 | 3 797–4 348 op/s | 520 MB | 522 MB | **+2 MB** |

3 600 reads on the server base and 1 800 on the file base with no growth once the pool has
filled. Handles flat (463 → 464 and 453 → 455). Managed heap stays under 1 MB throughout,
which is exactly why working set and private bytes are the numbers being tracked.

### Create / destroy churn

`churn`, 6 cycles of (fill pool → read → drain), server base:

| cycle | peak WS | drained WS | threads (drained) | handles (drained) |
|---|---|---|---|---|
| 1 | 202 MB | 78 MB | 19 | 384 |
| 2 | 214 MB | 83 MB | 19 | 387 |
| 3 | 212 MB | 85 MB | 19 | 387 |
| 4 | 213 MB | 88 MB | 19 | 387 |
| 5 | 220 MB | 92 MB | 19 | 387 |
| 6 | 218 MB | 96 MB | 19 | 387 |

Threads and handles are **completely flat** across cycles — no thread or handle leak from
session create/destroy. Working set creeps ~3 MB per cycle and decelerates.

Running the same churn with 6× the reads per cycle (60 instead of 10) gives the same creep
(84 → 97 MB over 6 cycles), so **the creep is per session, not per read** — it is 1C's own
per-connection metadata cache inside comcntr, not something the host holds. Nothing here
releases it; the mitigation is not to churn sessions needlessly, which the warm pool
already handles.

Residual after everything is drained and the host is disposed: ~62 MB above the 35 MB
baseline, +10 threads, +143 handles. Those are comcntr's own threads and handles; they
appear on the first connect and never grow again.

### Connector's own pool — measured, then left off

The connector's type library exposes `PoolCapacity`, `PoolTimeout` and `MaxConnections`.
All three default to **0** (off) and all three are writable.

Connect + release latency, 6 cycles:

| base | PoolCapacity=0 | PoolCapacity=4 |
|---|---|---|
| kansler (SERVER) | 941–1051 ms every time | 940 ms once, then **0 ms** |
| bilim (FILE) | 1637–1798 ms every time | 1818 ms once, then **0 ms** |

It works — reconnect becomes free. But it does that by keeping the connection alive, and
that defeats the thing the rewrite needs most:

| after releasing 4 server sessions | working set |
|---|---|
| PoolCapacity=0 | 286 MB → **73 MB** |
| PoolCapacity=4 | 284 MB → **284 MB**, still 284 MB two seconds later |

**Verdict: leave `ConnectorPoolCapacity = 0`.** It duplicates our warm pool (we already
keep sessions warm) and removes the one thing ours adds — the ability to hand memory back
when a base goes quiet. `Connect` identity was also checked: even with the pool on, two
`Connect` calls return different IUnknowns, so it pools the underlying connection, not the
object. The option stays in `PoolOptions` for a workload that is provably connect-bound.

### Memory budget in action

`bench --base bilim --k 4 --maxws 400`: four worker threads, a 400 MB ceiling, a base whose
sessions cost 210 MB each.

```
round 0 baseline      ws=305 MB  sessions=1
round 1  80 reads     ws=308 MB  sessions=1   611 op/s
round 2  80 reads     ws=308 MB  sessions=1   784 op/s
PoolStats { bilim, Live = 1, Rents = 161 }
```

All 160 reads completed against a single session, zero failures, working set never passed
308 MB. Memory pressure turns into a little latency, not into errors.

### Process model

Two hosts started at the same time, one per platform version, no registry involved:

| process | comcntr | base | result | WS |
|---|---|---|---|---|
| A | 8.3.15.1565 | kansler (SERVER) | 3 rows, 4454 ms | 143 MB |
| B | 8.3.18.1289 | bilim (FILE) | 3 rows, 2651 ms | 314 MB |

`plan` collapses bases onto the fewest processes: with both bases needing 8.3.15 or newer,
`2 bases -> 1 host process`. A second process only appears when a server base pins a
version no other base can share.

---

## Phase 2.1 — write vertical slice (2026-09-23)

`OneC.Host write`, `WriteService` over the same pool. Every document tagged
`AIBA_REWRITE_<run>` and deleted afterwards; `cleanup` confirmed 0 left on both bases.

### Lifecycle timings (one session)

| step | bilim (FILE) | kansler (SERVER) |
|---|---|---|
| create (clone + `Записать`), first in process | 46 s / 157 s (two runs) | 8.4 s |
| update | 46–149 ms | 45 ms |
| post | 5.7 s | blocked (KAN config, D14) |
| mark / unmark for deletion | 420–764 / 42 ms | 45 / 45 ms |
| delete (posted doc) | 11–18 s | — |
| delete (unposted doc) | — | 4.4 s |

The cold first write on the file base is very slow and variable (46 s vs 157 s); warm creates
are ~1 s. Not investigated yet.

### Write burst with reads alongside

| base | writers × creates | created | write throughput | reads during burst | errors |
|---|---|---|---|---|---|
| bilim (FILE) | 2 × 5 | 10/10 | 0.9 op/s | 24 302 | 0 |
| kansler (SERVER) | 4 × 5 | 20/20 | 8.3 op/s | 1 481 | 0 |

Cleanup: 10 file-base deletes in 2.2 s, 20 server deletes in 2.7 s.

On the server base the 4 writers held all 4 pool slots, so the reader got far fewer turns
(`Waits = 7`). Shared-pool fairness between reads and writes is carried into 2.2.

### Writing makes sessions heavier

| session state | server | file |
|---|---|---|
| read only (phase 1) | ~50 MB | ~210 MB |
| after writes | **~95 MB** (4 sessions: 37 → 419 MB) | **~400–460 MB** (1 session: 499–527 MB; 2 sessions: 826 MB) |

1C loads object and form modules into the session on first write and keeps them. A file
session that has written costs about twice a read-only one. With `MaxWorkingSetMb = 1200`
a host fits two write-active file sessions, which matches the file-base write ceiling of 2.
The budget measures the live working set, so this growth is counted; only the estimate for a
*new* session (`EstimatedSessionMb`) is read-sized.

ComRefs: 73 062 created in the file burst run, 1 live at exit (the connector),
0 wrong-thread releases, 0 release failures.

---

## Phase 2.2 — several bases in one host (2026-09-23)

`OneC.Host multi --phase 16 --idle 10 --pressure 600 --doc ПоступлениеТоваровУслуг`, default
budget (8 sessions, 1200 MB) unless stated.

### A — both bases busy

kansler ×4 readers + bilim ×2 readers, 16–20 s:

| | WS | private | sessions |
|---|---|---|---|
| steady | 754–758 MB | 700–703 MB | kansler 4 + bilim 2 = 6 |

~40 000 server reads and ~59 000–67 000 file reads per phase, 0 errors. Flat from t=4 s.

### B — server base goes quiet (idle timeout 10 s)

| t | WS | kansler sessions | bilim sessions |
|---|---|---|---|
| 0–10 s | 755–758 MB | 4 | 2 |
| 12 s | 676–679 MB | **0** (retired 4) | 2 |
| 14 s+ | 609–614 MB | 0 | 2 |

A quiet base gives all its sessions back; it does not hold K forever.

### C — memory pressure, ceiling 600 MB, idle retirement disabled

| run | peak WS | kansler | bilim | reads / errors |
|---|---|---|---|---|
| before fix | **753 MB** (ceiling ignored) | 4, evicted 0 | 2 | 63 586 / 0 |
| after admission fix | 566 MB | 0, evicted 4 | 2, **evicted 1 (self)** | 37 303 / 0 |
| after self-eviction fix | **396 MB** | 0, evicted 4 | 1, evicted 0, created 1 | 31 186 / 0 |

Two bugs found and fixed here (D21, D22). With both fixed, the busy base evicts the quiet
base's idle sessions, then its two readers share one session instead of breaking the ceiling.

### D — read/write fairness on one server base (default pool, cap 4)

| writers | reads (1 reader thread, 16 s) | p50 | p95 | max | creates |
|---|---|---|---|---|---|
| 0 | 11 567 | 1 ms | 1 ms | 11 ms | — |
| 4 | 5 827 | 1 ms | 2 ms | **835 ms** | 578 (all deleted) |

Reads stay fast at p95 with four writers holding the cap; the cost is a sub-second tail when
the reader waits for a slot. Warm-session creates ran at ~36 op/s (578 in 16 s) — much faster
than the cold 2.1 burst.

### Crash found: connector teardown + recreate

Running phase C in the same process after phases A+B had disposed their `SessionManager`
killed the process with `0xC0000005` inside `IDispatch::Invoke` on the first query. Phase C
alone in a fresh process ran clean. Cause: releasing the process's last `V83.ComConnector`
and creating another. Fixed by a process-lifetime shared connector (D20); the full A→B→C→D
run then completed in one process, 698 812 ComRefs created, 0 live at exit, 0 wrong-thread.

---

## Phase 2.3 — long churn and long-lived sessions (2026-09-23)

CSV series in `measurements/`. "Drained" = sampled after every session for the base was
disposed, GC settled.

### Session churn (fill pool K=2 → 20 reads → drain, repeated)

| run | cycles | drained WS first → last | slope 1st half | slope 2nd half | threads | handles |
|---|---|---|---|---|---|---|
| kansler (SERVER) | 120 | 78 → **285 MB** | 2.60 MB/cycle | 0.96 MB/cycle | 19 → 21 | 409 → 426 |
| bilim (FILE) | 90 | 82 → **542 MB** | 9.88 MB/cycle | 2.14 MB/cycle | 15 → 18 | 390 → 404 |
| bilim (FILE), keeper session held open | 90 | 325 → 737 MB | 8.49 MB/cycle | **−0.48 MB/cycle** | 17 → 15 | 453 → 465 |

File base without keeper, every 5 cycles: 98, 385, 431, 453, 479, 503, 506, 513, 519, 516,
517, 521, 527, 535, 538, 540, 541, 542 MB — a jump of ~290 MB between cycles 5 and 10, then
a slow climb. With a keeper session the retained memory flattens at ~736 MB from cycle 40
(≈ 410 MB above the keeper's own cost) with two transient dips to ~420 MB.

Reading: comcntr keeps native memory per base that session disposal does not return —
roughly 200 MB after 120 server cycles and 400–460 MB for a file base. Threads and handles do
**not** leak. The only complete reclaim is ending the process.

**One crash.** The first 80-cycle file-base run died at ~cycle 62–70 with `0xC0000005` inside
`IDispatch::Invoke` on the session thread (the Connect / platform-version path), while a
server churn ran in another process. Two further 90-cycle file runs completed. Not
reproducible on demand; treated as a host-fatal event the supervisor must survive.

### Long-lived warm session (K=1, create + delete pairs)

| base | pairs | WS after warmup → end | ms/pair | errors |
|---|---|---|---|---|
| kansler (SERVER) | 300 | 198 → 202 MB (+4) | 177–181 | 0 |
| bilim (FILE) | 100 | 499 → 502 MB (+3) | 191–194 | 0 |

Together with phase 1 (3 600 reads, +8 MB) and 2.1: a warm session does not grow with use.
Recycling by operation count is not needed.

---

## Phase 2.4 — supervisor, multi-version hosts, recycling (2026-09-23)

`OneC.Supervisor verify --bases … --file-version 8.3.18.1289` (the file base is told to need
8.3.18 so the plan produces two hosts from the bases available).

| item | measured |
|---|---|
| plan → 2 hosts started and ready | 376 ms |
| host 8.3.15 (kansler) after one read | pid 32732, WS 143 MB, priv 80 MB; 1C says 8.3.15.1565 |
| host 8.3.18 (bilim) after one read | pid 41556, WS 316 MB, priv 250 MB; 1C says 8.3.18.1289 |
| supervisor process | **35 MB**, **0** comcntr modules mapped |
| kill a host → restarted and serving a read | **2.6 s** (restarts = 1) |
| manual recycle of an idle host | 145 MB → **34 MB** fresh, 3.0 s, read OK after |
| auto-recycle, zero sessions, threshold 50 MB | fired after the session idle-retired (~29 s with a 30 s sweep); 150 → 34 MB |
| OneC.Host processes after supervisor dispose | **0** |

### Connect/release overlap stress (`connstress`)

Threads each loop open → read → release on the one shared connector, so Connects and
releases on different threads constantly overlap.

| base | threads × iterations | runs | sessions/s | failures | crashes |
|---|---|---|---|---|---|
| kansler (SERVER) | 6 × 25 | 3 | 3.1–3.7 | 0 | 0 |
| bilim (FILE) | 4 × 30 | 2 | 1.7 | 2 / 240: `Database sharing violation '…/1Cv8tmp.1CD'` | 0 |

The sharing violation is transient (the same Connect succeeds on retry) and is now classified
retryable and retried at session open (D26).

### The intermittent crash — current record

| where | frequency |
|---|---|
| file-base churn (80 cycles, another churn process running) | 1 of 3 long runs, ~cycle 62–70 |
| full live test suite (in-process managers + child hosts) | 1 of ~16 runs ("Test Run Aborted") |
| targeted Connect/release overlap stress | 0 of 690 session cycles |

Always `0xC0000005` inside `IDispatch::Invoke` on a session thread. Not reproducible on
demand; the cause is not established. Mitigation is structural: hosts are separate processes
and the supervisor restarts a dead one in ~2.6 s.

*Later (superseded):* the coreclr crashes were on the connector's typelib-bound IDispatch,
removed by D29; the "Test Run Aborted" runs were unhandled test-thread exceptions and an
`out object` marshalling bug in the vtable Connect (D27 update); the non-coreclr address is
1C's `rtrsrvc.dll` at exit (D37). What is still open is one `0xC0000374` heap corruption
inside 1C (MIGRATION_STATUS, "Blocked / known limits").

Test-suite wall time varied 1 m 15 s – 5 m 25 s across runs with identical results — the
live bases' own load, not the host.

---

## Milestone 3 — IPC: pipes inside, HTTP edge (2026-09-23)

Transport candidates: `research/ipc-transport/README.md` (pipe +5 MB idle / 32 µs; HTTP
+16 MB / 213 µs; gRPC +17 MB / 314 µs).

`OneC.Supervisor verify --file-version 8.3.18.1289`, two hosts, 200 warm sequential reads of
one row per base:

| base | HTTP edge → pipe → host → 1C, p50 / p95 | pipe only, p50 / p95 | edge adds |
|---|---|---|---|
| bilim (FILE, 8.3.18 host) | 1.44 / 2.19 ms | 0.72 / 1.11 ms | ~0.7 ms |
| kansler (SERVER, 8.3.15 host) | 2.10 / 3.11 ms | 1.83 / 2.62 ms | ~0.3 ms |

| process | WS | private |
|---|---|---|
| supervisor + Kestrel edge, after the run | 78 MB | 28 MB |
| host 8.3.15 (kansler, write-used session) | 211 MB | 139 MB |
| host 8.3.18 (bilim, restarted after the kill test) | 316 MB | 250 MB |

The supervisor went from 35 MB (no edge, 2.4) to 78 MB with Kestrel and the test's own
HttpClient traffic — paid once per machine, as D28 intended. Killed host → reads served again
through HTTP after 3.4 s.

### Reads that present references are slow inside 1C

`РегистрБухгалтерии.Хозрасчетный`, fields `Период, Сумма, Регистратор`, 100 000 rows:
**87.7 s** (~0.9 ms/row) — every `Регистратор` value is a COM reference turned into text by a
`Строка()` call on the connection. Catalog reads without reference fields run ~16 µs/row warm
(37 526 rows in 609 ms). Returning GUIDs or skipping presentation for bulk reads is the fix
when subsystem migration gets to register reads. *(Done in 4.0: 87.7 → 31.0 s, D30.)*

### Churn re-run after D29 (vtable Connect), the original crash condition

Server 120-cycle churn and file 80-cycle churn in parallel — the setup that produced a
coreclr `0xC0000005` before:

| run | result | drained WS first → last | slope 2nd half | threads | handles |
|---|---|---|---|---|---|
| kansler, 120 cycles | exit 0 | 78 → 271 MB | 0.69 MB/cycle | 19 → 19 | 399 → 410 |
| bilim, 80 cycles | exit 0 | 85 → 531 MB | **0.06 MB/cycle** | 15 → 14 | 380 → 385 |

No crash; the file-base residue flattened at ~531 MB where the pre-D29 run was still climbing
2.1 MB/cycle at cycle 90. One run each — suggestive, not proof.

---

## Milestone 4.0 — reference rendering (2026-09-24)

`OneC.Host refbench`, 5 000 rows, KAN, warm session. µs per row:

| variant | Регистратор (register) | Номенклатура.Родитель (catalog) |
|---|---|---|
| old: per-row `Строка()` on the connection (always failed → fallback) | 938–1 921 | 155 |
| `ПРЕДСТАВЛЕНИЕ()` in the query | 90–322 | 36 |
| per-row `XMLСтрока()` → GUID | 22–27 | 22 |
| fetch the RCW and drop it (floor) | 7–14 | 7 |
| `ПРЕДСТАВЛЕНИЕ()` + GUID | 67–99 | 40 |

100 000-row `РегистрБухгалтерии.Хозрасчетный` read (Период, Сумма, Регистратор):
**87.7 s → 31.0 s**, and the text is now correct (D30).

---

## Milestone 4.5 — the whole desktop stack (2026-09-24)

`tools/measure-stack.ps1`: launches the app, samples app → supervisor → hosts from outside,
closes it. Two infobases configured (kansler + bilim, one 8.3.15 host).

| process | idle, no sessions | after a 100-row register read (1 server session) |
|---|---|---|
| AIBA.Connector (WinUI window, self-contained) | 168 MB WS / 114 MB private | 183 / 125 MB |
| OneC.Supervisor (+ Kestrel edge) | 59 / 18 MB | 56 / 16 MB |
| OneC.Host 8.3.15 | 42 / 12 MB | 173 / 106 MB |
| **stack total** | **269 MB** | **412 MB** |

After closing the window: 0 supervisor/host processes left (every run).

**Re-measured 2026-09-25** (final build, same script; five infobases configured instead of
two — KAN, Kanstik and signum were imported — still one 8.3.15 host):

| process | idle, no sessions | after the 100-row register read |
|---|---|---|
| AIBA.Connector | 165 / 111 MB | 181 / 122 MB |
| OneC.Supervisor (+ Kestrel edge) | 59 / 19 MB | 57 / 18 MB |
| OneC.Host 8.3.15 | 42 / 12 MB | 171 / 104 MB |
| **stack total** | **266 MB** (was 269) | **409 MB** (was 412) |

No regression; the three extra configured bases cost nothing until used (lazy pools, D10).
0 supervisor/host processes after close, both runs.

The WinUI window is the largest idle process — larger than the engine. 1C sessions are
still the variable cost (D10: ~50 MB server, ~210 MB file per session); the fixed cost of
the whole stack with no 1C work is ~270 MB.

---

## Milestone 5.2 — catalog reads in the old row shape (2026-09-24)

`OneC.Host catbench` (KAN, warm schema, median of 5, 1000-row keyset page). "Old algorithm"
is `LegacyCatalogOracle`: the old adapter's per-value steps without its always-failing
`Строка()` call and without oscript — so the real old adapter was slower still.

| catalog | fast page | µs/row | old algorithm | µs/row | × |
|---|---|---|---|---|---|
| Контрагенты | 294 ms | 294 | 8 061 ms | 8 061 | 27 |
| Номенклатура | 300 ms | 300 | 2 989 ms | 2 989 | 10 |
| ДоговорыКонтрагентов | 248 ms | 248 | 478 ms | 478 | 1.9 |

How it got there (same catalogs, `CATBENCH_SPLIT=1` shows execute vs row walk):

| step | reference value | enum value | ДоговорыКонтрагентов page |
|---|---|---|---|
| first cut: deref columns, name lookup per call | ~24 µs | ~17 µs | 668 ms (0.8× — slower than old) |
| + `DispatchMemo` (DISPIDs once per `Выборка`) + empty-reference flag in the first deref column | ~2 µs | ~17 µs | 305 ms |
| + enum by `f.Порядок` → name from metadata | ~2 µs | ~2 µs | 248 ms |

What is left per page is mostly the query itself (execute 100–170 ms per 1000 rows on KAN).

**A trap found by the whole-configuration sweep.** Attributes typed "any reference"
(`ИдентификаторыОбъектовМетаданных.ЗначениеПустойСсылки`, file owners) made query-side
dereferencing join every table: a 20-row page took **25 s** to execute. Type sets wider than
4 references now go per value (`MaxDerefTypes`): KAN's 217-catalog sweep went 92 s → 3.6 s.

**Whole catalogs, keyset walk** (`catwalk`, KAN, tabular sections included):

| catalog | rows | page 1000 | page 250 (the connector's size) | host after walk (page 1000 / 250) |
|---|---|---|---|---|
| ДоговорыКонтрагентов | 116 757 | 34.4 s (3 394 rows/s) | 46.2 s (2 525 rows/s) | 258 / 181 MB WS |
| Номенклатура | 37 526 | 11.0–14.5 s (~3 300 rows/s) | 22.8 s (1 644 rows/s) | 262–274 / 201 MB WS |
| Контрагенты | 17 388 | 6.3 s (2 775 rows/s) | — | 197 MB WS |

- Four walks of Номенклатура in one process: WS 266 → 271 → 271 → 262 MB, private ~200 MB,
  handles 416–430, 0 live ComRefs after every walk — **no drift**.
- Bigger pages are faster (fixed ~100 ms per query) but cost RAM: a 1000-row page is 7.5 MB
  of JSON and leaves the GC ~100 MB committed for a 23 MB live heap; 250-row pages leave
  26–45 MB. `DOTNET_GCConserveMemory` 5/9 changed WS by ≤ 20 MB (noise) — page size is the
  lever, not GC settings.
- File base (bilim): cold first page 2–20 s (connect + first metadata access), warm 26–30 ms.
- Schema per catalog: first metadata access in a session ~1.2–1.9 s, then 3–15 ms per catalog
  (cached 10 min).

---

## Milestone 5.3 — document reads, and the reference-cache leak (2026-09-24)

**Found: reading a reference's fields over COM grows 1C's native memory without bound.**
`OneC.Host docwalk`, KAN `РеализацияТоваровУслуг`, 250-document keyset pages, samples every
20 pages (GC settled first):

| variant | 25 000 docs + 175 953 lines | private bytes | live ComRefs | managed |
|---|---|---|---|---|
| per-value `ref.Номер` for wide composites (the old algorithm's way) | 218.9 s | 143 → **1 170 MB**, +~10 MB per page, linear | 0 | 6 MB |
| headers only, per-value | 67.5 s / 15 000 docs | 116 → 657 MB | 0 | 2 MB |
| headers only, per-value skipped (A/B) | — | flat ~140 MB | 0 | 2 MB |
| **`RefBatch`** (one query per referenced type per page), headers | **11.4 s** / 15 000 docs | **flat 160 MB** | 0 | 2 MB |
| **`RefBatch`**, with tabular sections | **68.7 s** | **flat 206–238 MB** | 0 | 6 MB |

Every property read on a reference over COM makes 1C load that object into the session's
client-side cache; nothing we release frees it. Only wide composite attributes go per value
(`Сделка`, `ДокументПартии`, the `Субконто1..3` of every tabular section here) — and those were
enough for 1.2 GB after 25 000 documents. The old adapter read *every* reference that way,
which fits its crash history (heap corruption after long walks). `RefBatch` collects those
references per page by XML type and resolves each type with `ВЫБРАТЬ Ссылка, Наименование /
Код / Номер … ГДЕ Ссылка В (&refs)`: no object loads, 3–6× faster, same values (both sweeps
re-run identical).

Document page, KAN sales, 250 documents with tabular sections: fast **637 ms**, old algorithm
4 773 ms (7.5×). The by-date list is two queries (refs in order, then render): one query with
the rendered columns' joins made PostgreSQL sort the whole table — 20-row page 4.6 s → 0.7 s.

---

## Milestone 5.4 — register reads (2026-09-24)

`OneC.Host regwalk`, KAN `Хозрасчетный` (3 M movements), cursor walk asc from 2025-06-01,
250-row pages, GC-settled samples every 20 pages:

| | value |
|---|---|
| 30 000 rows / 120 pages | **55.9 s — 537 rows/s** (old adapter's measured walk: 26 463 rows in 197 s, 134 rows/s) |
| host private bytes | 134 → 142 → 138 → 139 → 139 → 136 MB — **flat** |
| live ComRefs / managed heap | 0 / 0 MB after every sample |
| host CPU | 14.5 s (0.48 ms/row; the rest is the 1C server) |
| first page, fast vs old algorithm | 452 ms vs 2 580 ms (5.7×) |

**Found: the column probe.** `ВЫБРАТЬ ПЕРВЫЕ 0 * ИЗ ….ДвиженияССубконто` with no period
parameters did not return within 10 minutes — the virtual table joins subconto over the whole
register first (the same 612 s effect the old adapter measured). An empty window
(`ДАТАВРЕМЯ(3999,12,31)` both ends) returns the same columns in milliseconds.

Whole-configuration sweeps after every change in this milestone (bilim + KAN): registers
1 149/1 149, catalogs 682/682, documents 525/525 identical to the old algorithm. Sweep time on
KAN, fast vs old algorithm: documents 23.2 s vs 272.8 s, accumulation registers 2.2 s vs
4.9 s. On bilim (file base) the old algorithm is faster on these tiny 20-row pages: fixed
per-query costs (more queries: pick + render + tabular + `RefBatch`) dominate when a page
has a handful of rows. Full pages invert that on KAN (the walks above); full-page walks on the
file base are not measured yet (bilim has little data left).

---

## Milestone 5.5 — cold reads (2026-09-24)

`OneC.Host coldread`, KAN `Хозрасчетный`, June 2025 (26 504 movements), 250-row cursor walks
over `SlicePlanner` slices, one pool session per slice:

| sessions | slices (rows) | wall | rows/s | host private after | same rows as K=1 |
|---|---|---|---|---|---|
| 1 | 26 504 | 44.6 s | 593 | 142 MB | — |
| 2 | 13 680 / 12 824 | 24.7 s | 1 072 (1.8×) | 225 MB | yes |
| 4 | 6 868 / 6 812 / 6 558 / 6 266 | 15.3 s | 1 737 (2.9×) | 387 MB | yes |

Planning (base table grouped by day) took 10–1 074 ms (the first includes the session
connect). Each extra session costs ~80 MB private — the D10 per-session figure.

`OneC.Host xdtobench`, warm 1 000-row accounting page, 32 columns, 4 reps:

| step | ms |
|---|---|
| `Выполнить` (windowed) | 341–389 |
| + this host's per-cell walk with `RefBatch` | +382–795 (warm ~380) |
| + `Выгрузить` + `СериализаторXDTO` + C# XML parse | +330–805 (warm ~330), 4.2 MB XML |

The XDTO path saves ≤ 7% and still lacks reference rendering — not ported (D35). Earlier bench
without a window: `Выполнить` alone 51–57 s for the same 1 000 rows — the window is what
makes accounting reads fast, not the row path.

---

## Milestone 5.6 — change feed (2026-09-24)

`OneC.Host logscan`, KAN server base, newest `.lgp` (`20260901000000.lgp`, 2 072 MB, 24 days)
read from its start in 8 MB steps — the worst catch-up a base can ask for:

| | value |
|---|---|
| records / data events | 4 604 437 / 361 224 (РегистрСведений 333 322 — the МИКО telephony job, Документ 16 867, РегистрНакопления 9 761, РегистрБухгалтерии 1 216) |
| time | 21.2 s in 260 reads — **98 MB/s** |
| process private bytes | 9 → 119 MB (read buffers; managed heap 0 after GC) |
| 1C sessions | 0 — the feed never connects |

A normal poll reads a few KB from the tail.

---

## Milestones 5.7 / 5.8 — writes and posting (2026-09-25)

Round-trip sweeps (`OneC.Host writeparity`): per document type, read the newest document into
an old-style body, create a copy through the writer, read the copy, delete it.

| run | types | wall | per type |
|---|---|---|---|
| KAN, drafts | 79 | 999 s | ~12.6 s (KAN deletes take ~10–20 s each) |
| KAN, posted | 67 | 995 s | ~14.9 s |
| bilim, drafts | 14 (of 327 scanned) | 1 157 s | dominated by the file-base stalls (MIGRATION_STATUS) |
| KAN, one ПТУ copy posted | 1 | 157 s | the configuration's own posting cost |

A write refused for a bad value costs no 1C write at all (checked before the object is
created): `UnresolvedReferencesRefuse…` 2 s, `ATooLongString…` 15 ms, `exchange=true` 15 ms.
Per-write memory was not measured separately; the sweep host stayed at ~400–500 MB WS on KAN.

---

## Milestone 5.9 — sync into the local stub (2026-09-25)

The 5.9 engine and its `sync` mode were removed on 2026-10-02 (D48); numbers kept as history.

| run (KAN, local copy) | result |
|---|---|
| `sync` mode, cold read: Банки 500 + ПТУ since 01.09 (19, all tabular sections) + Хозрасчетный since 01.09 (27) | pass 33 s incl. first connect and schema loads; 3 uploads, 365 KB |
| live feed test: posted copy → stub with its movements, then delete → gone | 87 s end to end, including posting the copy on KAN (the split between posting, the server's log write delay and the sync passes was not measured) |

The connector's per-cycle costs that disappear with the feed: `/counts` per table per base, a
full read of `Catalog_Контрагенты`/`Банки` every cycle, and budgeted edit sweeps.

## Sync — S0 / S1 (2026-09-30)

| measurement | result |
|---|---|
| Event log, KAN September file (2.7 GB, 5.9 M records), full read by the spike tool | 23 s |
| Version scan (`Ссылка, ВерсияДанных`, keyset 5 000), KAN РеализацияТоваровУслуг 89 988 docs | 9.6 s = 9 415 rows/s, host +11 MB private |
| Version scan, KAN Номенклатура 37 527 | 1.7 s = 21 997 rows/s |
| `sync.db` small commit (cursor + item, WAL, synchronous=FULL) | p50 5.2 ms, p95 10.2 ms, max 18 ms |
| 10 000 work items, 20 transactions | 0.2 s |
| 1 M `object_versions` (random GUIDs, 5 000 per transaction) | 128 s = 7 800/s, 159 MB on disk (167 B/object); 2 MB default cache: 5 600/s |
| verify page (5 000 lookups + stamps) against 1 M stored | 58 ms |

### Sync — final local numbers (S4–S15, consolidated 2026-10-01)

Measured on this machine (local KAN server base, bilim file base, stub or the isolated local
backend/1c). No shared-dev number yet (S12 dev is blocked on the developer's dev login).

| measurement | result | raw |
|---|---|---|
| Idle scheduler, 23 real bases, 10 min | **0.009 % CPU**, 8–11 MB private, 0 sync sessions | `s4-idlebench.txt` |
| Sync engine private memory (kill test, engine alone in a child process) | **41–49 MB** peak across four 50-kill runs | `s15-chaos-*.txt` |
| Supervisor private during snapshots | catalog 116 759 rows ≤ 145 MB; accounting K=1 177 MB, K=4 166 MB; into the local backend 118 MB | `s5-kan-accounting.txt`, `s12-local-backend.txt` |
| Host (1C) memory during the accounting snapshot | **K=1 195 MB private / 268 MB ws; K=4 497 MB private / 561 MB ws** | `s5-kan-accounting.txt` |
| Catalog snapshot, KAN ДоговорыКонтрагентов 116 759 rows, stub | ~2 200 rows/s (= plain pipe reading; in-process walk 3 163, the gap is the IPC hop, D43) | `MIGRATION_STATUS` S5 |
| Accounting snapshot, KAN Хозрасчетный 12 months, 249 148 rows / 381 MB | **K=1 227 rows/s → K=4 1 301 rows/s (5.7×)** | `s5-kan-accounting.txt` |
| Same, window from 2026-04-01 (66 552 rows) | raw host walk 476 rows/s, pipeline 619 rows/s (no pipeline overhead) | S5 |
| Feed replay, KAN September log 2.7 GB | 59 s (45 MB/s), 440 575 data events → 1 473 work items, 98 MB private | `s6-feedbench.txt` |
| Verify pass, KAN РеализацияТоваровУслуг 89 988 documents | 6.3 s (14 300 objects/s), 0 changed / 0 gone | S11 |
| Independent information register refresh, nothing changed | bilim 68 rows 0.1 s; KAN 4 387–4 749 rows 0.4 s; 23 521 rows 5.0 s; МИКО 1 467 891 rows (not synced) 223 s | `s9-refresh.txt` |
| Upload into the isolated local backend/1c (v2 multipart) | 2026-09-30: 116 759 rows / 272.6 MB in 71.8 s = 1 625 rows/s, supervisor 118 MB. **2026-10-01 after the upload-buffer fix (no growth copies, no `ToArray`): 57.1 s = 2 044 rows/s, supervisor 115 MB, byte budget peak 2.3 MB** — stored rows counted in the isolated Mongo: 116 759, distinct keys 116 759 (the 2026-09-30 "read back" was backend/1c's `/counts`, not a read-back). Part of the speed-up may be a warm KAN cache; the fix's certain effect is fewer copies of each ≤ 8 MB body | `s12-local-backend.txt`, `s12-local-backend-2026-10-02.txt` |
| Kill test convergence after the last kill (50 kills, ~3 500 changes, backend = 1C exactly) | 195 s and 266 s → **9 s** after the leftover-lease fix (items of a killed engine were locked for the 5-min lease) | `s15-chaos-final-4*.txt` |
| "Add table" metadata list (names + synonyms, one COM walk) | bilim 1 656 tables 3.0 s, kansler 706 tables 2.3–2.6 s; details for 42 tables 0.35–0.42 s; host-cached after (0.5 s) | `TableCatalogLiveTests` |

The 301 s / "FAIL: 2 differences" run in `s15-chaos-50.txt` is the first run, before the two fixes
it found (MIGRATION_STATUS S15) — kept as the record of the bugs, not a result.

Raw: `measurements/sync-s0/`.

---

## Process exit and native crashes (2026-09-24, D37)

| | before | after |
|---|---|---|
| test hosts left after runs | 10, each one thread at ~100 % of a core, 296–838 MB WS; machine CPU 83 % (16 logical) | 0 (full suite + loops) |
| after those were cleared | machine CPU 25 % | — |
| file base bilim while a stuck process had held it | every new Connect blocked ~45 min (session starts, then waits), even after the processes were gone | — |
| `OneC.Host nativecrash` (crash on a native thread after a reconnect) | host lives on, 101 % of a core | host exits `0xC0000005` in 4 s |
| `OneC.Host exittrap --base` (access violation planted in process exit) | — | exit 0, no crash |

Caveat for earlier numbers: from ~20:30 one stuck test host (1 of 16 logical CPUs) ran
alongside the measurements, and 21:47–22:44 up to ten. The 5.5 cold-read table (20:44) and the
5.6 feed rate (21:37) ran with that one busy core; small effect on a 16-CPU machine, but
re-measure before quoting them as limits.

---

## Budget guidance that follows from these numbers

For the supported 16 GB minimum, per host process:

| setting | value | why |
|---|---|---|
| `GlobalMaxSessions` | 8 | 8 server sessions ≈ 0.4 GB; the memory ceiling catches the rest |
| `MaxWorkingSetMb` | 1200 | count alone cannot bound a mix of 50 MB and 210 MB sessions |
| server `MaxConcurrency` | 4 | measured ceiling of useful concurrency, ~200 MB |
| file `MaxConcurrency` | **2** | 4 would be 0.9 GB for one base |
| `PerBaseMinWarm` | 0 | nothing exists until a base is used |
| `IdleTimeout` | 5 min | a quiet base gives its memory back |

A client with 30 configured bases and one active base costs one session.

---

## Open resource questions

Reconciled 2026-09-25. Answered since this list was written: session-cycle creep (§2.3 —
comcntr keeps native memory per base that only a process restart returns; handled by
zero-session recycling, D24/D25); write workloads (§2.1, §5.7–5.8); several active bases in
one host (§2.2).

Still open:
- Idle retirement has only been measured over minutes, not hours.
- x86 hosts are unmeasured — no x86 1C install on this machine.
