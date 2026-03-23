<#
.SYNOPSIS
    Initialize pretix and pretalx after first deployment.
.DESCRIPTION
    Runs database migrations and creates initial admin users inside the running containers.
#>
param(
    [string]$Stack = "dev"
)

$ErrorActionPreference = "Stop"

Write-Host "=== Post-Deploy Initialization ===" -ForegroundColor Cyan

# Get resource details from Pulumi outputs
$rgName = pulumi stack output resourceGroupName --stack $Stack 2>$null
$prefix = pulumi config get prefix --stack $Stack 2>$null

if (-not $rgName -or -not $prefix) {
    Write-Host "Could not retrieve Pulumi outputs. Is the stack deployed?" -ForegroundColor Red
    exit 1
}

# Initialize Pretix
Write-Host "`n--- Pretix: Running migrations ---" -ForegroundColor Cyan
az containerapp exec `
    --resource-group $rgName `
    --name "$prefix-pretix" `
    --command "pretix migrate"

Write-Host "`n--- Pretix: Rebuilding static files ---" -ForegroundColor Cyan
az containerapp exec `
    --resource-group $rgName `
    --name "$prefix-pretix" `
    --command "pretix rebuild"

# Initialize Pretalx
Write-Host "`n--- Pretalx: Running migrations ---" -ForegroundColor Cyan
az containerapp exec `
    --resource-group $rgName `
    --name "$prefix-pretalx" `
    --command "pretalx migrate"

Write-Host "`n--- Pretalx: Rebuilding static files ---" -ForegroundColor Cyan
az containerapp exec `
    --resource-group $rgName `
    --name "$prefix-pretalx" `
    --command "pretalx rebuild"

Write-Host "`n=== Initialization complete ===" -ForegroundColor Green
Write-Host @"

Next steps:
  1. Open the pretix URL and create your first organizer + event
  2. Open the pretalx URL and create your first organizer + event
  3. Configure custom domains:  pulumi config set pretixUrl https://tickets.yourdomain.com
  4. Set up SMTP:               pulumi config set --secret smtpPassword <your-smtp-password>

"@ -ForegroundColor Yellow
