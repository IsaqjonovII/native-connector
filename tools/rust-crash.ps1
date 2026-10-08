# R6 recovery (PYTHON_TO_RUST_MIGRATION_PLAN): kill the Rust module while a 2000-row batch is in
# flight; after restart the table must hold all of the batch or none of it, and a replay of the same
# batch id must end with exactly the batch stored. Uses the RELEASE instance on 18113 only (the engine
# keeps using 18112), its own fresh connection.
param([int]$Rounds = 6)
. D:\aiba\1c-arch\tools\rust-local.ps1
$s = Get-Content $Secrets -Raw | ConvertFrom-Json
$url = 'http://127.0.0.1:18113'
$h = @{ 'X-Service-Secret' = $s.service }
$runner = Join-Path $RustData 'run-onec-release.ps1'
function Up { Start-Process pwsh -ArgumentList '-NoProfile', '-File', "`"$runner`"" -WindowStyle Hidden
    for ($i = 0; $i -lt 60; $i++) { try { Invoke-RestMethod "$url/health" | Out-Null; return } catch { Start-Sleep -Milliseconds 500 } }; throw 'release instance did not come up' }
function Down { Get-Process aiba-onec-release -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 500 }
$conn = (Invoke-RestMethod "$url/api/v1/onec" -Method Post -Headers $h -ContentType 'application/json' -Body '{"companyId":4998,"provider":"unisoft","name":"AIBA_REWRITE_crash"}').id
$table = 'AccountingRegister_Crash_RecordType'
$pad = 'x' * 1500
$results = @()
for ($r = 1; $r -le $Rounds; $r++) {
    $rec = [guid]::NewGuid().ToString()
    $rows = (1..2000 | ForEach-Object { "{`"key`":`"$rec#$_`",`"version`":1,`"data`":{`"recorderRef`":`"$rec`",`"lineNo`":$_,`"pad`":`"$pad`"}}" }) -join ','
    $batch = "b-crash-$rec"
    $body = [Text.Encoding]::UTF8.GetBytes("{`"batchId`":`"$batch`",`"partition`":`"$conn`",`"table`":`"$table`",`"rows`":[$rows]}")
    if ($r % 2 -eq 1) {
        # Odd rounds: the request is held INSIDE its transaction (sync_batch row written, rows not yet)
        # by holding the batch's own advisory lock from another session, then the module dies.
        $lockSql = Join-Path $env:TEMP "aiba-crash-lock.sql"
        [IO.File]::WriteAllText($lockSql, "select pg_advisory_lock(hashtextextended('rows:${conn}:${table}', 0)); select pg_sleep(8);", (New-Object Text.UTF8Encoding $false))
        $locker = Start-Process $Psql -ArgumentList '-h', '127.0.0.1', '-p', '55440', '-U', 'postgres', '-d', 'onec_rust_sync_test', '-f', "`"$lockSql`"" -WindowStyle Hidden -PassThru
        Start-Sleep -Milliseconds 300
        $job = Start-Job { param($u, $b, $sec) try { Invoke-RestMethod $u -Method Post -Headers @{ 'X-Service-Secret' = $sec } -ContentType 'application/json' -Body $b -TimeoutSec 60 | ConvertTo-Json -Compress } catch { "ERR $($_.Exception.Message)" } } `
            -ArgumentList "$url/api/sync/v1/connections/$conn/rows", $body, $s.service
        for ($w = 0; $w -lt 40 -and -not (Sql "select 1 from pg_stat_activity where wait_event_type = 'Lock' and query like '%pg_advisory_xact_lock%'"); $w++) { Start-Sleep -Milliseconds 150 }
        $held = [bool](Sql "select 1 from pg_stat_activity where wait_event_type = 'Lock' and query like '%pg_advisory_xact_lock%'")
        Down
        $locker.WaitForExit(15000) | Out-Null
    } else {
        $job = Start-Job { param($u, $b, $sec) try { Invoke-RestMethod $u -Method Post -Headers @{ 'X-Service-Secret' = $sec } -ContentType 'application/json' -Body $b -TimeoutSec 60 | ConvertTo-Json -Compress } catch { "ERR $($_.Exception.Message)" } } `
            -ArgumentList "$url/api/sync/v1/connections/$conn/rows", $body, $s.service
        Start-Sleep -Milliseconds (Get-Random -Minimum 900 -Maximum 2600)
        $held = $false
        Down
    }
    $first = Receive-Job $job -Wait; Remove-Job $job
    Up
    $after = [int](Sql "select count(*) from onec.entity_data where onec_id = $conn and recorder_key = '$rec'")
    $batchRow = [int](Sql "select count(*) from onec.sync_batch where batch_id = '$batch'")
    $replay = Invoke-RestMethod "$url/api/sync/v1/connections/$conn/rows" -Method Post -Headers $h -ContentType 'application/json' -Body $body -TimeoutSec 120
    $final = [int](Sql "select count(*) from onec.entity_data where onec_id = $conn and recorder_key = '$rec'")
    $ok = ($after -eq 0 -or $after -eq 2000) -and ($batchRow -eq [int]($after -eq 2000)) -and $final -eq 2000
    $results += [pscustomobject]@{ round = $r; heldInTx = $held; firstAnswer = ($first -replace '(.{60}).*', '$1'); afterCrash = $after; batchRecorded = $batchRow
                                   replayed = $replay.replayed; replayInserted = $replay.counts.inserted; final = $final; ok = $ok }
}
$results | Format-Table -AutoSize | Out-String -Width 220
if ($results.ok -contains $false) { 'CRASH TEST FAIL' } else { 'CRASH TEST PASS' }

