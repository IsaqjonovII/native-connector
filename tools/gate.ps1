# D45 release gate, one complete run: clean Release build, the full suite in gate mode (AIBA_TEST_GATE=1: a
# missing live dependency FAILS instead of returning early), live 1C (bilim file base + kansler server base),
# the isolated local backend/1c and the local Rust module. Afterwards: no leftover processes, no AIBA_REWRITE_
# documents in either base. Run it twice back to back on the same build for the gate (-NoBuild the 2nd time).
#   pwsh tools\gate.ps1 -Run 1            pwsh tools\gate.ps1 -Run 2 -NoBuild
param([int]$Run = 1, [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$Repo = Split-Path $PSScriptRoot
$Data = Join-Path $env:LOCALAPPDATA 'AIBA-rust-sync-test'
$OutDir = Join-Path $Repo 'measurements\gate'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$log = Join-Path $OutDir ("gate-{0:yyyyMMdd-HHmmss}-run{1}.txt" -f (Get-Date), $Run)
function Say([string]$s) { "$(Get-Date -Format HH:mm:ss) $s" | Tee-Object -Append $log }

$env:AIBA_TEST_GATE = '1'
$env:ONEC_TEST_BASES = Join-Path $Repo 'bases.local.json'
$env:AIBA_SYNC_BACKEND = 'http://127.0.0.1:18041'
$env:AIBA_SYNC_SECRETS = Join-Path $Data 'python-secrets.json'
$env:AIBA_RUST_SYNC_SECRETS = Join-Path $Data 'rust-secrets.json'
foreach ($u in 'http://127.0.0.1:18041/docs', 'http://127.0.0.1:18112/api/sync/v1/capabilities') {
    try { Invoke-WebRequest $u -UseBasicParsing -TimeoutSec 10 | Out-Null } catch { if (-not $_.Exception.Response) { throw "dependency down: $u" } }
}
$stray = Get-Process testhost, OneC.Host, OneC.Supervisor, OneCLiveChild, DevBench -ErrorAction SilentlyContinue
if ($stray) { throw "processes left from before: $($stray.Name -join ', ')" }

if (-not $NoBuild) {
    Say "run ${Run}: clean Release build"
    foreach ($p in 'src\OneC.Host\OneC.Host.csproj', 'src\OneC.Supervisor\OneC.Supervisor.csproj', 'src\OneC.Desktop\OneC.Desktop.csproj',
                   'tests\OneC.Tests\OneC.Tests.csproj', 'tests\OneCLiveChild\OneCLiveChild.csproj', 'research\sync-spikes\DevBench\DevBench.csproj') {
        & dotnet clean (Join-Path $Repo $p) -c Release --nologo -v q | Out-Null
        & dotnet build (Join-Path $Repo $p) -c Release --nologo -v q 2>&1 | Select-String ' error ' | ForEach-Object { Say "BUILD ERROR $_" }
        if ($LASTEXITCODE -ne 0) { throw "build failed: $p" }
    }
}
Say "run ${Run}: dotnet test (gate mode)"
$sw = [Diagnostics.Stopwatch]::StartNew()
& dotnet test (Join-Path $Repo 'tests\OneC.Tests\OneC.Tests.csproj') -c Release --no-build --nologo --logger 'console;verbosity=normal' 2>&1 |
    Tee-Object -FilePath "$log.full" | Select-String 'Passed!|Failed!|\[FAIL\]|Skipped |Total tests' | ForEach-Object { Say "$_" }
$code = $LASTEXITCODE
Say ("run {0}: exit {1} in {2:mm\:ss}" -f $Run, $code, $sw.Elapsed)
Start-Sleep 5
$left = Get-Process testhost, OneC.Host, OneC.Supervisor, OneCLiveChild -ErrorAction SilentlyContinue
Say "leftover processes: $(if ($left) { ($left | ForEach-Object { "$($_.Name)#$($_.Id)" }) -join ', ' } else { 'none' })"
$dev = Join-Path $Repo 'research\sync-spikes\DevBench\bin\Release\net9.0\DevBench.exe'
& $dev leftovers --bases $env:ONEC_TEST_BASES 2>&1 | ForEach-Object { Say "leftovers: $_" }
if ($code -ne 0 -or $left) { throw "gate run $Run FAILED" }
Say "gate run $Run PASS"
