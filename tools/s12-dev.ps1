# S12 shared-dev test helpers (DEV_SYNC_TEST_RUNBOOK.md). Dot-source:  . D:\aiba\1c-arch\tools\s12-dev.ps1
# Development backend only (aiba-1c-dev.aiba.uz, enforced in OneC.Supervisor DevBackend); the token is
# the Development session the app keeps in $DevData. Nothing here holds or prints a token.
$Repo    = 'D:\aiba\1c-arch'
$DevData = Join-Path $env:LOCALAPPDATA 'AIBA\ConnectorDev'          # the app's data folder for the dev sign-in
$Work    = Join-Path $DevData 's12'                                  # sync.db, sync.json, bases (outside the repo)
$Out     = Join-Path $Repo 'measurements\sync-s12-dev'              # reports kept with the repo
$Sup     = Join-Path $Repo 'src\OneC.Supervisor\bin\Release\net9.0\OneC.Supervisor.exe'
$HostExe = Join-Path $Repo 'src\OneC.Host\bin\Release\net9.0\OneC.Host.exe'
$DevBench = Join-Path $Repo 'research\sync-spikes\DevBench\bin\Debug\net9.0\DevBench.exe'
$Port    = 57741
New-Item -ItemType Directory -Force $Work, $Out | Out-Null
if (-not (Test-Path "$Work\edge-token.txt")) { [guid]::NewGuid().ToString('N') | Set-Content "$Work\edge-token.txt" }   # local edge only
$EdgeToken = (Get-Content "$Work\edge-token.txt").Trim()
$Bases = "$Work\bases.json"

# bilim only, from the gitignored bases.local.json (1C test-base password; stays outside the repo).
function New-S12Bases {
    $all = Get-Content "$Repo\bases.local.json" -Raw | ConvertFrom-Json
    ConvertTo-Json -InputObject @($all | Where-Object Name -eq 'bilim') -Depth 5 | Set-Content $Bases -Encoding utf8
}

# The first-run table set (DEV_SYNC_TEST_RUNBOOK.md §4); $Record = the test record id from `create-record`.
function New-S12Config([string]$Record, [switch]$Faults) {
    if ($Record -notmatch '^[0-9a-f]{24}$') { throw "record id must be the 24-hex id printed by create-record" }
    $cfg = [ordered]@{
        db = "$Work\sync.db"; target = 'dev'; session = $DevData; faults = [bool]$Faults
        bases = @(@{ name = 'bilim'; connectionId = $Record; tables = @(
            @{ table = 'ChartOfAccounts_Хозрасчетный'; name = 'Хозрасчетный'; family = 'chart'; isMovement = $false },
            @{ table = 'Catalog_Банки'; name = 'Банки'; family = 'catalog'; isMovement = $false },
            @{ table = 'Document_ПоступлениеТоваровУслуг'; name = 'ПоступлениеТоваровУслуг'; family = 'document'; isMovement = $true },
            @{ table = 'AccountingRegister_Хозрасчетный'; name = 'Хозрасчетный'; family = 'reg_accounting'; isMovement = $true; from = '2025-08-01' }) })
    }
    $cfg | ConvertTo-Json -Depth 6 | Set-Content "$Work\sync.json" -Encoding utf8
}

function Dev([string[]]$a) { & $Sup sync-dev @a --session $DevData }

function Start-Sup {
    $log = "$Out\sup-$(Get-Date -Format yyyyMMdd-HHmmss).log"
    $p = Start-Process $Sup -ArgumentList 'run', '--bases', $Bases, '--port', $Port, '--token', $EdgeToken, '--sync-config', "$Work\sync.json", '--host', $HostExe `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -WindowStyle Hidden
    $p.Id | Set-Content "$Work\sup.pid"
    for ($i = 0; $i -lt 240; $i++) {
        if ($p.HasExited) { throw "supervisor exited ($($p.ExitCode)): $(Get-Content "$log.err" -Raw)" }
        try { Edge GET '/v1/supervisor' | Out-Null; return $p } catch { Start-Sleep -Milliseconds 500 }
    }
    throw "supervisor did not come up; see $log"
}

function Stop-Sup {
    $id = Get-Content "$Work\sup.pid" -ErrorAction SilentlyContinue
    if ($id) { $p = Get-Process -Id $id -ErrorAction SilentlyContinue; if ($p) { $p.Kill($true); $p.WaitForExit(15000) | Out-Null } }
    Get-Process OneC.Host -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $HostExe } | ForEach-Object { $_.Kill() }
}

function Edge([string]$Method, [string]$Path) {
    $r = Invoke-WebRequest -Uri "http://127.0.0.1:$Port$Path" -Method $Method -Headers @{ 'X-AIBA-Token' = $EdgeToken } -UseBasicParsing -TimeoutSec 60
    if ($r.Content) { $r.Content | ConvertFrom-Json }
}

function Status { (Edge GET '/v1/sync')[0] }
function Faults([int]$Fail503 = 0, [int]$FailNetwork = 0, [int]$BadToken = 0, [int]$Reject400 = 0, [switch]$CrashOnComplete) {
    Edge POST "/v1/sync/faults?fail503=$Fail503&failNetwork=$FailNetwork&badToken=$BadToken&reject400=$Reject400&crashOnComplete=$($CrashOnComplete.IsPresent.ToString().ToLower())"
}

function Wait-Idle([int]$TimeoutSec = 900) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        try { $b = Status } catch { Start-Sleep 2; continue }
        if ($b.mode -eq 'paused') { throw "paused: $($b.reason) / $($b.lastError)" }
        if ($b.mode -eq 'incremental' -and $b.pendingWork -eq 0) { return [pscustomobject]@{ seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1); status = $b } }
        Start-Sleep -Milliseconds 500
    }
    throw "not idle after $TimeoutSec s: $(Status | ConvertTo-Json -Compress)"
}

# Every stored row read back and compared with 1C (never the backend's counts). Run while idle.
function Verify([string]$Tag) {
    & $Sup sync-verify --bases $Bases --sync-config "$Work\sync.json" --host $HostExe --report "$Out\verify-$Tag.json" 2>&1 | Tee-Object "$Out\verify-$Tag.txt"
    if ($LASTEXITCODE -ne 0) { throw "verify $Tag FAILED (exit $LASTEXITCODE) — stop condition, see $Out\verify-$Tag.txt" }
}

# The test-owned document on bilim (AIBA_REWRITE_S12_ marker): create | post | unpost | change | delete | cleanup | show
function Doc([string]$Step) {
    & $DevBench lifecycle $Step --bases $Bases --base bilim --state "$Work\lifecycle.json" 2>&1 | Tee-Object -Append "$Out\lifecycle.txt"
    if ($LASTEXITCODE -ne 0) { throw "lifecycle $Step failed" }
}

function Http-Stats([switch]$Clear) {
    $h = Edge GET "/v1/sync/http?clear=$($Clear.IsPresent.ToString().ToLower())"
    $h.calls | Group-Object { "$($_.method) $($_.path)" } | ForEach-Object {
        $ms = @($_.Group.ms | Sort-Object)
        [pscustomobject]@{
            call = $_.Name; n = $_.Count
            p50 = [math]::Round($ms[[int][math]::Floor(($ms.Count - 1) * 0.5)], 0)
            p95 = [math]::Round($ms[[int][math]::Floor(($ms.Count - 1) * 0.95)], 0)
            reqMB = [math]::Round((($_.Group.requestBytes | Measure-Object -Sum).Sum) / 1MB, 2)
            statuses = (($_.Group | Group-Object status | ForEach-Object { "$($_.Name)x$($_.Count)" }) -join ' ')
        } }
}

function Mem {
    $sup = Get-Process -Id (Get-Content "$Work\sup.pid") -ErrorAction SilentlyContinue
    $hosts = Get-Process OneC.Host -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $HostExe }
    [pscustomobject]@{
        supervisorPrivateMB = [math]::Round($sup.PrivateMemorySize64 / 1MB); supervisorCpuS = [math]::Round($sup.TotalProcessorTime.TotalSeconds, 1)
        hostPrivateMB = [math]::Round((($hosts | Measure-Object PrivateMemorySize64 -Sum).Sum) / 1MB); hostCpuS = [math]::Round((($hosts | ForEach-Object { $_.TotalProcessorTime.TotalSeconds } | Measure-Object -Sum).Sum), 1)
    }
}
