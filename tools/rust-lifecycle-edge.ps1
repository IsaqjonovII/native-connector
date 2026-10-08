# R5 (PYTHON_TO_RUST_MIGRATION_PLAN) steps 2–7 through the Supervisor's own edge — the host that
# already holds bilim does the writes, so no second process opens the file base (each DevBench run
# waited ~35 min on it, 2026-10-07). Test-owned document only (AIBA_REWRITE_ marker; WriteService
# refuses anything else). After each step: wait until Rust changes, wait idle, compare with 1C.
param([string[]]$Only, [int]$FirstNumber = 2, [int]$PickupMinutes = 60)
. D:\aiba\1c-arch\tools\rust-local.ps1
$log = "$Out\lifecycle-run.txt"
function Say([string]$s) { "$(Get-Date -Format HH:mm:ss) $s" | Tee-Object -Append $log }
function Fingerprint([string]$ref) {
    (Sql "select coalesce(string_agg(table_name || row_key || data_hash, ',' order by table_name, row_key), '') from onec.entity_data where onec_id = 13 and (row_key = '$ref' or recorder_key = '$ref')") -join ''
}
function Rows([string]$ref) {
    $n = Sql "select table_name || ' ' || count(*) from onec.entity_data where onec_id = 13 and (row_key = '$ref' or recorder_key = '$ref') group by table_name order by 1"
    Say "  Rust rows of $ref : $(if ($n) { $n -join '; ' } else { 'none' })"
}
$ref = (Get-Content "$Work\lifecycle.json" -Raw | ConvertFrom-Json).ref.ToLower()
$doc = '/v1/bases/bilim/documents/' + [Uri]::EscapeDataString('ПоступлениеТоваровУслуг') + "/$ref"
function Write-Edge([string]$Method, [string]$Path, [string]$Body) {
    $p = @{ Uri = "http://127.0.0.1:$Port$Path"; Method = $Method; Headers = @{ 'X-AIBA-Token' = $EdgeToken }; UseBasicParsing = $true; TimeoutSec = 3600 }
    if ($Body) { $p.Body = [Text.Encoding]::UTF8.GetBytes($Body); $p.ContentType = 'application/json; charset=utf-8' }
    (Invoke-WebRequest @p).Content
}
$newDate = (Get-Date).AddDays(-1).ToString('yyyy-MM-ddTHH:mm:ss')
$steps = [ordered]@{
    'post'         = { Write-Edge POST "$doc/post" }
    'change-date'  = { Write-Edge PUT "$doc`?autoUnpost=true&post=true" "{`"Дата`":`"$newDate`"}" }
    'unpost'       = { Write-Edge POST "$doc/unpost" }
    'repost'       = { Write-Edge POST "$doc/post" }
    'mark-deleted' = { Write-Edge POST "$doc/mark-deleted" '{}' }
    'delete'       = { Write-Edge DELETE "$doc`?hard=true" }
}
$i = $FirstNumber - 1
foreach ($s in @($steps.Keys | Where-Object { -not $Only -or $Only -contains $_ })) {
    $i++
    Say "step $i $s (edge)"
    $before = Fingerprint $ref
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try { $answer = & $steps[$s] }
    catch { Say "  1C call failed after $([math]::Round($sw.Elapsed.TotalSeconds, 1)) s: $($_.ErrorDetails.Message -replace '\s+', ' ')"; $answer = '' }
    Say "  1C answered in $([math]::Round($sw.Elapsed.TotalSeconds, 1)) s: $($answer.Substring(0, [Math]::Min(200, $answer.Length)))"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ((Fingerprint $ref) -eq $before -and $sw.Elapsed.TotalMinutes -lt $PickupMinutes) { Start-Sleep 2 }
    if ((Fingerprint $ref) -eq $before) { Say "  FAIL: Rust did not change within $PickupMinutes min"; throw "step $s not picked up" }
    Say "  picked up after $([math]::Round($sw.Elapsed.TotalSeconds, 1)) s"
    $w = Wait-Idle 3600
    Say "  idle after $($w.seconds) s"
    Rows $ref
    Verify "r5-$i-$s"
    Say "  verify r5-$i-$s PASS"
}
Say 'lifecycle PASS'
