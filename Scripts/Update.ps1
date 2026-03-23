<#
.SYNOPSIS
    Update pretix and/or pretalx container images to a new version.
.DESCRIPTION
    Sets the new image tag in Pulumi config and runs 'pulumi up' for a rolling update.
.PARAMETER PretixTag
    New pretix image tag (e.g. "2025.1.0", "stable")
.PARAMETER PretalxTag
    New pretalx image tag (e.g. "2025.1.0", "latest")
.PARAMETER AutoApprove
    Skip the Pulumi confirmation prompt.
#>
param(
    [string]$PretixTag,
    [string]$PretalxTag,
    [switch]$AutoApprove
)

$ErrorActionPreference = "Stop"

if (-not $PretixTag -and -not $PretalxTag) {
    Write-Host "Usage: .\Update.ps1 -PretixTag <tag> [-PretalxTag <tag>] [-AutoApprove]" -ForegroundColor Yellow
    Write-Host "Example: .\Update.ps1 -PretixTag 2025.1.0" -ForegroundColor Yellow
    exit 1
}

Write-Host "=== Container Image Update ===" -ForegroundColor Cyan

if ($PretixTag) {
    Write-Host "Setting pretix image tag to: $PretixTag" -ForegroundColor Green
    pulumi config set pretixImageTag $PretixTag
}

if ($PretalxTag) {
    Write-Host "Setting pretalx image tag to: $PretalxTag" -ForegroundColor Green
    pulumi config set pretalxImageTag $PretalxTag
}

Write-Host "`nBuilding..." -ForegroundColor Cyan
dotnet build --configuration Release --quiet
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "Deploying update..." -ForegroundColor Cyan
if ($AutoApprove) {
    pulumi up --yes
} else {
    pulumi up
}

Write-Host "`n=== Update complete ===" -ForegroundColor Green
