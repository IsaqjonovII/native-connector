# S12 shared-dev test — runbook

The Sync engine against the **Development** backend/1c (`aiba-1c-dev.aiba.uz`). Approved 2026-10-01
(dev/staging only, one test company, one test record, bilim first). **Not** a production approval.
Every check reads the stored rows back and compares them with 1C — a count the backend reports is
never accepted as proof (v2 reports 2 stored when 1 is).

**Hard guards (in code, not just here):** the target is the dev host only (`DevBackend.Root`;
`*.aiba.group` refused); the engine refuses to start unless the record's name **and** odataName
start with `SYNC-TEST` and its `connection_state` is `offline` (no connector socket on it — D-1);
`allowWithOldConnector` and table rebuilds are refused on dev; the token is the app's Development
session, never written by these tools; one base, one record per run.

## 0. Before (developer, ~2 min)

```powershell
cd D:\aiba\1c-arch
$env:AIBA_CONNECTOR_DATA = "$env:LOCALAPPDATA\AIBA\ConnectorDev"        # Development, separate from your prod session
& .\src\OneC.Desktop\bin\Release\net9.0-windows10.0.19041.0\win-x64\AIBA.Connector.exe
```
Sign in with your **dev** login in that window (it shows DEVELOPMENT), then close it. Then:
```powershell
. .\tools\s12-dev.ps1
Dev companies                     # read-only: pick the DEDICATED test company (never a client's)
```

## 1–3. Record (the only write before syncing)

```powershell
Dev create-record --company <companyId> --base bilim     # → SYNC-TEST bilim <stamp>, odataName SYNC-TEST-bilim-<stamp>
Dev check --record <recordId>                             # must print OK … connection_state offline; lists bindings
New-S12Bases; New-S12Config -Record <recordId> -Faults
```
The odataName matches no local 1C base, so no old Connector ever picks the record up. A reused
record must still be `SYNC-TEST…` and offline — the engine refuses otherwise.

## 4–9. First sync and read-back (catalog, chart, documents, register)

| table | 1C size (2026-10-01) | why |
|---|---|---|
| `ChartOfAccounts_Хозрасчетный` | small | chart (code key) |
| `Catalog_Банки` | 919 | catalog, several hundred rows |
| `Document_ПоступлениеТоваровУслуг` | 68 | documents (+ the test document) |
| `AccountingRegister_Хозрасчетный` from 2025-08-01 | ~49 lines | recorder-keyed movements |

```powershell
$p = Start-Sup; Http-Stats -Clear | Out-Null
$first = Wait-Idle; $first; Http-Stats; Mem          # snapshot time, p50/p95, MB, RAM
Stop-Sup; Verify first                               # every stored row vs 1C: PASS required
```
`Verify` checks per table: 1C COUNT, canonical rows, backend rows read back, missing, stale/extra,
duplicate keys, wrong partition, every field of every row (numbers by value), unmapped orgs.

## 10. Document lifecycle (test-owned, `AIBA_REWRITE_S12_…` on bilim)

```powershell
$p = Start-Sup
Doc create; Doc post;   Wait-Idle; Stop-Sup; Verify posted      # document + its Хозрасчетный lines
$p = Start-Sup; Doc unpost; Wait-Idle; Stop-Sup; Verify unposted  # lines gone, document kept
$p = Start-Sup; Doc change; Doc post; Wait-Idle; Stop-Sup; Verify reposted  # same keys, new amounts
$p = Start-Sup; Doc delete; Wait-Idle; Stop-Sup; Verify deleted   # document and lines gone
```
`Doc` prints 1C's own view after each step (posted, lines, sum) into `lifecycle.txt`. Event →
backend latency: the first upload call after each step in `Http-Stats` (timestamps) minus the step's end.

## 11. Multi-org (only if the test company has bindings)

`Dev check` lists bindings. With none, everything goes to the connection's shared partition — the
routing path is then covered by the local S10 test (KAN, 2 organisations). With bindings: rerun
`Verify` — it builds the expected partition of every row from dev's real bindings and reports
`wrong partition`; unbound organisations are listed as waiting, never written to shared.

## 12. Failure tests (small, non-destructive; faults are injected locally, never sent)

```powershell
$p = Start-Sup
Faults -BadToken 1;  Doc create; Doc post; Wait-Idle; Http-Stats   # a real 401 from dev → one refresh → retried (statuses show 401x1 then 200)
Faults -Fail503 3;   Doc unpost; Wait-Idle                         # queued retry, no dead letter
Faults -FailNetwork 2; Doc post; Wait-Idle                         # same
Edge POST '/v1/sync/bases/bilim/pause'; Doc change; Start-Sleep 20; (Status).pendingWork   # paused: nothing sent
Edge POST '/v1/sync/bases/bilim/resume'; Wait-Idle
Faults -CrashOnComplete; Doc post; Start-Sleep 30; $p.HasExited     # dies after upload, before local completion
$p = Start-Sup; Wait-Idle                                           # restart: the item runs again, idempotent
Stop-Sup -ErrorAction SilentlyContinue; Doc change; Doc post; $p = Start-Sup; Wait-Idle   # changes while down are caught up
Faults -Reject400 1; Doc change; Doc post; Start-Sleep 30; Status    # validation → dead letter, base keeps going
Edge GET '/v1/sync/bases/bilim/dead-letters'                       # then retry it:
Edge POST "/v1/sync/dead-letters/<id>/retry"; Wait-Idle
Stop-Sup; Verify failures                                           # still exactly 1C
```
Old Connector presence: `Dev check` reads `connection_state`; the engine refuses to start on
anything but `offline` and pauses itself if it changes (D-1, unit + local S13 tests). Not exercised
against a live old Connector on dev — that would mean two connectors on one oneCId.

## 13. Cleanup

```powershell
Doc cleanup                     # deletes AIBA_REWRITE_S12_ documents on bilim only, lists what is left (0)
Stop-Sup
```
The dev record and its rows may stay as the test record for later runs (it is `SYNC-TEST`, no
one reads it). Deleting it purges its data on dev: only by the developer, in the app's Infobases.
`$Work` (`%LOCALAPPDATA%\AIBA\ConnectorDev\s12`) holds sync.db and the bilim base file — delete it
when done.

## 14. Success criteria

Every `Verify` PASS (0 missing, 0 stale/extra, 0 duplicate keys, 0 wrong partition, 0 wrong
content); the lifecycle steps show the movements appear, vanish, change amounts on the same keys,
and vanish with the document; every failure test converges with `Verify` PASS; no dead letter left
except the one created on purpose and retried; numbers recorded in `measurements/sync-s12-dev/`
and `DOTNET_REWRITE_PERFORMANCE.md` (upload rows/s, MB/s, HTTP p50/p95, snapshot rows/s, event →
backend latency, supervisor / host RAM, CPU, retry rate) next to the isolated local result
(116 759 rows / 272.6 MB / 71.8 s ≈ 1 625 rows/s).

## 15. Abort immediately (stop, report, change nothing)

Stored rows ≠ canonical 1C rows; duplicate canonical keys; stale recorder movements; a row in
another organisation's partition; a delete/reconcile removing unrelated rows; an unexplained 5xx
pattern; dev behaving against the mapped Python v2 contract; anything that would need production.
