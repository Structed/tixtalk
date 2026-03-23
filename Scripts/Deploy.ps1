<#
.SYNOPSIS
    First-time deployment of Pretix + Pretalx infrastructure to Azure.
.DESCRIPTION
    Checks prerequisites and runs 'pulumi up' to provision all resources.
.PARAMETER Stack
    Pulumi stack name (default: dev)
#>
param(
    [string]$Stack = "dev"
)

$ErrorActionPreference = "Stop"

Write-Host "=== Pretix + Pretalx Azure Deployment ===" -ForegroundColor Cyan

# Check prerequisites
$missing = @()
if (-not (Get-Command pulumi -ErrorAction SilentlyContinue))  { $missing += "pulumi" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue))  { $missing += "dotnet" }
if (-not (Get-Command az -ErrorAction SilentlyContinue))      { $missing += "az (Azure CLI)" }

if ($missing.Count -gt 0) {
    Write-Host "Missing prerequisites: $($missing -join ', ')" -ForegroundColor Red
    Write-Host "Install them and try again." -ForegroundColor Red
    exit 1
}

# Verify Azure login
$azAccount = az account show 2>$null | ConvertFrom-Json
if (-not $azAccount) {
    Write-Host "Not logged in to Azure. Running 'az login'..." -ForegroundColor Yellow
    az login
}
Write-Host "Azure subscription: $($azAccount.name) ($($azAccount.id))" -ForegroundColor Green

# Verify Pulumi login
$pulumiUser = pulumi whoami 2>$null
if (-not $pulumiUser) {
    Write-Host "Not logged in to Pulumi. Running 'pulumi login'..." -ForegroundColor Yellow
    pulumi login
}

# Select or create stack
pulumi stack select $Stack 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Creating stack '$Stack'..." -ForegroundColor Yellow
    pulumi stack init $Stack
}

# Build the project
Write-Host "`nBuilding .NET project..." -ForegroundColor Cyan
dotnet build --configuration Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

# Deploy
Write-Host "`nDeploying infrastructure..." -ForegroundColor Cyan
Write-Host "This will create Azure resources. Review the plan below.`n" -ForegroundColor Yellow
pulumi up

Write-Host "`n=== Deployment complete ===" -ForegroundColor Green
Write-Host "Run .\Scripts\InitApps.ps1 to initialize pretix and pretalx." -ForegroundColor Cyan
