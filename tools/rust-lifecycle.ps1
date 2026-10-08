# R5 (PYTHON_TO_RUST_MIGRATION_PLAN): document lifecycle on bilim into the local Rust module.
# Each step changes ONE test-owned document (AIBA_REWRITE_S12_ marker, DevBench lifecycle), waits for
# the engine to be idle, checks the recorder's rows in Rust, then compares every stored row with 1C.
. D:\aiba\1c-arch\tools\rust-local.ps1
$log = "$Out\lifecycle-run.txt"
function Say([string]$s) { "$(Get-Date -Format HH:mm:ss) $s" | Tee-Object -Append $log }
function Rows([string]$ref) {
    $n = Sql "select table_name || ' ' || count(*) from onec.entity_data where onec_id = 13 and (row_key = '$ref' or recorder_key = '$ref') group by table_name order by 1"
    Say "  Rust rows of $ref : $(if ($n) { $n -join '; ' } else { 'none' })"
}
# What Rust holds for the recorder (document + movements, with content hashes): a step is picked up
# when this changes. Idle alone is not enough — the engine may not have read the change yet.
function Fingerprint([string]$ref) {
    if (-not $ref) { return '' }
    (Sql "select coalesce(string_agg(table_name || row_key || data_hash, ',' order by table_name, row_key), '') from onec.entity_data where onec_id = 13 and (row_key = '$ref' or recorder_key = '$ref')") -join ''
}
$steps = @('create', 'post', 'change', 'post', 'unpost', 'post', 'delete')
Doc 'cleanup'
$i = 0
$last = $null
foreach ($s in $steps) {
    $i++
    Say "step $i $s"
    $before = Fingerprint $last
    Doc $s
    $st = Get-Content "$Work\lifecycle.json" -Raw | ConvertFrom-Json
    $ref = if ($st.ref) { $st.ref.ToLower() } else { $last }
    $last = $ref
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ((Fingerprint $ref) -eq $before -and $sw.Elapsed.TotalMinutes -lt 60) { Start-Sleep 2 }
    if ((Fingerprint $ref) -eq $before) { Say "  FAIL: Rust did not change within 60 min"; throw "step $s not picked up" }
    Say "  picked up after $([math]::Round($sw.Elapsed.TotalSeconds, 1)) s"
    $w = Wait-Idle 3600
    Say "  idle after $($w.seconds) s"
    Rows $ref.ToLower()
    Verify "r5-$i-$s"
    Say "  verify r5-$i-$s PASS"
}
Say 'lifecycle PASS'
