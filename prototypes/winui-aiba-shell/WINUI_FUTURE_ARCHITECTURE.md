# A possible future architecture for a native AIBA Connector

**Status: exploratory.** This is a sketch produced alongside a UI prototype, not a decision
and not a final architecture. It exists so the trade-offs can be argued about concretely.
Nothing here has been validated against the production system.

---

## Why the question is worth asking

The current connector is Tauri + React + Rust with an oscript adapter underneath. That stack
delivers the product today. The parts that hurt are not the UI framework; they are:

- **Process supervision.** The product is really a supervisor of a small fleet of processes:
  the adapter router, N oscript workers each holding exactly one `V83.ComConnector`, the 1UZ
  adapter, a bank connector, a browser automation host. Today that supervision is spread
  across Rust commands, JS state and a lot of implicit knowledge.
- **COM.** `V83.ComConnector` is STA. Real parallelism means more processes, not more threads.
  The host language for COM interop matters.
- **Observability.** Operators need to see which base is on which worker, what the queue looks
  like, and why a base is stuck — right now that means reading logs.
- **Density.** The product is an operations console for accountants and integrators. Web
  layout primitives fight that; native list virtualisation and the Windows type ramp do not.

A native .NET shell is interesting precisely because .NET has first-class COM interop and a
mature process/job-object story, and because WinUI gives the density for free.

It is also a real cost: a rewrite, a second UI codebase during transition, and the loss of
web-stack familiarity. This document assumes that cost is understood.

---

## Shape

```
                    WinUI 3 (Presentation)
                    pages, view-models, converters
                              |
                    Application services
       IOneCService  ISyncService  IProcessSupervisor  IBankService
       ILogService   ISystemMetricsService  ISettingsService
                              |
                        Domain model
        InfoBase  Worker  SyncRun  Lane  Document  Statement
                              |
                       Infrastructure
                              |
                    Process Supervisor (in-proc)
                              |
        +---------+-----------+-----------+-------------+
        |         |           |           |             |
   1C COM host  Reports    Bank        Browser      Other
   (per base    worker     connector   automation   integrations
    or per
    lane)
```

The prototype already has the top two layers, with the bottom two replaced by `Mock*`
implementations. That is the point: the seam is the interface list, and it survives.

---

## Layers

### Presentation — WinUI 3

Pages and view-models. No knowledge of COM, HTTP, processes or files. Everything it renders
arrives as a domain object or an event.

The prototype keeps code-behind because it is throwaway. A real build would put a view-model
per page and bind with `x:Bind`, which also gets compile-time checking of every binding.

Two rules worth keeping from the prototype:

- Models stay UI-free. A single `StatusKind` enum drives every colour in the app through one
  converter, so re-theming is one file and states cannot drift between screens.
- Tables are a header `Grid` plus a virtualised `ListView`. It costs a duplicated column
  definition and buys full control plus no third-party dependency.

### Application services

The seam. Each one owns a use case, not a technology:

| Service | Responsibility |
|---|---|
| `IOneCService` | the set of bases, their connection state, connect/disconnect, schema read-through |
| `ISyncService` | plan a sync, slice it into lanes, run it, report progress, stop it |
| `IProcessSupervisor` | start, watch, restart and stop every child process |
| `IBankService` | bank connections, statement pulls, the state that needs a human |
| `ILogService` | one structured stream from every component |
| `ISystemMetricsService` | machine and process metrics |
| `ISettingsService` | durable configuration |

These are the async, cancellable boundary. Everything below may block; nothing above may.

### Domain

`InfoBase`, `Worker`, `SyncRun`, `Lane`, `Document`, `Statement`, `BankConnection`, plus the
value objects that already exist implicitly in the current system — the AIBA marker format,
the COM version pin, the keyset cursor. Pure C#, no framework types, unit-testable.

This is where the knowledge that currently lives in comments and in people's heads should go.

### Infrastructure

Everything that touches the world. The interesting part is the supervisor.

---

## Process supervisor

The supervisor is the reason to do this at all. Sketch of what it would own:

- **Lifecycle.** Start children with an explicit command line and environment. Put every child
  in a Win32 **job object** with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` so a crashed or killed
  shell cannot orphan a worker holding a COM connection to a live 1C base.
- **Health.** A cheap probe per child (an HTTP ping to the adapter port, a heartbeat on a pipe)
  with a restart policy: crash, N failed probes, or RSS over budget. Backoff, and a restart
  counter surfaced in the UI — the prototype's Processes page shows exactly this.
- **Placement.** Which base runs on which worker. Today this is implicit; it should be a
  decision the supervisor makes and the UI can show. COM being STA means the mapping matters:
  one connection per process, and a base pinned to a COM version needs a worker registered for
  that version.
- **Log capture.** Child stdout/stderr, tagged with the child identity, into the same ring
  buffer the UI tails and the crash reporter ships.
- **Draining.** Stop means "finish the in-flight read, then exit", not "kill". Recycle a worker
  between reads, never mid-read.

### COM host

Two options, and the choice is not obvious:

1. **Keep oscript workers.** Least disruption: the adapter is ~21 k lines of working oscript
   with years of landmines already defused. The supervisor just manages them better.
2. **A .NET COM host per worker.** A small C# process that holds one `V83.ComConnector` on an
   STA thread and speaks a typed protocol to the shell. Better debugging, better error
   surfacing, real types — but it means porting the adapter's accumulated knowledge, which is
   the expensive part and the part most likely to regress.

A hybrid is plausible: new work in a .NET host, existing read/write paths left in oscript
until each is replaced with tests proving parity. The supervisor should not care which kind of
child it is managing — that is an argument for defining the child protocol first.

### IPC

Between the shell and its children: named pipes or a local HTTP port (the adapter already uses
a port, which is why the Processes page shows one). Whatever it is, it needs to carry
cancellation and streaming progress, because a cold read is minutes long and must be stoppable.

### Storage

Synced 1C data does not belong in the UI process's memory. A local store — SQLite is the
obvious candidate — gives the reports and reconciliation screens something to query without
going back to 1C, and gives sync a durable cursor to resume from.

### Background work

Sync, polling and bank pulls are long-running and must survive the window being closed. In a
native shell that means the work lives in the supervisor and the UI attaches to it, rather
than the work being owned by whatever page happens to be open. The prototype hints at this:
`SimulationEngine` is a single heartbeat that pages subscribe to, not per-page timers.

### Banks and browser automation

Browser automation is the most failure-prone component and should stay in its own process, out
of the UI's address space, with the supervisor restarting it. Signing stays where it is — in a
daemon that holds the token — and the shell only ever asks for a signature.

---

## Migration, if it ever happened

Not proposed here, but the order that would minimise risk:

1. Build the supervisor and the child protocol **inside the existing app** first, managing the
   existing oscript workers. That is valuable on its own and independent of the UI framework.
2. Move observability (process tree, log stream, sync progress) onto that supervisor.
3. Only then decide whether the shell is WinUI or stays Tauri. The supervisor work is the
   expensive part and it is framework-agnostic.

That ordering means the UI decision can be deferred until the part that actually hurts is
fixed — which may well be the honest conclusion of this whole exercise.

---

## Open questions

- Is the pain actually in the UI framework, or entirely in supervision and observability? If
  the latter, a native shell is a large cost for a small win.
- Uzbek-first localisation and the existing design language would have to be rebuilt.
- Two UI codebases during any transition. Who maintains both?
- Auto-update: the current Tauri updater works. A native shell needs an equivalent.
- Does a .NET COM host actually beat the oscript adapter, measured, on the reads that matter?
  That is testable today, independent of any UI work, and should probably be tested before
  anything else in this document is taken seriously.
