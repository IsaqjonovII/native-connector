# R9 / D53: typed writes on CUSTOMER-created objects, round trip
#   Rust command → Supervisor → OneC.Host → 1C → Sync → Rust converges to 1C.
# bilim (the LOCAL test copy) only. Every change is put back: the customer document is reposted as it was
# (unpost + post), its Комментарий and the catalog item's name are changed by compare-and-set and then set
# back by compare-and-set. Refusals must change nothing. Supervisor up on New-RustConfig -Conn 13 -Commands.
. D:\aiba\1c-arch\tools\rust-local.ps1
$log = "$Out\r9-customer-run.txt"
$Conn = if ($env:R9_CONN) { $env:R9_CONN } else { '13' }        # R9_CONN / R9_BASE: another test base (e.g. the server copy bilimsrv)
$Base = if ($env:R9_BASE) { $env:R9_BASE } else { 'bilim' }
$DocType = 'ПоступлениеТоваровУслуг'
$Cat = 'Номенклатура'
function Say([string]$s) { "$(Get-Date -Format HH:mm:ss) $s" | Tee-Object -Append $log }
function Fingerprint([string]$ref) {
    (Sql "select coalesce(string_agg(table_name || row_key || data_hash, ',' order by table_name, row_key), '') from onec.entity_data where onec_id = $Conn and (row_key = '$ref' or recorder_key = '$ref')") -join ''
}
function Command([string]$Kind, [string]$Key, [hashtable]$Payload) {
    $f = "$Work\r9u-payload.json"
    [IO.File]::WriteAllText($f, ($Payload | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding $false))
    $j = ((& $Sup sync-rust command --secrets $Secrets --connection $Conn --kind $Kind --key $Key --payload $f --wait 3600 2>&1) | Select-Object -Last 1) | ConvertFrom-Json
    Say "  command $Kind [$Key]: state $($j.state)$(if ($j.enqueued -eq $false) { " refused by Rust: $($j.message)" }) result $(($j.result | ConvertTo-Json -Compress -Depth 5)) error $(if ($j.error) { "$($j.error.kind)/$($j.error.layer): $($j.error.message) $(($j.error.data.fillDiagnostics | ConvertTo-Json -Compress -Depth 4))" })"
    $j
}
function Expect($j, [string]$state, [string]$what) { if ($j.state -ne $state -and -not ($state -eq 'refused' -and $j.enqueued -eq $false)) { Say "  FAIL: $what — got $($j.state)"; throw $what } }
function Converge([string]$Tag, [string]$ref, [string]$before) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ((Fingerprint $ref) -eq $before -and $sw.Elapsed.TotalMinutes -lt 20) { Start-Sleep 2 }
    if ((Fingerprint $ref) -eq $before) { Say "  FAIL: Rust did not change within 20 min"; throw "$Tag not observed" }
    Say "  Sync brought it into Rust after $([math]::Round($sw.Elapsed.TotalSeconds, 1)) s"
    Wait-Idle 1800 | Out-Null
    Verify "r9u-$Tag"
    Say "  verify r9u-$Tag PASS (Rust = 1C)"
}
function Unchanged([string]$ref, [string]$before, [string]$what) {
    Start-Sleep 8
    if ((Fingerprint $ref) -ne $before) { Say "  FAIL: $what changed data"; throw $what }
    Say "  $what changed nothing"
}

$stamp = Get-Date -Format MMddHHmmss
Say "=== R9 customer-owned objects (D53) $stamp"
# A posted customer document (no AIBA marker) and a customer catalog item, as Rust holds them.
$doc = Sql "select row_key || '|' || coalesce(raw->>'Комментарий', '') from onec.entity_data where onec_id = $Conn and table_name = 'Document_ПоступлениеТоваровУслуг' and raw->>'posted' = 'true' and coalesce(raw->>'Комментарий', '') not like 'AIBA%' order by row_key limit 1"
$dref, $dcomment = $doc -split '\|', 2
$item = Sql "select row_key || '|' || coalesce(raw->>'name', raw->>'Наименование', raw->>'description', '') from onec.entity_data where onec_id = $Conn and table_name = 'Catalog_Номенклатура' and coalesce(raw->>'Комментарий', '') not like 'AIBA%' order by row_key limit 1"
$iref, $iname = $item -split '\|', 2
Say "  customer document $dref (comment '$dcomment'), customer item $iref ('$iname')"
if (-not $dref -or -not $iref -or -not $iname) { throw 'no customer objects found' }

Say '--- post an existing customer document (repost: same movements)'
# A repost can leave Rust's rows as they were, so wait for Sync to read the event, not for a change.
$seen = (Status).lastEventAt
$j = Command 'document.post' "r9u-post-$stamp" @{ docType = $DocType; ref = $dref }; Expect $j 'succeeded' 'customer post'
$sw = [Diagnostics.Stopwatch]::StartNew()
while ((Status).lastEventAt -eq $seen -and $sw.Elapsed.TotalMinutes -lt 20) { Start-Sleep 2 }
if ((Status).lastEventAt -eq $seen) { Say '  FAIL: Sync did not see the post within 20 min'; throw 'post not observed' }
Say "  Sync saw the post after $([math]::Round($sw.Elapsed.TotalSeconds, 1)) s"
Wait-Idle 1800 | Out-Null; Verify 'r9u-post'; Say '  verify r9u-post PASS (Rust = 1C)'

Say '--- unpost it, then post it back'
$before = Fingerprint $dref
$j = Command 'document.unpost' "r9u-unpost-$stamp" @{ docType = $DocType; ref = $dref }; Expect $j 'succeeded' 'customer unpost'
Converge 'unpost' $dref $before
$before = Fingerprint $dref
$j = Command 'document.post' "r9u-repost-$stamp" @{ docType = $DocType; ref = $dref }; Expect $j 'succeeded' 'customer repost'
Converge 'repost' $dref $before

Say '--- typed update of the customer document: compare-and-set only'
$before = Fingerprint $dref
$j = Command 'document.update' "r9u-blind-$stamp" @{ docType = $DocType; ref = $dref; fields = @{ 'Комментарий' = 'r9 blind update' } }; Expect $j 'failed' 'blind update of a customer document'
$j = Command 'document.update' "r9u-stale-$stamp" @{ docType = $DocType; ref = $dref; fields = @{ 'Комментарий' = 'r9 stale' }; expected = @{ 'Комментарий' = 'not what 1C holds' } }; Expect $j 'failed' 'stale update'
$j = Command 'document.update' "r9u-claim-$stamp" @{ docType = $DocType; ref = $dref; fields = @{ 'Комментарий' = "AIBA_REWRITE_X: claimed" }; expected = @{ 'Комментарий' = $dcomment } }; Expect $j 'failed' 'claiming ownership'
Unchanged $dref $before 'the three refused updates'
$j = Command 'document.update' "r9u-cas-$stamp" @{ docType = $DocType; ref = $dref; fields = @{ 'Комментарий' = "$dcomment [r9 $stamp]" }; expected = @{ 'Комментарий' = $dcomment }; autoUnpost = $true }; Expect $j 'succeeded' 'customer CAS update'
Converge 'doc-cas' $dref $before
$before = Fingerprint $dref
$j = Command 'document.update' "r9u-cas-back-$stamp" @{ docType = $DocType; ref = $dref; fields = @{ 'Комментарий' = $dcomment }; expected = @{ 'Комментарий' = "$dcomment [r9 $stamp]" }; autoUnpost = $true }; Expect $j 'succeeded' 'customer CAS update back'
Converge 'doc-cas-back' $dref $before

Say '--- still refused on customer documents: mark for deletion'
$before = Fingerprint $dref
$j = Command 'document.markDeleted' "r9u-mark-$stamp" @{ docType = $DocType; ref = $dref }; Expect $j 'failed' 'customer deletion mark'
Unchanged $dref $before 'the refused deletion mark'

Say '--- catalog compare-and-set on a customer item'
$before = Fingerprint $iref
$j = Command 'catalog.update' "r9u-cblind-$stamp" @{ catalog = $Cat; ref = $iref; set = @{ 'Наименование' = 'blind' } }; Expect $j 'refused' 'catalog update without expected'
$j = Command 'catalog.update' "r9u-cstale-$stamp" @{ catalog = $Cat; ref = $iref; expected = @{ 'Наименование' = 'not what 1C holds' }; set = @{ 'Наименование' = 'stale' } }; Expect $j 'failed' 'stale catalog CAS'
$j = Command 'catalog.update' "r9u-cclaim-$stamp" @{ catalog = $Cat; ref = $iref; expected = @{ 'Наименование' = $iname }; set = @{ 'Комментарий' = 'AIBA_REWRITE_X: claimed' } }; Expect $j 'failed' 'claiming the item'
Unchanged $iref $before 'the refused catalog writes'
$j = Command 'catalog.update' "r9u-ccas-$stamp" @{ catalog = $Cat; ref = $iref; expected = @{ 'Наименование' = $iname }; set = @{ 'Наименование' = "$iname [r9]" } }; Expect $j 'succeeded' 'customer catalog CAS'
# A rename: documents that use the item show its NAME in their table parts, and 1C changes none of them.
# Sync must find exactly those (ReferrerSearch) and re-read them, so the WHOLE base matches 1C right after.
$users = Sql "select count(*) from onec.entity_data where onec_id = $Conn and table_name like 'Document_%' and strpos(raw::text, '$($iname -replace "'", "''")') > 0"
Say "  documents in Rust showing '$iname' before the rename: $users"
$sw = [Diagnostics.Stopwatch]::StartNew()
while ((Sql "select raw->>'name' from onec.entity_data where onec_id = $Conn and row_key = '$iref'") -ne "$iname [r9]" -and $sw.Elapsed.TotalMinutes -lt 20) { Start-Sleep 2 }
if ((Sql "select raw->>'name' from onec.entity_data where onec_id = $Conn and row_key = '$iref'") -ne "$iname [r9]") { Say '  FAIL: the renamed item did not reach Rust'; throw 'cat-cas not observed' }
Say "  Sync brought the renamed item into Rust after $([math]::Round($sw.Elapsed.TotalSeconds, 1)) s"
Wait-Idle 1800 | Out-Null
$renamed = Sql "select count(*) from onec.entity_data where onec_id = $Conn and table_name like 'Document_%' and strpos(raw::text, '$("$iname [r9]" -replace "'", "''")') > 0"
Say "  documents in Rust showing the new name: $renamed (of $users)"
Verify 'r9u-cat-cas'
Say '  verify r9u-cat-cas PASS (Rust = 1C, the referencing documents included)'
$before = Fingerprint $iref
$j = Command 'catalog.update' "r9u-ccas-back-$stamp" @{ catalog = $Cat; ref = $iref; expected = @{ 'Наименование' = "$iname [r9]" }; set = @{ 'Наименование' = $iname } }; Expect $j 'succeeded' 'customer catalog CAS back'
Converge 'cat-cas-back' $iref $before
Say 'R9 customer-owned PASS'
