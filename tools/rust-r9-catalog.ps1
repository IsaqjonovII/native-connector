# R9 catalogs (PYTHON_TO_RUST_MIGRATION_PLAN): create + compare-and-set update, round trip
#   Rust command → Supervisor → OneC.Host → 1C → Sync → Rust converges to 1C.
# bilim, one test-owned Номенклатура item (AIBA_REWRITE_R9C_ marker in Комментарий). Supervisor up on
# New-RustConfig -Conn 13 -Commands.
. D:\aiba\1c-arch\tools\rust-local.ps1
$log = "$Out\r9-catalog-run.txt"
$Conn = '13'
$Cat = 'Номенклатура'
function Say([string]$s) { "$(Get-Date -Format HH:mm:ss) $s" | Tee-Object -Append $log }
function Fingerprint([string]$ref) {
    if (-not $ref) { return '' }
    (Sql "select coalesce(string_agg(table_name || row_key || data_hash, ','), '') from onec.entity_data where onec_id = 13 and row_key = '$ref'") -join ''
}
function Command([string]$Kind, [string]$Key, [hashtable]$Payload) {
    $f = "$Work\r9c-payload.json"
    [IO.File]::WriteAllText($f, ($Payload | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding $false))
    $j = ((& $Sup sync-rust command --secrets $Secrets --connection $Conn --kind $Kind --key $Key --payload $f --wait 600 2>&1) | Select-Object -Last 1) | ConvertFrom-Json
    Say "  command $Kind [$Key]: state $($j.state)$(if ($j.deduplicated) { ' (deduplicated)' })$(if ($j.enqueued -eq $false) { " refused by Rust: $($j.message)" }) result $(($j.result | ConvertTo-Json -Compress -Depth 5)) error $(if ($j.error) { "$($j.error.kind)/$($j.error.layer): $($j.error.message) $(($j.error.data.diagnostics | ConvertTo-Json -Compress -Depth 4))" })"
    $j
}
function Converge([string]$Tag, [string]$ref, [string]$before) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ((Fingerprint $ref) -eq $before -and $sw.Elapsed.TotalMinutes -lt 20) { Start-Sleep 2 }
    if ((Fingerprint $ref) -eq $before) { Say "  FAIL: Rust did not change within 20 min"; throw "$Tag not observed" }
    Say "  Sync brought it into Rust after $([math]::Round($sw.Elapsed.TotalSeconds, 1)) s; Rust: $(Sql "select coalesce(max(raw->>'Наименование'), '(gone)') from onec.entity_data where onec_id = 13 and row_key = '$ref'")"
    Wait-Idle 1800 | Out-Null
    Verify "r9c-$Tag"
    Say "  verify r9c-$Tag PASS (Rust = 1C)"
}

$stamp = Get-Date -Format MMddHHmmss
$marker = "AIBA_REWRITE_R9C_$stamp"
$name1 = "AIBA test item $stamp"
$name2 = "AIBA test item $stamp (renamed)"
Say "=== R9 catalogs $marker"

$c = Command 'catalog.create' "r9c-create-$stamp" @{ catalog = $Cat; body = @{ 'Наименование' = $name1; 'Комментарий' = "${marker}: rust command"; '_idempotencyMarker' = $marker } }
if ($c.state -ne 'succeeded') { throw 'catalog create failed' }
$ref = $c.result.id.ToLower()
Converge 'create' $ref ''
$twin = Command 'catalog.create' "r9c-create2-$stamp" @{ catalog = $Cat; body = @{ 'Наименование' = $name1; 'Комментарий' = "${marker}: rust command"; '_idempotencyMarker' = $marker } }
if ($twin.result.id.ToLower() -ne $ref -or -not $twin.result.idempotent) { throw 'the marker must find the first item' }
Say "  idempotent: a new key with the same marker returned the same item"

Say '--- compare-and-set'
$before = Fingerprint $ref
$u = Command 'catalog.update' "r9c-cas-$stamp" @{ catalog = $Cat; ref = $ref; expected = @{ 'Наименование' = $name1 }; set = @{ 'Наименование' = $name2 } }
if ($u.state -ne 'succeeded') { throw 'CAS failed' }
Converge 'cas' $ref $before

Say '--- a stale compare-and-set is refused and changes nothing'
$before = Fingerprint $ref
$stale = Command 'catalog.update' "r9c-stale-$stamp" @{ catalog = $Cat; ref = $ref; expected = @{ 'Наименование' = $name1 }; set = @{ 'Наименование' = 'must not be written' } }
if ($stale.state -ne 'failed') { throw 'a stale CAS must fail' }
$foreign = Sql "select row_key from onec.entity_data where onec_id = 13 and table_name = 'Catalog_Номенклатура' and coalesce(raw->>'Комментарий', '') not like 'AIBA%' order by row_key limit 1"
$f = Command 'catalog.update' "r9c-foreign-$stamp" @{ catalog = $Cat; ref = $foreign; set = @{ 'Наименование' = 'must not be written' } }
if ($f.state -ne 'failed') { throw 'an item AIBA did not create must not change' }
$b = Command 'catalog.create' "r9c-bank-$stamp" @{ catalog = 'Банки'; body = @{ 'Наименование' = 'x'; 'Комментарий' = "${marker}B: x" } }
if ($b.state -ne 'failed') { throw 'a catalog without Комментарий cannot hold an owned item' }
Start-Sleep 10
if ((Fingerprint $ref) -ne $before) { throw 'a refused CAS changed the item' }
Say '  refusals returned with 1C/host reasons; nothing changed'

Say '--- cleanup (owned delete through the local edge)'
$before = Fingerprint $ref
$path = '/v1/bases/bilim/catalogs/' + [Uri]::EscapeDataString($Cat) + "/$ref"
$r = Invoke-WebRequest -Uri "http://127.0.0.1:$Port$path" -Method DELETE -Headers @{ 'X-AIBA-Token' = $EdgeToken } -UseBasicParsing -TimeoutSec 600
Say "  1C: $([Text.Encoding]::UTF8.GetString($r.RawContentStream.ToArray()))"
Converge 'cleanup' $ref $before
Say 'R9 catalogs PASS'
