# .NET ↔ 1C interop research — registration-free COM and typed interop

Second research pass. Continues `DOTNET_ONEC_CONCURRENCY_RESEARCH.md`; nothing from that
report is re-litigated here. Still architecture research — no production host was started.

Harness: `research/interop/` (`Activation.cs`, `Disp.cs`, `Program.cs`, ~1200 lines).
Modes: `act | matrix | mixed | typeinfo | cmp | err | conc | cleanup`.

Date: 2026-09-21. Machine: Windows 11 Pro 26200, x64, .NET 9.0.318.

---

## 0. Headline

1. **Global COM registration can be dropped entirely.** Loading an explicit
   `comcntr.dll` path and calling `DllGetClassObject` binds deterministically to that exact
   1C version. Proven by making a process run 8.3.18 while `HKLM\...\CLSID\{181E893D-…}`
   still pointed at 8.3.15. No `regsvr32` flipping, ever again.
2. **One process can host exactly one 1C version.** Not a policy choice — a hard OS
   constraint. Second version fails to load with win32 127.
3. **The "textless 1C error" does not exist.** It was a C# `dynamic` bug. The same errors
   carry full Russian/English text in `EXCEPINFO`; a hand-rolled `IDispatch` call reads them
   fine. `dynamic` crashes with `NullReferenceException` before it can show them.

That third point changes the production design more than anything in the first report.

---

## 1. Exact setup

| Item | Value |
|---|---|
| 1C versions installed | `8.3.15.1565`, `8.3.18.1289` — both x64 only, no x86 tree |
| comcntr paths | `C:\Program Files\1cv8\<ver>\bin\comcntr.dll` |
| CLSID | `{181E893D-73A4-4722-B61D-D604B3D67D47}` |
| Global registration at start | `InprocServer32` → `…\8.3.15.1565\bin\comcntr.dll`, `ThreadingModel=Both` |
| 1C server agent | `ragent.exe` from **8.3.15.1565**, ports 1540/1541 |
| Server base | `kansler` (KAN), PostgreSQL 1C 12 |
| File bases | `bilim` (`D:\1C\bilim`), `prime-holding` |
| .NET apartment | MTA (default `Main`, explicit `MTA` worker threads) |

**Global COM registration was never modified during this research.** No restore was needed.
Verified after the last run: `InprocServer32` still reads
`C:\Program Files\1cv8\8.3.15.1565\bin\comcntr.dll`.

The only thing written outside the repo was a temporary `comcntr.manifest` in each 1C `bin`
directory for the SxS test. Both were deleted; `bin` has no `*.manifest` left.

Restore command, recorded before starting in case it had been needed:

```powershell
# baseline, for reference only — never executed, registration was not touched
reg add "HKLM\SOFTWARE\Classes\CLSID\{181E893D-73A4-4722-B61D-D604B3D67D47}\InprocServer32" `
  /ve /t REG_SZ /d "C:\Program Files\1cv8\8.3.15.1565\bin\comcntr.dll" /f
```

---

## 2. A — activation matrix

Three activation routes × two versions × two bases. Each cell runs in its **own child
process**, because a loaded `comcntr` cannot be unloaded.

| how | ver | base | result |
|---|---|---|---|
| registry | 8.3.15 | bilim (FILE) | 1C says 8.3.15.1565 — **match** |
| registry | 8.3.15 | kansler (SERVER) | 1C says 8.3.15.1565 — **match** |
| registry | 8.3.18 | bilim | 1C says 8.3.15.1565 — **MISMATCH, request ignored** |
| registry | 8.3.18 | kansler | 1C says 8.3.15.1565 — **MISMATCH, request ignored** |
| direct | 8.3.15 | bilim | 8.3.15.1565 — match |
| direct | 8.3.15 | kansler | 8.3.15.1565 — match |
| direct | 8.3.18 | bilim | **8.3.18.1289 — match, registry still says 8.3.15** |
| direct | 8.3.18 | kansler | clean 1C error: version mismatch (see below) |
| sxs | 8.3.15 | bilim | 8.3.15.1565 — match |
| sxs | 8.3.15 | kansler | 8.3.15.1565 — match |
| sxs | 8.3.18 | bilim | **8.3.18.1289 — match** |
| sxs | 8.3.18 | kansler | clean 1C error: version mismatch |

The `registry` row is the whole problem in one line: **asking for 8.3.18 through the ProgID
silently gives you 8.3.15.** That is today's Connector.

### Proof the right DLL loaded

Three independent checks per run, not just "object creation succeeded":

1. `Process.Modules` filtered to `comcntr.dll`, with `FileVersionInfo`:
   `8.3.18.1289  C:\Program Files\1cv8\8.3.18.1289\bin\comcntr.dll`
2. exactly one `comcntr.dll` mapped — never two
3. 1C asked about itself: `NewObject("СистемнаяИнформация").ВерсияПриложения` →
   `8.3.18.1289`

Only check 3 is 1C's own answer, and it agrees with 1 and 2 in every cell.

### 8.3.18 client against the 8.3.15 server

```
hr=0x80020009 code=w0/0x80004005 op=Invoke/Connect src=V83.COMConnector.1
Client software code version does not match 1C:Enterprise server version
Different client and server versions (8.3.18.1289 - 8.3.15.1565),
client application: COM connection
```

Server bases need the exact platform version. File bases accept the newer platform. That
confirms the earlier "server = exact / file = floor" note, now measured rather than recalled.

### Timings and memory

| route | activation | Connect (file, cold) | Connect (file, warm) | Connect (server) | RSS after connect |
|---|---|---|---|---|---|
| registry | ~10–20 ms | — | 1.8–2.2 s | 1.3–1.4 s | 106–141 MB (server) |
| direct | **10–36 ms** | 23.9 s (first ever) | 2.0–4.0 s | 1.35 s | 306–315 MB (file) |
| sxs | 24 ms | — | 2.7 s | — | 314 MB |

Activation cost is noise next to `Connect`. The 23.9 s is the very first cold open of the
file base on that boot, not a property of the activation route.

### SxS / manifest route — works, but not worth it

`CreateActCtx` + `ActivateActCtx` + plain `CoCreateInstance` **does** activate the coclass
registration-free, and 1C reports the right version. One condition:

- manifest **must** sit in the same directory as `comcntr.dll` (i.e. inside
  `C:\Program Files\1cv8\<ver>\bin`). Writing it there needs admin.
- manifest in a temp directory with `lpAssemblyDirectory` pointing at the 1C `bin`:
  `CoCreateInstance` fails `0x8007007E` (`ERROR_MOD_NOT_FOUND`). Adding the bin directory
  to `PATH` does not help.

So SxS trades a machine-global registry write for a write into Program Files. No gain.
**The direct-path route is strictly better** — it needs no privileges and no files at all.

### Does PATH need changing?

**No** — tested with a control flag. `LoadLibraryEx(..., LOAD_WITH_ALTERED_SEARCH_PATH)`
already searches the DLL's own directory first, which is where all 69 sibling modules live.
Runs with `SetDllDirectory` disabled and a clean `PATH` succeed identically:

```
ONEC_NO_DLLDIR=1  → 1C reports 8.3.18.1289, match=True
```

`SetDllDirectory` is kept in the harness as belt-and-braces but is not required.

### Files that must be colocated

None. Nothing is copied next to the host. The host only needs the **path** to
`<1cv8>\<version>\bin\comcntr.dll`; the 1C installation stays where it is.

### Missing / bogus version

| case | behaviour |
|---|---|
| version not installed | `FileNotFoundException` immediately, from our own `File.Exists` check |
| path points at a non-COM DLL | `EntryPointNotFoundException: DllGetClassObject` |
| second version in same process | `LoadLibraryEx … win32 127 (The specified procedure could not be found.)` |

All three fail locally in milliseconds, with the requested path in the message. Compare the
registry route, where a wrong version is not an error at all — it just silently connects.

### Bitness

`comcntr.dll` PE machine = `0x8664` (x64) for both versions. Only the x64 1C tree is
installed here, so an x86 host cannot be tested and would in any case need the x86 1C
install. Bitness must match host process ↔ comcntr ↔ nothing else; it is a per-process
property, which reinforces the one-process-per-version conclusion.

### Two versions in one process — hard no

```
8.3.15: connector created in 9 ms, Connect OK, 1C says 8.3.15.1565
8.3.18: FAILED LoadLibraryEx … win32 127 (The specified procedure could not be found.)
```

Cause, measured: loading one `comcntr.dll` maps **69 further 1C modules** into the process,
all with unversioned base names —

```
accnt.dll addin.dll backbas.dll backend.dll bsl.dll core83.dll dbeng8.dll dcs.dll
dcscore.dll edb.dll enums.dll filedb.dll lockman.dll mngcore.dll nuke83.dll xdto.dll …
```

The second version's `comcntr` resolves its imports against the **already-loaded** modules
of the first version, finds an export missing, and the load fails. This is a Windows loader
property, not a 1C bug and not something an activation context can fix.

### Two versions in two processes — yes, simultaneously

Run concurrently:

```
proc A  pid 36896  8.3.15 → kansler (SERVER)  Connect 1352 ms  1C says 8.3.15.1565  rss 141 MB
proc B  pid 19288  8.3.18 → bilim   (FILE)    Connect 2844 ms  1C says 8.3.18.1289  rss 313 MB
```

Neither touched the registry. Neither interfered with the other. This is the
`OneC.Host.8.3.15.exe` / `OneC.Host.8.3.18.exe` target from the brief, working.

---

## 3. B — typed interop: what type information actually exists

Measured with `GetTypeInfoCount` / `GetTypeInfo` / `IProvideClassInfo::GetClassInfo`:

| object | `GetTypeInfoCount` | `IProvideClassInfo` | verdict |
|---|---|---|---|
| `V83.ComConnector` | **1** (TKIND_DISPATCH, guid `2ff2245e-c604-45bd-ac16-19b1f64bd9a4`) | no | typed interop possible |
| the connection returned by `Connect` | **0** | no | no type information at all |
| `Запрос` (and every other 1C object) | **0** | QI succeeds, `GetClassInfo` → `0x80004001` **E_NOTIMPL** | no type information at all |

The connector's type library, dumped in full — this is the entire typed surface 1C offers:

```
Connect(1), PoolCapacity(get/set), PoolTimeout(get/set), MaxConnections(get/set),
ConnectWorkingProcess(1), ConnectAgent(1),
RAgentPortDefault(), RMngrPortDefault(), LowBoundDefault(), HighBoundDefault()
```

So:

- **Generated interop assemblies are feasible for `V83.ComConnector` only** — 6 real methods
  and 3 properties. That covers `Connect` and nothing else.
- **Everything past `Connect` is untypeable.** Documents, catalogs, queries, selections,
  metadata: zero type information, `IProvideClassInfo` not implemented. There is no type
  library to import, generate from, or early-bind to. Option B from the brief does not exist
  beyond the first call.
- Therefore **late binding is mandatory**, and the only real question is *which* late binding.

Bonus finding, relevant to the pooling work in the first report: the connector exposes
`PoolCapacity`, `PoolTimeout` and `MaxConnections`. 1C has its own connection pool knobs we
have never set. Not tested here — flagged for the next pass.

### Member name resolution

`GetIDsOfNames` on a connection:

| name | DISPID |
|---|---|
| `NewObject` | 200001 |
| `СоздатьОбъект` | **NOT FOUND** |
| `Метаданные` / `Metadata` | 100072 / 100072 |
| `Документы` / `Documents` | 101536 / 101536 |
| `Справочники` / `Catalogs` | 101532 / 101532 |
| `ПолучитьCOMОбъект` | 202 |
| `NotAMember` | NOT FOUND |

Russian and English names resolve to the **same DISPID** — they are aliases, not different
members. Language is a non-issue at the IDispatch layer. The one trap is that the
connector-level API is English-only (`NewObject` exists, `СоздатьОбъект` does not), while the
business objects take both. A wrapper can cache DISPIDs per (object-class, name) and stop
paying `GetIDsOfNames` per call.

---

## 4. dynamic vs controlled late binding — head to head

Same 14 operations, same session, same base. `A = C# dynamic`, `C = hand-rolled IDispatch`.
(`B = generated typed interop` does not exist past `Connect`, per §3.)

### File base `bilim`, read path

| step | A dynamic | C IDispatch |
|---|---|---|
| activate | ok 23 ms | ok 0 ms |
| Connect | ok 2151 ms | ok 1882 ms |
| metadata (`Метаданные.Документы.Количество()`) | ok 26 ms — 327 types | ok **0 ms** — 327 types |
| discover doc type (6 probe queries) | ok 910 ms | ok **181 ms** |
| query create / property write / Execute / Выбрать / traverse | ok, 1 ms each | ok, 0 ms each |
| property read by name | ok | ok |
| property read `Get(0)` | ok | ok |
| **invalid query** | **FAIL `NullReferenceException`** | FAIL — *full 1C text* |
| **missing member** | FAIL `RuntimeBinderException` | FAIL `DISP_E_UNKNOWNNAME` |
| bad arg type (int into `Текст`) | ok — 1C accepts it | ok — 1C accepts it |

### File base `bilim`, write path

| step | A dynamic | C IDispatch |
|---|---|---|
| find source doc | ok 7 ms | ok 0 ms |
| doc create (clone + `Записать`) | ok 13593 ms — №0000-000015 | ok **2193 ms** — №0000-000016 |
| doc update | ok 55 ms | ok 43 ms |
| post (`РежимЗаписиДокумента.Проведение`) | — | **ok 3645 ms, posted** |

Both test documents carried the `AIBA_INTEROP_` marker and were deleted afterwards
(`cleanup` → `total removed: 2`). Nothing without that marker prefix is ever touched.

### Server base `kansler`

Same shape, plus the known KAN posting blocker:

| step | A dynamic | C IDispatch |
|---|---|---|
| metadata | ok 35 ms — 201 types | ok 0 ms — 201 types |
| invalid query | **FAIL `NullReferenceException`** | FAIL — full text |
| doc create (clone) | **FAIL `NullReferenceException`** | FAIL — `Failed to save: "Реализация товаров и услуг"!` |
| doc update | **FAIL `NullReferenceException`** | FAIL — `Failed to save: …` |
| post | — | FAIL — `Failed to post: "Реализация товаров и услуг"!` |

No documents were created on KAN (the writes failed); `cleanup` confirmed `total removed: 0`.

### Scored

| criterion | A dynamic | C IDispatch wrapper |
|---|---|---|
| correctness on the happy path | same | same |
| **exception quality** | loses the message entirely on 1C runtime errors; truncates connector errors | full text, always |
| **HRESULT visibility** | only when the runtime built a `COMException` | always, raw |
| **EXCEPINFO visibility** | none | `wCode`, `scode`, `bstrSource`, `bstrDescription`, `puArgErr` |
| crash behaviour | binder-level `NullReferenceException` on a whole class of errors | none observed |
| performance | 1.1×–6× slower per call (first-call binder cost dominates) | baseline |
| boilerplate | ~0 | **~260 lines once** (`Disp.cs`), then `Disp.Call/Get/Set` |
| thread safety | fine (MTA, session per thread) | fine (MTA, session per thread) |
| RCW release complexity | same — `FinalReleaseComObject` on the creating thread, mandatory | same |

Throughput, K threads × sessions, `Справочник.Номенклатура` 20 rows with index access:

| base | K × reps | dynamic | IDispatch |
|---|---|---|---|
| kansler | 4 × 30 | 29.4 op/s, 0 errors | **42.2 op/s**, 0 errors |
| kansler | 8 × 50 | 96.2 op/s, 0 errors | **103.3 op/s**, 0 errors |
| bilim | 4 × 30 | 20.3 op/s, 0 errors | **26.3 op/s**, 0 errors |

Speed is a side effect, not the reason to switch.

---

## 5. The "textless 1C error" — resolved, and it was us

The first report recorded a textless 1C error that blew up inside
`Microsoft.CSharp.RuntimeBinder.ComInterop.ExcepInfo.GetException`, and treated the empty
message as a 1C property.

It is not. Reading `EXCEPINFO` by hand shows the text is there the whole time:

```
dynamic : NullReferenceException, no message
IDispatch: hr=0x80020009  wCode=1001  scode=0x00000000  src="1C:Enterprise 8.3.15.1565"
           {(1, 27)}: Table not found "Документ.НетТакогоДокумента"
           ВЫБРАТЬ ЭтойКолонкиНет ИЗ <<?>>Документ.НетТакогоДокумента
```

The pattern is exact and reproducible across every error observed:

| error origin | `bstrSource` | `wCode` | `scode` | dynamic | wrapper |
|---|---|---|---|---|---|
| **1C runtime** (query, `Записать`, `NewObject` of unknown type) | `1C:Enterprise 8.3.15.1565` | **1001** | **0x00000000** | `NullReferenceException` | full text |
| **COM connector** (connect, credentials, infobase) | `V83.COMConnector.1` | 0 | **0x80004005** | `COMException`, message truncated | full text |
| **name resolution** (no such member) | — | — | — | `RuntimeBinderException` | `DISP_E_UNKNOWNNAME` |

So the rule is: **the C# binder crashes exactly when `EXCEPINFO.scode == 0` and
`wCode != 0`** — i.e. on every genuine 1C *runtime* error, which is the category that
matters most. It survives connector-level errors because those carry a non-zero `scode`.

Even where `dynamic` survives it loses information. Bad credentials:

```
dynamic : "User identification failed"
wrapper : "User identification failed Incorrect user name or password"
```

`ОбработкаПроведения` failures on KAN come through as `Failed to post: "…"!` and nothing
more — 1C does not put its inner reason into `EXCEPINFO`. That is a real 1C limit, unlike
the textless-error story, and remains the open part of the KAN blocker.

---

## 6. Error matrix — can the proposed `OneCException` fields be populated?

Measured on `bilim` via the wrapper:

| case | ms | hr | 1C code | source | message |
|---|---|---|---|---|---|
| bad credentials | 1694 | `0x80020009` | `w0/0x80004005` | `V83.COMConnector.1` | User identification failed. Incorrect user name or password |
| missing base | 535 | `0x80020009` | `w0/0x80004005` | `V83.COMConnector.1` | Infobase not found. Database file is missing `D:\1C\…\1Cv8.1CD` |
| garbage connection string | 469 | `0x80020009` | `w0/0x80004005` | `V83.COMConnector.1` | Invalid or missing parameters for connection to the Infobase |
| unknown server | **33257** | `0x80020009` | `w0/0x80004005` | `V83.COMConnector.1` | `server_addr=… descr=11001 No such host is known. line=1074 file=src\DataExchangeCommon.cpp` |
| missing member on connector | 0 | `0x80020006` | — | — | `DISP_E_UNKNOWNNAME` |
| invalid query text | 9 | `0x80020009` | `w1001/0x0` | `1C:Enterprise 8.3.15.1565` | `{(1, 1)}: Expression expected "ВЫБРАТЬ" <<?>>ЭТО НЕ ЗАПРОС` |
| unknown metadata object | 0 | `0x80020006` | — | — | `DISP_E_UNKNOWNNAME` |
| `NewObject` unknown type | 0 | `0x80020009` | `w1001/0x0` | `1C:Enterprise …` | `Type is not defined 'НетТакогоТипа'` |
| use released session | 1997 | `0x80131527` | — | — | `InvalidComObjectException` (ours, not COM's) |
| version mismatch (server) | — | `0x80020009` | `w0/0x80004005` | `V83.COMConnector.1` | Different client and server versions (8.3.18.1289 - 8.3.15.1565) |

Field-by-field verdict for the proposed abstraction:

| field | populated reliably? | from |
|---|---|---|
| `HResult` | **yes, always** | `IDispatch::Invoke` return |
| `OneCCode` | **yes** when 1C raised it | `EXCEPINFO.wCode` (1001 for runtime) + `scode` |
| `Message` | **yes** | `EXCEPINFO.bstrDescription`, full |
| `Operation` / `Member` | yes | supplied by the wrapper call site |
| `BaseName` / `Version` | yes | host context, plus `ВерсияПриложения` per session |
| `Source` | **yes, and it is the discriminator** — `V83.COMConnector.1` vs `1C:Enterprise <ver>` tells connector-layer from runtime-layer | `EXCEPINFO.bstrSource` |
| `OriginalException` | yes | |
| `IsRetryable` | **partly** — lock/timeout wording is detectable, but by substring matching on a localized message. Fragile. Needs a code table, not text. |
| `IsSessionFatal` | yes for `RPC_E_DISCONNECTED`, `CO_E_OBJNOTCONNECTED`, `RPC_E_SERVERFAULT` | HRESULT |
| `IsHostFatal` | yes for `TYPE_E_CANTLOADLIBRARY` | HRESULT — **not reproduced in this pass**, carried over from the first report |

Extra field worth adding: `ArgErrIndex` from `puArgErr` (the wrapper already carries it).

One operational note: **an unreachable server costs 33 s before it fails.** Any connect must
run under a caller-side timeout, because `Connect` has none.

---

## 7. Crash and lifetime behaviour

- No crash of any kind in this pass, in either style, including the `Get(0)` index access at
  K=4 and K=8 that produced `0xC0000005` in the first report. That crash was the leaked-RCW
  bug, already fixed; **the "never use `sel.Get(0)`" landmine from the first report is
  withdrawn** — index access is fine when every RCW is released deterministically.
- RCW discipline is unchanged and still mandatory: `Marshal.FinalReleaseComObject` on the
  creating thread, in reverse creation order, for every `Запрос` / result / `Выборка` /
  document object. The wrapper does not make this easier or harder.
- Using a session after `FinalReleaseComObject` raises `InvalidComObjectException` cleanly —
  a .NET-side guard, not a native crash. Good: double-release is survivable.
- The manual `Invoke` path allocates and frees its own `VARIANT` array, result variant and
  `EXCEPINFO` per call, with `VariantClear` in a `finally`. No leak observed across the
  400-op runs (RSS 142 → 151 MB, flat).

---

## 8. Process isolation — which option

**OPTION B: one `OneC.Host.exe` process per 1C platform version.**

Not for elegance. Because:

- two `comcntr.dll` versions cannot coexist in one process (win32 127, §2) — measured, and
  caused by 69 unversioned sibling DLLs, so no manifest, no `AssemblyLoadContext` and no
  activation context can work around it;
- a loaded `comcntr` can never be unloaded, so a process cannot switch versions later
  either;
- bitness is a per-process property, and 1C ships x86 and x64 trees.

Option C (per architecture/version pair) is the same thing as B in practice: version and
bitness together identify the install directory, and one process serves one directory. Treat
the process key as **(platform version, bitness)** and Option C collapses into B.

Option A is impossible, full stop.

Shape that follows from the measurements:

```
supervisor
  ├─ OneC.Host.exe --comcntr "C:\Program Files\1cv8\8.3.15.1565\bin\comcntr.dll"
  │     one connector, N warm sessions, K per the first report's per-base limits
  └─ OneC.Host.exe --comcntr "C:\Program Files\1cv8\8.3.18.1289\bin\comcntr.dll"
```

Version comes from config per base (server bases: exact; file bases: floor), the supervisor
starts one host per distinct version actually in use, and nothing is ever registered.

---

## 9. Explicit answers

**1. Can we eliminate global `regsvr32` switching?**
Yes. `LoadLibraryEx(path, LOAD_WITH_ALTERED_SEARCH_PATH)` + `DllGetClassObject` +
`IClassFactory::CreateInstance` binds to an exact `comcntr.dll` with no registry read, no
admin rights, no `PATH` change and no colocated files. Proven by running 8.3.18 while the
machine registration still pointed at 8.3.15.

**2. Can multiple 1C versions run simultaneously?**
Yes — in separate processes, concurrently, verified with 8.3.15→KAN and 8.3.18→bilim live at
the same time.

**3. Do they need separate processes?**
Yes, unavoidably. `win32 127` on the second `comcntr`, caused by 69 unversioned sibling
modules already mapped from the first version.

**4. Can we eliminate C# `dynamic` from the production path?**
Yes, and we should. It is not an optimization — `dynamic` throws away the error text on
every 1C runtime error, which is the majority of what production has to diagnose.

**5. If not, what should replace raw `dynamic`?**
A controlled late-binding wrapper over `IDispatch` with `PreserveSig` on `Invoke`, reading
`EXCEPINFO` directly (`Disp.cs`, ~260 lines). Typed/generated interop is not an option: only
`V83.ComConnector` has a type library; everything past `Connect` returns
`GetTypeInfoCount = 0` and `E_NOTIMPL` for `IProvideClassInfo`.

**6. Can we reliably surface textless 1C errors?**
There are no textless 1C errors. The text is in `EXCEPINFO.bstrDescription` every time; the
C# binder crashes before reading it whenever `scode == 0 && wCode != 0`. The wrapper returns
the full message, the 1C error code (`wCode` 1001 for runtime errors) and `bstrSource`, which
cleanly separates connector-layer from runtime-layer failures. The one genuine gap is 1C's
own terseness on posting: `Failed to post: "…"!` with no inner reason — a 1C limit, not an
interop one.

**7. What COM abstraction should the future `OneC.Host` use?**
- activation: explicit `comcntr.dll` path, one version per process (§8)
- calls: the `IDispatch` wrapper, with DISPID caching per (class, name)
- errors: `OneCException` as prototyped, with `Source` as the layer discriminator and
  `ArgErrIndex` added; `IsRetryable` driven by a code table rather than message substrings
- lifetime: unchanged — deterministic `FinalReleaseComObject` on the creating thread
- concurrency: unchanged — the per-base K limits from the first report

**8. What remains unknown?**
- Whether reg-free activation still works with the machine registration **absent**. Every
  test here ran with 8.3.15 registered. The coclass is not read from the registry, but the
  *type library* registration (`{98AC3B5B-…}`) may still matter for cross-apartment
  marshaling. Untested because unregistering was out of scope.
- Everything here ran MTA. STA behaviour under reg-free activation is untested.
- `TYPE_E_CANTLOADLIBRARY` was not reproduced this pass; `IsHostFatal` is carried over on
  trust.
- The connector's own `PoolCapacity` / `PoolTimeout` / `MaxConnections` properties — never
  set, never measured. Possibly relevant to the session-pool design.
- x86: no x86 1C install on this machine, so bitness is reasoned, not measured.
- KAN's `Failed to post` inner reason is still invisible over COM.
- DISPID caching is designed but not yet measured.

---

## 10. Not done, per the brief

No production `OneC.Host` was started. No WinUI integration, no `main.os` changes, no
business logic ported, no Connector changes, no IPC API, no deployment changes, no license
ceiling work. Global COM registration is exactly as it was found.
