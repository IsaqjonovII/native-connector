# R9 (PYTHON_TO_RUST_MIGRATION_PLAN): normal writes, round trip
#   Rust command → Supervisor (pulls) → OneC.Host → 1C → Sync observes → Rust converges to 1C.
# bilim only, one test-owned document (AIBA_REWRITE_R9_ marker). After each write: the command's
# outcome, then Rust must change (Sync, not the command result), go idle, and match 1C (sync-verify).
# Run with the Supervisor up on New-RustConfig -Conn 13 -Commands.
. D:\aiba\1c-arch\tools\rust-local.ps1
$log = "$Out\r9-run.txt"
$Conn = if ($env:R9_CONN) { $env:R9_CONN } else { '13' }        # R9_CONN / R9_BASE: another test base (e.g. the server copy bilimsrv)
$Base = if ($env:R9_BASE) { $env:R9_BASE } else { 'bilim' }
$DocType = 'ПоступлениеТоваровУслуг'
function Say([string]$s) { "$(Get-Date -Format HH:mm:ss) $s" | Tee-Object -Append $log }
function Fingerprint([string]$ref) {
    if (-not $ref) { return '' }
    (Sql "select coalesce(string_agg(table_name || row_key || data_hash, ',' order by table_name, row_key), '') from onec.entity_data where onec_id = $Conn and (row_key = '$ref' or recorder_key = '$ref')") -join ''
}
function Command([string]$Kind, [string]$Key, [hashtable]$Payload) {
    $f = "$Work\r9-payload.json"
    [IO.File]::WriteAllText($f, ($Payload | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding $false))
    $out = & $Sup sync-rust command --secrets $Secrets --connection $Conn --kind $Kind --key $Key --payload $f --wait 3600 2>&1
    $j = ($out | Select-Object -Last 1) | ConvertFrom-Json
    Say "  command $Kind [$Key]: state $($j.state)$(if ($j.deduplicated) { ' (deduplicated)' }) result $(($j.result | ConvertTo-Json -Compress -Depth 5)) error $(($j.error | ConvertTo-Json -Compress -Depth 5))"
    $j
}
function Converge([string]$Tag, [string]$ref, [string]$before) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ((Fingerprint $ref) -eq $before -and $sw.Elapsed.TotalMinutes -lt 20) { Start-Sleep 2 }
    if ((Fingerprint $ref) -eq $before) { Say "  FAIL: Rust did not change within 20 min"; throw "$Tag not observed" }
    Say "  Sync brought it into Rust after $([math]::Round($sw.Elapsed.TotalSeconds, 1)) s"
    Wait-Idle 1800 | Out-Null
    $n = Sql "select table_name || ' ' || count(*) || ' posted=' || coalesce(max(raw->>'posted'), '-') || ' mark=' || coalesce(max(raw->>'deletionMark'), '-') from onec.entity_data where onec_id = $Conn and (row_key = '$ref' or recorder_key = '$ref') group by table_name order by 1"
    Say "  Rust holds: $($n -join '; ')"
    Verify "r9-$Tag"
    Say "  verify r9-$Tag PASS (Rust = 1C)"
}

$stamp = Get-Date -Format MMddHHmmss
$marker = "AIBA_REWRITE_R9_$stamp"
$body = Get-Content "$Work\ptu-body.json" -Raw | ConvertFrom-Json -AsHashtable
$body['Комментарий'] = "${marker}: rust command round trip"
$body['_idempotencyMarker'] = $marker
Say "=== R9 round trip $marker"

# --- refused before anything is stored (Rust) ---
foreach ($bad in @(
    @{ k = 'document.create'; p = @{ docType = $DocType; body = @{ 'Комментарий' = 'not ours'; Date = '2025-09-30' }; post = $false }; why = 'no AIBA marker' },
    @{ k = 'document.create'; p = @{ docType = $DocType; body = $body; exchange = $true }; why = 'exchange' },
    @{ k = 'document.create'; p = @{ docType = $DocType; body = ($body + @{ '_postCreateMethod' = 'Записать' }) }; why = '_postCreateMethod' },
    @{ k = 'document.delete'; p = @{ docType = $DocType; ref = [guid]::NewGuid().ToString() }; why = 'hard delete kind' })) {
    $j = Command $bad.k "r9-bad-$stamp-$($bad.why -replace '\W','')" $bad.p
    if ($j.enqueued -ne $false) { Say "  FAIL: '$($bad.why)' was accepted"; throw 'refusal' }
    Say "  refused as expected: $($bad.why)"
}

# --- create (draft), then the same command again, then a new key with the same marker ---
$c = Command 'document.create' "r9-create-$stamp" @{ docType = $DocType; body = $body; post = $false }
if ($c.state -ne 'succeeded' -or -not $c.result.id) { throw 'create failed' }
$ref = $c.result.id.ToLower()
Say "  created $ref (posted $($c.result.posted))"
Converge 'create' $ref ''
$again = Command 'document.create' "r9-create-$stamp" @{ docType = $DocType; body = $body; post = $false }
if (-not $again.deduplicated -or $again.commandId -ne $c.commandId) { throw 'the same key must be the same command' }
$twin = Command 'document.create' "r9-create2-$stamp" @{ docType = $DocType; body = $body; post = $false }
if ($twin.result.id.ToLower() -ne $ref) { throw "the marker must find the first document, got $($twin.result.id)" }
Say "  idempotent: same key = same command; new key, same marker = same 1C document ($($twin.result | ConvertTo-Json -Compress))"
$reuse = Command 'document.create' "r9-create-$stamp" @{ docType = $DocType; body = $body; post = $true }
if ($reuse.enqueued -ne $false) { throw 'a key reused for another request must be refused' }

# --- post, update (date) with repost, unpost, mark for deletion ---
$steps = @(
    @{ tag = 'post';     kind = 'document.post';        p = @{ docType = $DocType; ref = $ref } },
    @{ tag = 'update';   kind = 'document.update';      p = @{ docType = $DocType; ref = $ref; fields = @{ Date = '2025-09-29T12:00:00' }; autoUnpost = $true; post = $true } },
    @{ tag = 'unpost';   kind = 'document.unpost';      p = @{ docType = $DocType; ref = $ref } },
    @{ tag = 'repost';   kind = 'document.post';        p = @{ docType = $DocType; ref = $ref } },
    @{ tag = 'markdel';  kind = 'document.markDeleted'; p = @{ docType = $DocType; ref = $ref } })
foreach ($s in $steps) {
    Say "--- $($s.tag)"
    $before = Fingerprint $ref
    $j = Command $s.kind "r9-$($s.tag)-$stamp" $s.p
    if ($j.state -ne 'succeeded') { throw "$($s.tag) failed: $($j.error | ConvertTo-Json -Compress)" }
    Converge $s.tag $ref $before
}

# --- a real 1C/host error comes back as it is, and changes nothing ---
$foreign = (Sql "select row_key from onec.entity_data where onec_id = $Conn and table_name = 'Document_ПоступлениеТоваровУслуг' and raw->>'Комментарий' not like 'AIBA_%' order by row_key limit 1")
$bf = Fingerprint $foreign
$j = Command 'document.markDeleted' "r9-foreign-$stamp" @{ docType = $DocType; ref = $foreign }
if ($j.state -ne 'failed') { throw "marking a document AIBA did not create for deletion must fail (D53), got $($j.state)" }
$j2 = Command 'document.update' "r9-baddate-$stamp" @{ docType = $DocType; ref = $ref; fields = @{ Date = 'not-a-date' } }
if ($j2.state -ne 'failed') { throw "a bad date must fail, got $($j2.state)" }
Start-Sleep 10
if ((Fingerprint $foreign) -ne $bf) { throw 'the foreign document changed' }
Say "  errors returned as 1C/host gave them; foreign document unchanged"

# --- owned delete (test cleanup, not a command kind): the edge's owned hard delete; Sync removes it ---
$before = Fingerprint $ref
$doc = "/v1/bases/$Base/documents/" + [Uri]::EscapeDataString($DocType) + "/$ref`?hard=true"
# A hard delete checks references across the base: on a server base it can outlive the edge's 65 s deadline
# and still land — the convergence below is what proves it.
try { Invoke-WebRequest -Uri "http://127.0.0.1:$Port$doc" -Method DELETE -Headers @{ 'X-AIBA-Token' = $EdgeToken } -UseBasicParsing -TimeoutSec 600 | Out-Null }
catch { if ("$($_.ErrorDetails.Message)" -notmatch 'did not answer') { throw }; Say '  delete still running in 1C after the edge deadline; waiting for Sync' }
Converge 'cleanup' $ref $before
Say 'R9 PASS'
