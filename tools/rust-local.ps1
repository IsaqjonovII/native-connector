# R3–R8 local Rust sync helpers (PYTHON_TO_RUST_MIGRATION_PLAN.md). Dot-source:  . D:\aiba\1c-arch\tools\rust-local.ps1
# Everything is local: the Rust onec module on 127.0.0.1 against its own Postgres cluster (127.0.0.1:55440,
# its own data dir), the test secrets in $RustData\rust-secrets.json (outside the repo), local 1C test bases.
$Repo     = 'D:\aiba\1c-arch'
$RustData = Join-Path $env:LOCALAPPDATA 'AIBA-rust-sync-test'          # pgdata, onec.log, rust-secrets.json
$Work     = Join-Path $RustData 'sync'                                  # sync.db, sync.json, bases (outside the repo)
$Out      = Join-Path $Repo 'measurements\rust-sync'                     # reports kept with the repo
$Sup      = Join-Path $Repo 'src\OneC.Supervisor\bin\Release\net9.0\OneC.Supervisor.exe'
$HostExe  = Join-Path $Repo 'src\OneC.Host\bin\Release\net9.0\OneC.Host.exe'
$DevBench = Join-Path $Repo 'research\sync-spikes\DevBench\bin\Debug\net9.0\DevBench.exe'
$Secrets  = Join-Path $RustData 'rust-secrets.json'
$Psql     = 'C:\Program Files\PostgreSQL 1C\12\bin\psql.exe'
$Port     = 57751
New-Item -ItemType Directory -Force $Work, $Out | Out-Null
if (-not (Test-Path "$Work\edge-token.txt")) { [guid]::NewGuid().ToString('N') | Set-Content "$Work\edge-token.txt" }   # local edge only
$EdgeToken = (Get-Content "$Work\edge-token.txt").Trim()
$Bases = "$Work\bases.json"
$RustUrl = (Get-Content $Secrets -Raw | ConvertFrom-Json).url

function New-RustBases([string]$Name = 'bilim') {
    $all = Get-Content "$Repo\bases.local.json" -Raw | ConvertFrom-Json
    ConvertTo-Json -InputObject @($all | Where-Object Name -eq $Name) -Depth 5 | Set-Content $Bases -Encoding utf8
}

function Rust([string[]]$a) { & $Sup sync-rust @a --secrets $Secrets }

# The table set for the 1C comparison; $Conn = the Rust connection id printed by `Rust create`.
function New-RustConfig([string]$Conn, [string]$Base = 'bilim', [object[]]$Tables, [switch]$Faults, [switch]$Commands) {
    if ($Conn -notmatch '^\d+$') { throw "connection id must be the number printed by 'Rust create'" }
    if (-not $Tables) {
        $Tables = @(
            @{ table = 'ChartOfAccounts_Хозрасчетный'; name = 'Хозрасчетный'; family = 'chart'; isMovement = $false },
            @{ table = 'Catalog_Банки'; name = 'Банки'; family = 'catalog'; isMovement = $false },
            @{ table = 'Catalog_Контрагенты'; name = 'Контрагенты'; family = 'catalog'; isMovement = $false },
            @{ table = 'Catalog_Номенклатура'; name = 'Номенклатура'; family = 'catalog'; isMovement = $false },
            @{ table = 'Document_ПоступлениеТоваровУслуг'; name = 'ПоступлениеТоваровУслуг'; family = 'document'; isMovement = $true },
            @{ table = 'Document_РеализацияТоваровУслуг'; name = 'РеализацияТоваровУслуг'; family = 'document'; isMovement = $true },
            @{ table = 'AccountingRegister_Хозрасчетный'; name = 'Хозрасчетный'; family = 'reg_accounting'; isMovement = $true; from = '2025-08-01' },
            @{ table = 'InformationRegister_КурсыВалют'; name = 'КурсыВалют'; family = 'reg_info_independent'; isMovement = $false })
    }
    $cfg = [ordered]@{
        db = "$Work\sync-$Base.db"; target = "rust:$RustUrl"; secrets = $Secrets; faults = [bool]$Faults; commands = [bool]$Commands
        bases = @(@{ name = $Base; connectionId = $Conn; tables = $Tables })
    }
    $cfg | ConvertTo-Json -Depth 6 | Set-Content "$Work\sync.json" -Encoding utf8
}

function Start-Sup {
    $log = "$Out\sup-$(Get-Date -Format yyyyMMdd-HHmmss).log"
    $p = Start-Process $Sup -ArgumentList 'run', '--bases', "`"$Bases`"", '--port', $Port, '--token', $EdgeToken, '--sync-config', "`"$Work\sync.json`"", '--host', "`"$HostExe`"" `
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
}

function Edge([string]$Method, [string]$Path) {
    $r = Invoke-WebRequest -Uri "http://127.0.0.1:$Port$Path" -Method $Method -Headers @{ 'X-AIBA-Token' = $EdgeToken } -UseBasicParsing -TimeoutSec 60
    if ($r.Content) { $r.Content | ConvertFrom-Json }
}

function Status { (Edge GET '/v1/sync')[0] }

function Wait-Idle([int]$TimeoutSec = 1800) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        try { $b = Status } catch { Start-Sleep 2; continue }
        if ($b.mode -eq 'paused') { throw "paused: $($b.reason) / $($b.lastError)" }
        if ($b.mode -eq 'incremental' -and $b.pendingWork -eq 0) { return [pscustomobject]@{ seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1); status = $b } }
        Start-Sleep -Milliseconds 500
    }
    throw "not idle after $TimeoutSec s: $(Status | ConvertTo-Json -Compress)"
}

# Every stored row read back from Rust and compared with a fresh 1C read (never a count). Run while idle.
function Verify([string]$Tag) {
    & $Sup sync-verify --bases $Bases --sync-config "$Work\sync.json" --host $HostExe --report "$Out\verify-$Tag.json" 2>&1 | Tee-Object "$Out\verify-$Tag.txt"
    if ($LASTEXITCODE -ne 0) { throw "verify $Tag FAILED (exit $LASTEXITCODE) — see $Out\verify-$Tag.txt" }
}

# The test-owned document on bilim (AIBA_REWRITE_ marker): create | post | unpost | change | delete | cleanup | show
function Doc([string]$Step) {
    & $DevBench lifecycle $Step --bases $Bases --base bilim --state "$Work\lifecycle.json" 2>&1 | Tee-Object -Append "$Out\lifecycle.txt"
    if ($LASTEXITCODE -ne 0) { throw "lifecycle $Step failed" }
}

function Http-Stats([switch]$Clear) {
    $h = Edge GET "/v1/sync/http?clear=$($Clear.IsPresent.ToString().ToLower())"
    $h.calls | Group-Object { "$($_.method) $($_.path -replace '/connections/\d+', '/connections/{c}')" } | ForEach-Object {
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
    $rust = Get-Process aiba-onec -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$RustData*" }
    $pg = Get-Process postgres -ErrorAction SilentlyContinue
    [pscustomobject]@{
        supervisorPrivateMB = [math]::Round($sup.PrivateMemorySize64 / 1MB); supervisorCpuS = [math]::Round($sup.TotalProcessorTime.TotalSeconds, 1)
        hostPrivateMB = [math]::Round((($hosts | Measure-Object PrivateMemorySize64 -Sum).Sum) / 1MB)
        rustPrivateMB = [math]::Round($rust.PrivateMemorySize64 / 1MB); rustCpuS = [math]::Round($rust.TotalProcessorTime.TotalSeconds, 1)
        postgresCpuS = [math]::Round((($pg | ForEach-Object { $_.TotalProcessorTime.TotalSeconds } | Measure-Object -Sum).Sum), 1)
    }
}

# Through a UTF-8 file: a Cyrillic table name on psql's command line arrives in the console code page.
function Sql([string]$Query) {
    $f = Join-Path $env:TEMP "aiba-rust-sql-$PID.sql"
    [IO.File]::WriteAllText($f, $Query, (New-Object Text.UTF8Encoding $false))
    $env:PGCLIENTENCODING = 'UTF8'
    $prev = [Console]::OutputEncoding; [Console]::OutputEncoding = [Text.Encoding]::UTF8
    try { & $Psql -h 127.0.0.1 -p 55440 -U postgres -d onec_rust_sync_test -At -f $f } finally { [Console]::OutputEncoding = $prev }
}
