# .NET ↔ 1C concurrency research — writes, file bases, pooling, failure isolation

Measured 2026-09-18 on WIN-11-2070. Harness: `research/onec-concurrency/`.

Bases:
- **KAN** — `Srvr="WIN-11-2070";Ref="KAN"`, client-server (PostgreSQL)
- **bilim** — `File="D:\1C\bilim"`, file base, 961 MB `1Cv8.1CD`

Every COM object is released on its creating thread. Nothing relies on the GC finalizer —
that was proven fatal in `research/com-threading-probe/`.

**Safety.** Writes only ever cloned an existing *posted* document into a NEW unposted draft
carrying marker `AIBA_RESEARCH_<run>`. No existing document was mutated. Posting was
attempted exactly once, in its own mode. 650 drafts on KAN and 170 on bilim were created and
all 820 were marked for deletion afterwards; a second cleanup pass returned `total marked: 0`
on both bases.

---

## Comparison table

|                        | Server (KAN)            | File base (bilim)        |
|---|---|---|
| Read K=1               | 62.9 op/s · p50 7 ms    | 134.2 op/s · p50 5 ms    |
| Read K=2               | 250.0 op/s · p50 5 ms   | 307.7 op/s · p50 4 ms    |
| Read K=4               | 150.9 op/s · p50 6 ms   | 434.8 op/s · p50 6 ms    |
| Read K=8               | 287.8 op/s · p50 8 ms   | 680.9 op/s · p50 5 ms    |
| Write K=1              | 10.6 op/s · p50 27 ms   | 6.5 op/s · p50 16 ms     |
| Write K=2              | 21.9 op/s · p50 25 ms   | 9.4 op/s · p50 28 ms     |
| Write K=4              | 42.9 op/s · p50 29 ms   | 10.1 op/s · p50 60 ms    |
| Write K=8              | 39.7 op/s · p50 61 ms   | not run (K=4 regressed)  |
| Connect cost (cold)    | 5.0 s                   | **50.9 s**               |
| Connect cost (warm)    | 1.1 – 4.2 s             | 1.7 – 5.8 s              |
| Warm session cost      | p50 5–6 ms              | p50 4–6 ms               |
| Peak RAM               | 689 MB @ K=8 (~65 MB/session) | **1810 MB @ K=8 (~200 MB/session)** |
| Peak CPU               | 15% (read K=8)          | 27% (read K=8)           |
| Failure isolation      | clean — see below       | clean — identical        |
| Recommended read K     | **4**                   | **4** (memory-bound, not CPU-bound) |
| Recommended write K    | **4** (same type) / **1–2** (mixed types) | **2** |

Write rows are the "same document type on every thread" pattern. The different-type pattern
is reported separately below because it behaves *worse*, which was not expected.

---

## A — Write concurrency

### A1. Same document type on every thread (KAN)

| K | ops | fail | wall | op/s | p50 | p95 | max | RSS | CPU |
|---|---|---|---|---|---|---|---|---|---|
| 1 | 10 | 0 | 939 ms | 10.6 | 27 | 668 | 668 | 176 MB | 3% |
| 2 | 20 | 0 | 915 ms | 21.9 | 25 | 554 | 638 | 243 MB | 4% |
| 4 | 40 | 0 | 933 ms | 42.9 | 29 | 409 | 616 | 369 MB | 8% |
| 8 | 80 | 0 | 2015 ms | 39.7 | 61 | 908 | 1267 | 612 MB | 8% |

Scales cleanly to K=4 (**4.0×**), then flattens and p50 doubles at K=8.

### A2. Different document type per thread (KAN)

| K | ops | fail | wall | op/s | p50 | p95 | max | RSS | CPU |
|---|---|---|---|---|---|---|---|---|---|
| 1 | 10 | 0 | 567 ms | 17.6 | 23 | 335 | 335 | 240 MB | 3% |
| 2 | 20 | 0 | 863 ms | 23.2 | 30 | 365 | 530 | 306 MB | 6% |
| 4 | 40 | 0 | 2154 ms | 18.6 | 41 | 1424 | 1635 | 428 MB | 5% |
| 8 | 80 | 0 | 3468 ms | 23.1 | 127 | 1071 | 2116 | 689 MB | 8% |

**Counter-intuitive and worth repeating: mixing document types is worse than hammering one.**
Throughput never exceeds ~23 op/s and p50 degrades 5.5× from K=1 to K=8. The plausible
reason is per-type metadata/object-model warm-up being paid repeatedly across threads rather
than amortised, but that was not isolated — it is an open question, not a conclusion.

### A3. Failures, locks, numbering

**Zero failures** across all write runs on both bases once the harness stopped using a doc
type that has no `Комментарий` attribute. Specifically observed:

- no lock errors
- no numbering-collision errors
- no deadlocks or timeouts
- no COM exceptions during writes

The old expectation that concurrent same-type writes would serialise on number allocation did
not reproduce. Writes overlapped and throughput rose.

### A4. Do writes stall unrelated reads? (KAN, mixed mode)

| scenario | read p50 | read op/s |
|---|---|---|
| reads alone, K=2 | 7 ms | 130.4 |
| reads K=2 **while** writes K=2 run | 6 ms | 32.9 |

Read latency is **not** affected — p50 actually 0.86× of the baseline. Read *throughput* falls
only because the wall clock is dominated by the slower write threads finishing. Writers in the
same run showed p50 26 ms.

**Writes do not stall unrelated reads.**

### A5. Posting

Posting on KAN **failed**, and the failure mode is itself a finding:

```
post -> ok=False 661 ms [other]
NullReferenceException: Object reference not set to an instance of an object.
  @ at Microsoft.CSharp.RuntimeBinder.ComInterop.ExcepInfo.GetException()
```

1C raised an error with **no text**, and the C# `dynamic` binder threw `NullReferenceException`
while trying to construct the exception from an empty `EXCEPINFO`. This is the same textless-error
class that `main.os` works around by reading `ЖурналРегистрации`; with `dynamic` it degrades
into an NRE carrying no diagnostic value at all. The process then died with `0xC0000005` at exit.

KAN has a known posting blocker, so this does not prove posting is broken in general — but it
does prove that **a `dynamic`-based host cannot surface textless 1C errors**, and that a failed
post leaves COM state bad enough to crash teardown. Write-concurrency under posting therefore
remains **unmeasured**.

---

## B — File base

### B1. Reads — the old "15× worse" claim does not reproduce

| K | ops | fail | wall | op/s | p50 | p95 | max | RSS | CPU |
|---|---|---|---|---|---|---|---|---|---|
| 1 | 20 | 0 | 149 ms | 134.2 | 5 | 6 | 18 | 334 MB | 6% |
| 2 | 40 | 0 | 130 ms | 307.7 | 4 | 8 | 16 | 547 MB | 12% |
| 4 | 80 | 0 | 184 ms | 434.8 | 6 | 10 | 21 | 959 MB | 25% |
| 8 | 160 | 0 | 235 ms | 680.9 | 5 | 8 | 105 | 1810 MB | 27% |

**5.07× at K=8, zero failures, p50 flat at 4–6 ms.** The prior result — parallel file-base access
being ~15× *worse* than serial — was measured before the RCW/finalizer fix and **should be
discarded**. With deterministic COM release, a file base reads *better* than KAN in raw op/s.

No exclusive-lock errors occurred at any K.

### B2. Writes — these genuinely do not scale

Same type:

| K | ops | fail | wall | op/s | p50 | p95 | max | RSS |
|---|---|---|---|---|---|---|---|---|
| 1 | 10 | 0 | 1541 ms | 6.5 | 16 | 1365 | 1365 | 434 MB |
| 2 | 20 | 0 | 2121 ms | 9.4 | 28 | 1295 | 1882 | 747 MB |
| 4 | 40 | 0 | 3955 ms | 10.1 | 60 | 2396 | 3405 | 1372 MB |

Different types:

| K | ops | fail | wall | op/s | p50 | p95 | max | RSS |
|---|---|---|---|---|---|---|---|---|
| 1 | 10 | 0 | 1497 ms | 6.7 | 17 | 1310 | 1310 | 493 MB |
| 2 | 20 | 0 | 1660 ms | 12.0 | 22 | 1303 | 1376 | 789 MB |
| 4 | 40 | 0 | 7242 ms | 5.5 | 25 | 1499 | **6986** | 1463 MB |

K=4 on mixed types is **slower than K=1** with a 7 s worst case. Still zero failures — it is
slow, not broken.

### B3. The real file-base constraint is memory, not concurrency

~200 MB of process RSS per session, versus ~65 MB on KAN. K=8 reads took the host to
**1810 MB**. The file engine loads in-process per session, so session count is bounded by RAM
long before it is bounded by contention.

Cold connect is the other file-base tax: **50.9 s** on a first connect, against 5.0 s for KAN.
Warm reconnects were 1.7–5.8 s.

---

## Session pool (KAN)

| strategy | op/s | p50 | first op | warmup | idle RSS | recreated |
|---|---|---|---|---|---|---|
| connect → query → release, per operation | **0.2** | 3828 ms | 4821 ms | — | 138 MB | — |
| pool of 1 | 60.5 | 5 ms | — | 3658 ms | 190 MB | 0 |
| pool of 2 | 135.1 | 5 ms | — | 1925 ms | 238 MB | 0 |
| pool of 4 | **400.0** | 6 ms | — | 3671 ms | 348 MB | 0 |
| pool of 8 | 279.1 | 6 ms | — | 10224 ms | 543 MB | 0 |

Connect-per-operation is **~2000× slower** than a pool of 4 on the same workload. A pool of 1
already buys ~300×. The knee is at 4; a pool of 8 regresses and costs 10 s to warm.

No session had to be recreated during any pool run.

---

## Failure isolation

Identical behaviour on KAN and on the file base:

| test | result |
|---|---|
| F1 wrong credentials | clean `COMException: User identification failed`, 5.0 s / 1.4 s |
| F2 non-existent base | clean `COMException: Infobase not found`, 2.8 s / 0.5 s |
| F3 connector after those failures | **still usable** — connected normally |
| F4 bad query on one session | that op failed; **all four sessions kept working** (3–12 ms) |
| F5 dispose one session mid-flight | siblings unaffected (3–4 ms) |
| F6 recreate the disposed slot from the same connector | **worked** — 882 ms (KAN) / 1588 ms (file) |
| F7 connector health at end | healthy on both |

A failed `Connect` does **not** poison `V83.ComConnector`. A broken session can be disposed and
replaced independently. This is a direct contradiction of the fear encoded in `main.os:2239` —
but note that comment is about nulling and recreating the **connector object**, which is a
different operation and was not retested here.

---

## Architecture questions

1. **Can one .NET process safely host multiple concurrent 1C sessions?**
   Yes. 8 concurrent sessions on both a server and a file base, thousands of operations, zero
   failures — provided every COM object is released on its creating thread.

2. **Can one `V83.ComConnector` safely serve those sessions?**
   Yes. All runs used a single connector. It survived bad credentials, a missing base, a
   failing query, and a mid-flight session dispose, and it could still mint new sessions afterwards.

3. **Should reads use a pool?**
   Yes, emphatically. Connect-per-operation is ~2000× slower than a pool of 4. This is the
   single largest effect measured in this study.

4. **Should writes use the same pool?**
   Not proven either way. Writes and reads did not interfere when run on separate sessions,
   but a shared pool was never tested. Do not assume it is safe yet.

5. **Should writes have a separate pool/lane?**
   The evidence leans yes, for a reason unrelated to safety: writes have very different latency
   (p50 25–60 ms with multi-second tails) from reads (p50 5 ms). Sharing a pool would let a
   write tail block a read. Isolation is a queueing argument, not a correctness one.

6. **Should writes intentionally stay serial?**
   No — not on a server base. Same-type writes scaled 4.0× to K=4 with zero failures and no
   numbering or lock errors. On a file base, writes should stay at K≤2.

7. **Do FILE bases require a special architecture?**
   Yes, but not for the reason previously believed. Reads scale *fine* (5.07× at K=8). The
   constraints are **memory** (~200 MB/session) and **cold connect** (50.9 s), plus writes that
   stop scaling past K=2. So: fewer, longer-lived sessions, and a warm-up strategy.

8. **Is process-per-file-base still justified?**
   **The old justification is gone.** It rested on the 15× parallel-degradation measurement,
   which does not reproduce. A separate process may still be justified by memory isolation and
   by blast radius, but that is now a design choice, not a measured necessity. It needs an
   explicit re-decision rather than inheritance.

9. **Can a broken session be recycled independently?**
   Yes — F5/F6 disposed one session and recreated it from the same connector while its siblings
   kept serving, on both base kinds.

10. **Under what condition would the whole OneC.Host need to restart?**
    Nothing in these tests forced a host restart. Two known conditions remain, neither retested
    here: recreating the connector object after nulling it (`TYPE_E_CANTLOADLIBRARY`), and the
    COM state left behind by a failed *post*, which crashed the process at exit. A host that
    never nulls its connector and never posts would not have needed recycling in this study.

---

## Landmines found while building the harness

- **`sel.Get(0)` / index-based column access crashes the process** with `0xC0000005` at K≥2,
  inside `IDispatchInvoke`. Reading columns **by name** is stable. K=1 does not reveal it.
- **`МАКСИМУМ(Комментарий)`** is invalid in 1C query language (unlimited-length string);
  it silently reduced a doc-type scan from 8 types to 1.
- **Doc types without a `Комментарий` attribute** cannot carry a marker and therefore cannot be
  cleaned up. They must be excluded from any write test, not just handled.
- **Textless 1C errors become `NullReferenceException`** under `dynamic`, with the stack inside
  `ExcepInfo.GetException()` and no usable message.

---

## Not established

- Posting concurrency — blocked by KAN's posting blocker.
- Shared read+write pool behaviour.
- File-base K=8 writes — not attempted after K=4 regressed.
- Whether the different-type write penalty is metadata warm-up or something else.
- Multi-process file-base behaviour — only multi-session within one process was tested.
- The connector-recreate (`TYPE_E_CANTLOADLIBRARY`) path.
- Long-run stability; the longest run here was seconds, not hours.

No migration recommendation is made at this stage.
