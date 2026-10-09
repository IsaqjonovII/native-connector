# R10 (PYTHON_TO_RUST_MIGRATION_PLAN): per-base backend switch, LOCAL/ISOLATED only.
#   bilim bound to the local Rust module (connection 13) → refused without "switch" → explicit switch to
#   the isolated local backend/1c (record $Py) → a 1C change while on Python → rollback to Rust, which
#   catches up from its own cursor → Rust = 1C, coverage intact. Never two backends for the base.
. D:\aiba\1c-arch\tools\rust-local.ps1
$log = "$Out\r10-run.txt"
$Py = '6ac74a64b615f076549ab133'
$PySecrets = "$RustData\python-secrets.json"
$PyUrl = 'http://127.0.0.1:18041'
$DocType = 'ПоступлениеТоваровУслуг'
function Say([string]$s) { "$(Get-Date -Format HH:mm:ss) $s" | Tee-Object -Append $log }
function Config([string]$Target, [string]$Secrets, [string]$Conn, [string]$Db, [bool]$Switch) {
    $tables = (Get-Content "$Work\sync.json" -Raw | ConvertFrom-Json).bases[0].tables
    $cfg = [ordered]@{ db = $Db; target = $Target; secrets = $Secrets; switch = $Switch; bases = @(@{ name = 'bilim'; connectionId = $Conn; tables = $tables }) }
    $cfg | ConvertTo-Json -Depth 8 | Set-Content "$Work\sync.json" -Encoding utf8
}
function RustState { Sql "select count(*) || ' rows, last write ' || coalesce(max(synced_at)::text, '-') from onec.entity_data where onec_id = 13" }
# Not "Edge": rust-local.ps1's Edge (parsed JSON) is what Status / Wait-Idle use.
function EdgeWrite([string]$Method, [string]$Path, [string]$Body) {
    $p = @{ Uri = "http://127.0.0.1:$Port$Path"; Method = $Method; Headers = @{ 'X-AIBA-Token' = $EdgeToken }; UseBasicParsing = $true; TimeoutSec = 600 }
    if ($Body) { $p.Body = [Text.Encoding]::UTF8.GetBytes($Body); $p.ContentType = 'application/json; charset=utf-8' }
    $r = Invoke-WebRequest @p; [Text.Encoding]::UTF8.GetString($r.RawContentStream.ToArray())
}
$rustCfg = @{ Target = "rust:$RustUrl"; Secrets = $Secrets; Conn = '13'; Db = "$Work\sync-bilim.db" }
$pyCfg = @{ Target = $PyUrl; Secrets = $PySecrets; Conn = $Py; Db = "$Work\sync-bilim-python.db" }
New-RustConfig -Conn 13                                                  # the table plan
Say '=== R10 per-base switch (local only)'

Say '--- A: bilim on Rust (binding recorded)'
Config @rustCfg -Switch $false
Start-Sup | Out-Null; Wait-Idle 1800 | Out-Null; Stop-Sup
Say "  binding: $((Get-Content "$Work\sync-targets.json" -Raw | ConvertFrom-Json).bilim.active.identity)"

Say '--- B: the other backend without "switch" is refused'
Config @pyCfg -Switch $false
try { Start-Sup | Out-Null; Stop-Sup; Say '  FAIL: started without a switch'; throw 'B' } catch { if ($_.Exception.Message -eq 'B') { throw }; Say "  refused: $(($_.Exception.Message -split "`n" | Select-String 'switch' | Select -First 1))" }

Say '--- C: explicit switch to the isolated backend/1c'
$rustBefore = RustState
Config @pyCfg -Switch $true
Start-Sup | Out-Null
$w = Wait-Idle 3600; Say "  first copy into backend/1c done in $($w.seconds) s"
Verify 'r10-python-after-switch'; Say '  verify r10-python-after-switch PASS (backend/1c = 1C)'

Say '--- D: a 1C change while bilim is on Python'
$stamp = Get-Date -Format MMddHHmmss
$body = Get-Content "$Work\ptu-body.json" -Raw | ConvertFrom-Json -AsHashtable
$body['Комментарий'] = "AIBA_REWRITE_R10_${stamp}: switch test"; $body['_idempotencyMarker'] = "AIBA_REWRITE_R10_$stamp"
$seen = (Status).lastEventAt
$created = EdgeWrite POST ('/v1/bases/bilim/documents/' + [Uri]::EscapeDataString($DocType) + '?post=true') ($body | ConvertTo-Json -Depth 20) | ConvertFrom-Json
$ref = $created.id.ToLower(); Say "  created + posted $ref in 1C"
# An idle base reads its event log about once a minute: wait until Sync has read this write, not a fixed time
# (2026-10-09: 20 s + idle passed before the event was read — a test race, the change arrived ~70 s later).
$sw = [Diagnostics.Stopwatch]::StartNew()
while ((Status).lastEventAt -eq $seen -and $sw.Elapsed.TotalMinutes -lt 20) { Start-Sleep 2 }
Wait-Idle 1800 | Out-Null
Verify 'r10-python-after-change'; Say '  verify r10-python-after-change PASS (backend/1c has the change)'
$rustDuring = RustState
if ($rustDuring -ne $rustBefore) { Say "  FAIL: Rust was written while bilim was on Python ($rustBefore → $rustDuring)"; throw 'two backends' }
Say "  Rust untouched while on Python: $rustDuring"
Stop-Sup

Say '--- E: rollback to Rust'
Config @rustCfg -Switch $true
Start-Sup | Out-Null
$sw = [Diagnostics.Stopwatch]::StartNew()
while (-not (Sql "select 1 from onec.entity_data where onec_id = 13 and row_key = '$ref'") -and $sw.Elapsed.TotalMinutes -lt 30) { Start-Sleep 2 }
Say "  Rust caught up the change made on Python after $([math]::Round($sw.Elapsed.TotalSeconds, 1)) s: $(Sql "select table_name || ' ' || count(*) from onec.entity_data where onec_id = 13 and (row_key = '$ref' or recorder_key = '$ref') group by 1 order by 1")"
Wait-Idle 1800 | Out-Null
Verify 'r10-rust-after-rollback'; Say '  verify r10-rust-after-rollback PASS (Rust = 1C)'
Say "  Rust coverage: $(Sql "select string_agg(k || '=' || (v->>'complete') || '/' || coalesce(v->>'dataFrom', 'whole'), ', ' order by k) from onec.connection, jsonb_each(raw->'dataCoverage') t(k, v) where id = 13")"

Say '--- F: cleanup'
$before = Sql "select count(*) from onec.entity_data where onec_id = 13 and (row_key = '$ref' or recorder_key = '$ref')"
EdgeWrite DELETE ('/v1/bases/bilim/documents/' + [Uri]::EscapeDataString($DocType) + "/$ref`?hard=true") | Out-Null
$sw = [Diagnostics.Stopwatch]::StartNew()
while ((Sql "select count(*) from onec.entity_data where onec_id = 13 and (row_key = '$ref' or recorder_key = '$ref')") -ne '0' -and $sw.Elapsed.TotalMinutes -lt 20) { Start-Sleep 2 }
Wait-Idle 1800 | Out-Null
Verify 'r10-rust-after-cleanup'; Say '  verify r10-rust-after-cleanup PASS'
Say "  binding now: $((Get-Content "$Work\sync-targets.json" -Raw | ConvertFrom-Json).bilim | ConvertTo-Json -Compress)"
Say 'R10 PASS'
