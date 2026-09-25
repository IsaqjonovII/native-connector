# Launch the desktop app on one screen, wait, capture its window, close it gracefully.
#   .\shoot.ps1 -Page browse -Theme dark -Width 900 -Out D:\...\x.png -Extra '--autorun'
param(
    [string] $Page = 'bases',
    [ValidateSet('light', 'dark')] [string] $Theme = 'light',
    [int] $Width = 1360,
    [int] $Height = 860,
    [int] $WaitSeconds = 12,
    [string[]] $Extra = @(),
    [Parameter(Mandatory)] [string] $Out
)
$exe = Join-Path $PSScriptRoot '..\src\OneC.Desktop\bin\Release\net9.0-windows10.0.19041.0\win-x64\AIBA.Connector.exe'
$argList = @('--page', $Page, '--theme', $Theme, '--width', $Width, '--height', $Height) + $Extra
# Start-Process joins arguments with spaces; quote any that contain one.
$argList = $argList | ForEach-Object { if ("$_" -match '\s') { '"' + $_ + '"' } else { "$_" } }
$p = Start-Process -FilePath (Resolve-Path $exe) -ArgumentList $argList -PassThru
try {
    Start-Sleep -Seconds $WaitSeconds
    & (Join-Path $PSScriptRoot 'capture-window.ps1') -ProcessId $p.Id -Out $Out
}
finally {
    if (-not $p.HasExited) { [void]$p.CloseMainWindow(); $p.WaitForExit(15000) | Out-Null }
    if (-not $p.HasExited) { $p.Kill() }
}
