# R6 real multi-org (PYTHON_TO_RUST_MIGRATION_PLAN): KAN (kansler, the local server restore) synced
# READ-ONLY into the local Rust module, connection 30 with both organisations bound. No writes into
# KAN, no test documents there. Dot-source after rust-local.ps1's helpers:  . D:\aiba\1c-arch\tools\rust-kan.ps1
. D:\aiba\1c-arch\tools\rust-local.ps1
$Work  = Join-Path $RustData 'kan'
$Port  = 57752
$Bases = "$Work\bases.json"
if (-not (Test-Path "$Work\edge-token.txt")) { [guid]::NewGuid().ToString('N') | Set-Content "$Work\edge-token.txt" }
$EdgeToken = (Get-Content "$Work\edge-token.txt").Trim()

function New-KanConfig([string]$Conn = '30', [string]$From = '2026-06-01') {
    $tables = @(
        @{ table = 'ChartOfAccounts_Хозрасчетный'; name = 'Хозрасчетный'; family = 'chart'; isMovement = $false },
        @{ table = 'Catalog_Организации'; name = 'Организации'; family = 'catalog'; isMovement = $false },
        @{ table = 'Catalog_Банки'; name = 'Банки'; family = 'catalog'; isMovement = $false },
        @{ table = 'Document_РеализацияТоваровУслуг'; name = 'РеализацияТоваровУслуг'; family = 'document'; isMovement = $true; from = $From },
        @{ table = 'Document_ПоступлениеТоваровУслуг'; name = 'ПоступлениеТоваровУслуг'; family = 'document'; isMovement = $true; from = $From },
        @{ table = 'Document_ПлатежноеПоручениеИсходящее'; name = 'ПлатежноеПоручениеИсходящее'; family = 'document'; isMovement = $true; from = $From },
        @{ table = 'AccountingRegister_Хозрасчетный'; name = 'Хозрасчетный'; family = 'reg_accounting'; isMovement = $true; from = $From },
        @{ table = 'InformationRegister_КурсыВалют'; name = 'КурсыВалют'; family = 'reg_info_independent'; isMovement = $false })
    $cfg = [ordered]@{ db = "$Work\sync-kan.db"; target = "rust:$RustUrl"; secrets = $Secrets; faults = $false
                       bases = @(@{ name = 'kansler'; connectionId = $Conn; tables = $tables }) }
    $cfg | ConvertTo-Json -Depth 6 | Set-Content "$Work\sync.json" -Encoding utf8
}
