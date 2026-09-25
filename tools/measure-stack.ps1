# Whole-stack memory: the desktop app, its supervisor and every 1C host, sampled from outside.
#   .\measure-stack.ps1 -Label idle -Page resources
#   .\measure-stack.ps1 -Label after-read -Page browse -Extra '--autorun','--base','kansler'
param(
    [string] $Label = 'sample',
    [string] $Page = 'resources',
    [int] $WaitSeconds = 20,
    [string[]] $Extra = @()
)
$exe = Resolve-Path (Join-Path $PSScriptRoot '..\src\OneC.Desktop\bin\Release\net9.0-windows10.0.19041.0\win-x64\AIBA.Connector.exe')
$argList = @('--page', $Page) + $Extra | ForEach-Object { if ("$_" -match '\s') { '"' + $_ + '"' } else { "$_" } }
$app = Start-Process -FilePath $exe -ArgumentList $argList -PassThru
try {
    Start-Sleep -Seconds $WaitSeconds
    # Only THIS app's tree: app → supervisor → hosts. Other OneC.Host processes on the machine
    # (tests, benchmarks) are not part of the stack being measured.
    $all = Get-CimInstance Win32_Process
    $sup = $all | Where-Object { $_.ParentProcessId -eq $app.Id -and $_.Name -eq 'OneC.Supervisor.exe' }
    $treeIds = @($app.Id) + @($sup.ProcessId) + @($all | Where-Object { $sup.ProcessId -contains $_.ParentProcessId } | ForEach-Object ProcessId)
    $rows = foreach ($name in 'AIBA.Connector', 'OneC.Supervisor', 'OneC.Host') {
        Get-Process $name -EA SilentlyContinue | Where-Object { $treeIds -contains $_.Id } | ForEach-Object {
            $_.Refresh()
            [pscustomobject]@{
                Label = $Label; Process = $name; Pid = $_.Id
                WorkingSetMB = [math]::Round($_.WorkingSet64 / 1MB)
                PrivateMB = [math]::Round($_.PrivateMemorySize64 / 1MB)
                Threads = $_.Threads.Count; Handles = $_.HandleCount
                CpuSeconds = [math]::Round($_.CPU, 1)
            }
        }
    }
    $rows | Format-Table -AutoSize | Out-String -Width 200
    "TOTAL working set: {0} MB   private: {1} MB" -f ($rows | Measure-Object WorkingSetMB -Sum).Sum, ($rows | Measure-Object PrivateMB -Sum).Sum
}
finally {
    if (-not $app.HasExited) { [void]$app.CloseMainWindow(); $app.WaitForExit(15000) | Out-Null }
    if (-not $app.HasExited) { $app.Kill() }
    Start-Sleep -Seconds 2
    $left = @($treeIds | Where-Object { $_ -ne $app.Id } | Where-Object { Get-Process -Id $_ -EA SilentlyContinue })
    "after close: {0} of the app's supervisor/host processes still running" -f $left.Count
}
