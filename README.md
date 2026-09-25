# aiba-1c-arch

Work on the next-generation architecture for talking to 1C — read, write, edit, delete —
with connections that stay up under load instead of dying.

Private. Nothing here runs in production yet.

**Agents: start with `AGENT.md`**, then `MIGRATION_STATUS.md`, `MIGRATION_PLAN.md` and
`DECISIONS.md`. Those four files are the source of truth for the rewrite.

## What's in here

| Path | What |
|---|---|
| `research/com-threading-probe/` | Measured answers on `V83.ComConnector` threading, concurrency and the crash that kills workers. **Read its README first — it changes the design.** |
| `prototypes/winui-aiba-shell/` | Throwaway WinUI 3 shell prototype. Mock data only. Answers "what would a native desktop shell feel like", nothing more. |

## The findings that drive the design

From `research/com-threading-probe/README.md`, all measured against live KAN on WIN-11-2070:

1. **COM does not force a single connection.** `comcntr` is registered `ThreadingModel=Both`
   with no free-threaded marshaler, and a .NET host is MTA by default — so the object is
   created directly in the MTA, with no marshalling and no COM-level serialization.
2. **One connector object serves many concurrent sessions.** 8 sessions in one process:
   ~6.3x over serial, 78% efficiency. Query time stays flat (~10–13 ms) as concurrency rises.
3. **`Connect` is the cost** (~1.4 s), not COM and not query execution. Pool and reuse
   sessions; more threads without reuse buys little.
4. **The finalizer crash is self-inflicted.** Letting the GC finalizer thread release 1C COM
   objects kills the process with `0xC0000005` after ~40 queries. Releasing every COM object
   on the thread that created it: 160 queries clean, exit 0, and roughly twice the throughput.
   This is structural in OneScript (`COMWrapperContext.Finalize`) and fixable in C#.
5. **`comcntr` cannot be recreated in-process after teardown** (`TYPE_E_CANTLOADLIBRARY`).
   The registry has one slot per machine for the platform version — but that stopped being
   a constraint: explicit-path activation ignores the registry entirely (`DECISIONS.md` D04).

## Rules

- Never commit `config.json` / `config.local.json` — live 1C credentials.
- Never use `exchange=true` on a 1C write.
- 1C comment markers stay canonical: `AIBA_<KIND>_<id>: <fullName>`.
