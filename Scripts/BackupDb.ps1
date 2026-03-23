<#
.SYNOPSIS
    Trigger an on-demand backup of the PostgreSQL database.
.DESCRIPTION
    Uses Azure CLI to create a backup of the PostgreSQL Flexible Server.
#>
param(
    [string]$Stack = "dev"
)

$ErrorActionPreference = "Stop"

Write-Host "=== PostgreSQL On-Demand Backup ===" -ForegroundColor Cyan

# Get resource details from Pulumi outputs
$rgName = pulumi stack output resourceGroupName --stack $Stack 2>$null
$pgFqdn = pulumi stack output postgresServerFqdn --stack $Stack 2>$null

if (-not $rgName -or -not $pgFqdn) {
    Write-Host "Could not retrieve Pulumi outputs. Is the stack deployed?" -ForegroundColor Red
    exit 1
}

# Extract server name from FQDN
$serverName = $pgFqdn -replace '\.postgres\.database\.azure\.com$', ''

$backupName = "manual-$(Get-Date -Format 'yyyyMMdd-HHmmss')"

Write-Host "Server:  $serverName"
Write-Host "Backup:  $backupName"
Write-Host ""

az postgres flexible-server backup create `
    --resource-group $rgName `
    --name $serverName `
    --backup-name $backupName

if ($LASTEXITCODE -eq 0) {
    Write-Host "`nBackup '$backupName' created successfully." -ForegroundColor Green
} else {
    Write-Host "`nBackup failed." -ForegroundColor Red
    exit 1
}
