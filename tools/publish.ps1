# The Connector v1 distributable: one folder, nothing to install (no .NET on the target PC).
#   <Out>\AIBA.Connector.exe ...            the WinUI app (self-contained, unpackaged)
#   <Out>\runtime\OneC.Supervisor.exe ...   engine + edge (self-contained; ASP.NET Core inside)
#   <Out>\runtime\OneC.Host.exe ...         1C COM host, x64
# SupervisorProcess.FindRuntime looks for <app>\runtime first. Then the output is checked for anything
# that must never ship: local configs, secrets, test data.
#   pwsh tools\publish.ps1 [-Version 1.0.0] [-Out D:\aiba\1c-arch\out\connector]
param([string]$Version = '1.0.0', [string]$Out = (Join-Path (Split-Path $PSScriptRoot) 'out\connector'))
$ErrorActionPreference = 'Stop'
$Repo = Split-Path $PSScriptRoot
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', "-p:Version=$Version", "-p:InformationalVersion=$Version", '--nologo')
function Publish([string]$Project, [string]$To) {
    Write-Host "publish $Project -> $To"
    & dotnet publish (Join-Path $Repo $Project) @common -o $To
    if ($LASTEXITCODE -ne 0) { throw "publish $Project failed ($LASTEXITCODE)" }
}
Publish 'src\OneC.Desktop\OneC.Desktop.csproj' $Out
# Unpackaged WinUI: `dotnet publish` leaves out the app's own resources.pri and the compiled XAML (*.xbf)
# the build put next to the exe — the published app then dies at launch with XamlParseException in
# MainWindow.InitializeComponent (found by the clean-start test, 2026-10-09). Copy them from that same build.
$build = Get-ChildItem (Join-Path $Repo 'src\OneC.Desktop\bin\Release') -Directory -Filter 'net9.0-windows*' |
         ForEach-Object { Join-Path $_.FullName 'win-x64' } | Where-Object { Test-Path (Join-Path $_ 'AIBA.Connector.pri') } | Select-Object -First 1
if (-not $build) { throw 'the Desktop build output with AIBA.Connector.pri was not found' }
Copy-Item (Join-Path $build 'AIBA.Connector.pri') $Out -Force
Get-ChildItem $build -Recurse -Filter '*.xbf' | ForEach-Object {
    $to = Join-Path $Out $_.FullName.Substring($build.Length + 1)
    New-Item -ItemType Directory -Force (Split-Path $to) | Out-Null
    Copy-Item $_.FullName $to -Force
}
Write-Host "XAML resources: $((Get-ChildItem $Out -Recurse -Filter '*.xbf').Count) .xbf + AIBA.Connector.pri"
Publish 'src\OneC.Supervisor\OneC.Supervisor.csproj' (Join-Path $Out 'runtime')
Publish 'src\OneC.Host\OneC.Host.csproj' (Join-Path $Out 'runtime')

# Never ship: local base lists and configs (live 1C passwords), secrets, test state.
$bad = Get-ChildItem $Out -Recurse -File | Where-Object {
    $_.Name -match '(?i)(\.local\.json$|^config\.json$|bases\.json$|sync\.json$|session\.json$|edge-token|\.pfx$|\.db$)' -or
    ($_.Extension -notin '.dll', '.exe', '.pri', '.winmd', '.xbf' -and $_.Name -match '(?i)secret')
}
if ($bad) { $bad | ForEach-Object { Write-Host "MUST NOT SHIP: $($_.FullName)" }; throw 'secrets/config in the output' }
foreach ($exe in 'AIBA.Connector.exe', 'runtime\OneC.Supervisor.exe', 'runtime\OneC.Host.exe') {
    $f = Get-Item (Join-Path $Out $exe) -ErrorAction SilentlyContinue
    if (-not $f) { $alt = Get-ChildItem $Out -Filter '*.exe' -File | Select-Object -First 3 | ForEach-Object Name; throw "missing $exe (top-level exes: $($alt -join ', '))" }
    Write-Host ("{0,-32} {1}" -f $exe, $f.VersionInfo.ProductVersion)
}
$mb = [math]::Round(((Get-ChildItem $Out -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB)
Write-Host "OK: $Out ($mb MB)"
