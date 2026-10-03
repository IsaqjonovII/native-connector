# MIGRATION_PLAN.md — ordered remaining work

Work top to bottom. Each milestone ends with: tests green, measurements recorded, status and
decisions updated (`AGENT.md`). Blocks are small on purpose — "rewrite everything" is not a
milestone.

---

## Now: Sync (approved 2026-09-30, D42; one canonical engine since 2026-10-02, D48)

Work `SYNC_IMPLEMENTATION_PLAN.md` S0 → S15 in order, each block's stop conditions apply.
Local bases + `StubSyncTarget` only until S12; S12 writes to the shared dev backend only after
explicit approval; no production sync until S15 is green and approved (D-2).

**State 2026-10-01:** S0–S15 complete locally (stub, local bases, isolated local backend/1c);
Release gate met (two consecutive full runs, D45). **Next: S12 on shared dev** — approved, blocked
only on the developer signing in to Development and naming the dedicated test company; then
`DEV_SYNC_TEST_RUNBOOK.md` end to end. After it: S14 visual approval (the developer), then the
production decision (D-2). Backend findings to hand over: `BACKEND_SECURITY_FINDINGS.md` and the
backend/1c v2 limits (silent drop, no register purge) in MIGRATION_STATUS known limits.

**State 2026-10-03:** naming cleanup done (D48, one canonical Sync), the parent-crash test isolation
widened (D49, gate 417/417 twice), Sync always on for connected bases (D50, still the local test
target). **Old Connector reverse-engineered** (read-only, 2026-10-03):
`OLD_CONNECTOR_DEEP_AUDIT.md`, `OLD_CONNECTOR_FEATURE_MAP.md`, `OLD_CONNECTOR_HIDDEN_FEATURES.md`.
Before designing the remaining Connector features, the developer decides from those docs what to
preserve, expand, replace or drop; the audit's §18 lists what needs a second, deeper look first
(public reachability of the unauthenticated connector hub, `_postCreateMethod` / `hard:true` use,
HR `exchange=true` postings, the bank-signer relay). No new architecture until that review.

## Where the plan stood (2026-09-25)

Every approved milestone is built and verified (MIGRATION_STATUS, final pass 2026-09-25).
Desktop screens approved and the first checkpoint committed (2026-09-25). What is left needs
the user: the real sync target + credentials (5.9) and WER LocalDumps (machine-wide) for a
heap-corruption dump. The old-adapter parity run is no longer blocked — the comcntr
registration came back and the spot check ran (19/19). No Milestone 6 is approved.

## Milestone 2 — foundation completion — DONE 2026-09-23

### 2.1 Write vertical slice — DONE 2026-09-23 (see MIGRATION_STATUS)

### 2.2 Several active bases in one host — DONE 2026-09-23 (see MIGRATION_STATUS)

### 2.3 Long churn — DONE 2026-09-23 (verdict: DECISIONS D24)

### 2.4 Multi-version host spawning + recycling — DONE 2026-09-23

- A supervisor process that never loads comcntr itself, turns `HostPlan` into running
  `OneC.Host serve` processes (one per host key, explicit `--comcntr`), and passes each its
  bases over stdin — credentials never touch disk.
- A minimal line-JSON control channel over the host's stdin/stdout (`ping`, `stats`, `read`,
  `quit`) — **diagnostic only, not the IPC contract** (milestone 3 decides that).
- Health: detect a dead host and restart it with backoff; count restarts.
- Recycling (D24): restart a host that is idle and above a working-set threshold.
- Verify: 8.3.15 host + 8.3.18 host from one plan at once, each proving its version; kill one,
  it comes back and serves again; recycle triggers and memory returns to baseline.
- Measure: per-host baseline, total for the plan, supervisor's own cost, restart time.

Open side question from 2.1: the cold first write on the file base took 46–157 s once and was
not seen again in `grow`. Later file-base stalls of 10–45 min (MIGRATION_STATUS 5.7) are the
likely same phenomenon; still OPEN, cause unknown.

## Milestone 3 — IPC — DONE 2026-09-23 (see MIGRATION_STATUS)

Transport decided: **named pipes inside, HTTP at the edge** (DECISIONS D28).

- 3.1 Contract document `IPC_CONTRACT.md`: message envelope, operations, error model,
  deadlines, cancellation, backpressure, HTTP mapping.
- 3.2 `OneC.Ipc` library (no COM): contract types + length-prefixed frame codec.
- 3.3 Host pipe server, per-user ACL, multiplexed requests; stdin kept only for config and
  lifetime.
- 3.4 Supervisor pipe client, replacing the diagnostic stdin/stdout command channel.
- 3.5 HTTP edge on the supervisor (loopback, token).
- 3.6 Tests + measurements: end-to-end latency edge → pipe → 1C, supervisor RAM with Kestrel.

Details:
- Contract for read, write, post, delete, health, metrics; error model maps `OneCException`
  fields 1:1.
- Cancellation and timeouts end to end (1C `Connect` to an unreachable server takes ~33 s).
- Backpressure: requests queue inside the budget, never unbounded.

## Milestone 4 — WinUI integration — DONE 2026-09-24 (visually approved 2026-09-25)

Starts the supervisor (`OneC.Supervisor run`), reads port + token from its ready line, and
talks only to the HTTP edge (D28). Carry-over from 3: bulk reads should return GUIDs instead
of `Строка()` presentations (0.9 ms/row, P §3).

- Real WinUI 3 app shell (not the prototype) that starts the supervisor and talks IPC.
- Screens: infobases with live host/session state, read browse, write/post status, logs,
  resource view (RAM per host, sessions per base).
- Visual approval from the user before anything UI-related is committed.

Blocks:

| block | scope |
|---|---|
| 4.0 | Read-path carry-over: measure reference-rendering options, make the fast one the path |
| 4.1 | Supervisor `run` takes bases over stdin (no credentials on disk); edge `/v1/events` + bounded request log |
| 4.2 | `src/OneC.Desktop` (WinUI 3, unpackaged, self-contained): DPAPI base store, supervisor launcher, edge client |
| 4.3 | Screens: Infobases, Browse, Activity, Resources |
| 4.4 | Visual check: screenshots at normal and narrow widths, light and dark; fix layout; send to the user |
| 4.5 | Measure the app and the whole stack idle / active; update docs |

## Milestone 5 — subsystem migration from the old Connector — 5.1–5.9 DONE (see MIGRATION_STATUS; 5.1 live parity spot check DONE 2026-09-25, 5.9 real target PARKED), 5.10 out of scope

One subsystem at a time. Each block: map the old behaviour in `main.os`, build the .NET
equivalent, prove parity against the old adapter on the same base, measure, then cut over
behind a switch. The old Connector stays the default until a block is proven.

Order (dependencies first, highest value next):

| block | scope |
|---|---|
| 5.1 Infobase config + connection lifecycle | base list, versions, credentials storage, health |
| 5.2 Catalog reads | Номенклатура, Контрагенты, Организации etc., keyset paging |
| 5.3 Document reads | lists, by period, by ref, table parts |
| 5.4 Register reads | accounting / accumulation registers, period as parameter |
| 5.5 Bulk / cold reads | XDTO bulk export path |
| 5.6 Change feed | 1C event log |
| 5.7 Document writes | bank docs first, then sales/purchase, idempotency + markers |
| 5.8 Posting | per-type, with the KAN blocker handled explicitly |
| 5.9 Cloud sync pipeline | poller, hash diff, upload to backend (the 5.9 engine was replaced by Sync, D42, and removed 2026-10-02, D48) |
| 5.10 Bank + other integrations | OUT OF SCOPE (user, 2026-09-25, D40) — stays in the cloud bank service / bank-connector |

## Parked (not scheduled)

- ~~Reg-free activation with no machine registration~~ — resolved by D29 (vtable Connect,
  suite green with no registration).
- x86 hosts (no x86 1C install to test on).
- `TYPE_E_CANTLOADLIBRARY` reproduction.
- License ceiling research.
