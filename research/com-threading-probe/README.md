# V83.ComConnector threading probe

Measured answers to "why is the COM connector a singleton, and can it be made concurrent?"
Everything here was produced by running `com-probe` against a live base, not by reading
docs. 1C publishes nothing about `comcntr` thread-safety, so measurement is the only route.

Measured 2026-09-15 on WIN-11-2070 against **KAN** (`Srvr="WIN-11-2070";Ref="KAN"`, client-server).
Read-only: connects, reads metadata, runs `SELECT`. Never writes.

## Run it

```bash
dotnet build research/1c-adapter/com-threading-probe/com-probe.csproj -c Release
```

```bash
com-probe.exe <a|b|c|d|e> [configPath] [n] [rounds]
```

Config is the same shape `csharp-worker` uses (`{ baseName, connectionString }`); it defaults
to `research/1c-adapter/csharp-worker/config.local.json`. That file holds live credentials —
never commit it.

---

## Headline finding: the crash is ours, not 1C's

The adapter's long-standing "фатальные крахи финализатора" — and `oscript.exe` dying with
`AccessViolation` in `COMWrapperContext.Finalize → Marshal.InternalReleaseComObject` — is
caused by **letting the GC finalizer thread release 1C COM objects while other threads are
still calling into 1C**.

Clean A/B, identical parameters (4 sessions × 20 rounds, 160 queries):

| intermediate `Запрос`/`Выборка` RCWs | result |
|---|---|
| left to the GC (`COM_PROBE_LEAK=1`) | `0xC0000005` mid-run, no output |
| released on the creating thread | 4.20× speedup, **exit 0** |

Before the fix the process died after ~40 queries; with it, 160 queries across 8 sessions run
clean. Releasing them also nearly **doubled** throughput at k=8 (3.42× → 6.27×), because the
finalizer thread was contending with the workers.

Rule for any worker, in any language: **release every 1C COM object on the thread that
created it.** That includes the throwaway ones — `Запрос`, the query result, `Выборка`,
metadata collections, and every item pulled out of them.

---

## A — apartment

```
main thread        : managed=MTA native=MTA
thread-pool thread : managed=MTA native=MTA
new Thread default : managed=MTA native=MTA
new Thread w/ STA  : managed=STA native=MAIN_STA
connector type     : System.__ComObject
```

A plain .NET console process is **MTA everywhere** unless STA is forced. `comcntr` is
registered `ThreadingModel=Both` with **no** free-threaded marshaler (no
`CoCreateFreeThreadedMarshaler`, no `IMarshal` in the DLL), so the object is created directly
in the caller's apartment — the MTA — and COM does no marshalling and imposes no serialization.

`main.os:1649`'s comment calling this a "single-apartment процесс" is **wrong**.

## B — one connector object, N threads calling `Connect`

| run | serial wall | parallel wall | speedup |
|---|---|---|---|
| 1 | 13 481 ms | 2 828 ms | 4.77× |
| 2 | 10 927 ms | 3 668 ms | 2.98× |

Per-connect times in the parallel run were near-identical (~1.45–1.56 s), i.e. genuinely
simultaneous. **A shared `V83.ComConnector` does not serialize concurrent `Connect()` calls.**

## C — N connector objects vs one

No meaningful difference (~2.86 s wall either way). An early run suggested N connectors were
2.6× slower; re-running C before B removed the effect — it was warm-up ordering.

Also: 4 instances coexist fine (`distinct refs: 4`). The `TYPE_E_CANTLOADLIBRARY` failure in
`main.os:2239` is specifically **recreate-after-teardown**, not simultaneous creation.

## D — connect + query, cold each time

| k | wall ms | connect avg | query avg |
|---|---|---|---|
| 1 | 1 417 | 1 362 | 47 |
| 2 | 1 279 | 1 235 | 20 |
| 4 | 1 401 | 1 379 | 17 |
| 8 | 1 805 | 1 640 | 21 |

Query time is flat from k=1 to k=8; all the cost is `Connect` (~1.4 s), which degrades only
mildly under load. 8 concurrent sessions in one process complete in 1.8 s versus ~11 s serial.
No license errors at 8.

**`Connect` is the expensive thing — so pool and reuse sessions, don't spawn more threads.**

## E — pure query concurrency, sessions held open

Connections opened up front and excluded from timing. Query = `ВЫБРАТЬ ПЕРВЫЕ 500 Ссылка, Код,
Наименование` with every row walked, so COM actually marshals data. 20 rounds per session.

| k | serial wall | parallel wall | speedup | efficiency |
|---|---|---|---|---|
| 1 | 219 ms | 217 ms | 1.01× | 101% |
| 2 | 439 ms | 208 ms | 2.11× | 106% |
| 4 | 845 ms | 247 ms | 3.42× | 86% |
| 6 | 1 175 ms | 254 ms | 4.63× | 77% |
| 8 | 1 724 ms | 275 ms | 6.27× | 78% |

All exit 0. Per-query cost stays ~10–13 ms while concurrency rises 8×. Server-base query
work parallelises well inside a single process, holding ~78% efficiency at 8 threads.

---

## What this changes

1. **"One ComConnector per process" has no concurrency justification.** Its real basis is the
   recreate bug. One connector serves many concurrent sessions fine.
2. **Server bases can be read concurrently from one process** — ~6× at 8 sessions. The adapter
   currently leaves this entirely unused.
3. **Session establishment is the bottleneck**, not COM and not query execution. A session pool
   is worth more than any threading change.
4. **The finalizer crash is fixable in a C# worker** and is not fixable in oscript, because
   OneScript's `COMWrapperContext` releases on the GC finalizer thread by design. This is the
   single strongest technical argument for a C# worker on server bases.
5. File bases are untouched by all of this — the process is still the isolation unit there
   (see `RESEARCH.md`, multi-session poison).

## Not tested

- File bases under this harness (only KAN, client-server).
- Heavy register reads — the query here is a 500-row catalog fetch, not a cold
  `AccountingRegister` walk. Contention could differ.
- Writes. Prior work says same-type writes serialize on number allocation; unchanged here.
- The license-pool ceiling: 8 concurrent sessions worked, the limit was not searched for.
- Whether the same release discipline removes the *exit-time* AV in every case; it did here,
  across all five k values, but this is one machine and one base.
