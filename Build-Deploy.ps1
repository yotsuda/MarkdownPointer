<#
.SYNOPSIS
    Build and deploy MarkdownPointer PowerShell module.
.DESCRIPTION
    Builds the MarkdownPointer application and MCP server, then deploys
    the PowerShell module to the system modules directory.

.PARAMETER AppOnly
    Build and deploy only the App (skip MCP server).
.PARAMETER SkipBuild
    Skip the build step and only deploy existing outputs.
.EXAMPLE
    .\Build-Deploy.ps1
    # Full build and deploy
.EXAMPLE
    .\Build-Deploy.ps1 -AppOnly
    # Build and deploy App only
#>
[CmdletBinding()]
param(
    [switch]$AppOnly,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ProjectRoot = $PSScriptRoot
$AppProject = Join-Path $ProjectRoot 'MarkdownPointer\MarkdownPointer.App.csproj'
$McpProject = Join-Path $ProjectRoot 'MarkdownPointer.Mcp\MarkdownPointer.Mcp.csproj'
$ModuleDir = Join-Path $ProjectRoot 'Module'
$BuildDir = Join-Path $ProjectRoot 'dist'

$InstallDir = Join-Path $env:ProgramFiles 'PowerShell\7\Modules\MarkdownPointer'
$InstallBinDir = Join-Path $InstallDir 'bin'

# Get version from App project
$csprojContent = Get-Content $AppProject -Raw
if ($csprojContent -match '<Version>([^<]+)</Version>') {
    $Version = $Matches[1]
} else {
    $Version = '0.1.0'
}

Write-Host '=== MarkdownPointer Build & Deploy ===' -ForegroundColor Cyan
Write-Host "Version: $Version" -ForegroundColor DarkGray

# Step 1: Stop running processes
Write-Host "`n[1/4] Stopping running processes..." -ForegroundColor Yellow
$names = if ($AppOnly) { @('mdp') } else { @('mdp', 'mdp-mcp') }
$processes = @(Get-Process -Name $names -ErrorAction Ignore)
if ($processes.Count -gt 0) {
    $processes | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    Write-Host "      Stopped $($processes.Count) process(es)." -ForegroundColor Green
} else {
    Write-Host '      No running processes found.' -ForegroundColor DarkGray
}

# Step 2: Build
if (-not $SkipBuild) {
    Write-Host "`n[2/4] Building projects..." -ForegroundColor Yellow

    Write-Host "      Building MarkdownPointer.App..." -ForegroundColor DarkGray
    dotnet publish $AppProject -c Release -r win-x64 --no-self-contained -o "$BuildDir\app"
    if ($LASTEXITCODE -ne 0) { throw "App build failed" }

    if (-not $AppOnly) {
        Write-Host "      Building MarkdownPointer.Mcp..." -ForegroundColor DarkGray
        dotnet publish $McpProject -c Release -r win-x64 --no-self-contained -o "$BuildDir\mcp"
        if ($LASTEXITCODE -ne 0) { throw "MCP build failed" }
    }

    Write-Host '      Build succeeded.' -ForegroundColor Green
} else {
    Write-Host "`n[2/4] Skipping build (SkipBuild specified)" -ForegroundColor DarkGray
}

# Step 3: Sync module version
Write-Host "`n[3/4] Syncing module version..." -ForegroundColor Yellow
$psd1Path = Join-Path $ModuleDir 'MarkdownPointer.psd1'
$psd1Content = Get-Content $psd1Path -Raw
$psd1Content = $psd1Content -replace "ModuleVersion\s*=\s*'[^']*'", "ModuleVersion = '$Version'"
Set-Content $psd1Path -Value $psd1Content -NoNewline
Write-Host "      ModuleVersion = '$Version'" -ForegroundColor DarkGray

# Step 4: Deploy to module directory
Write-Host "`n[4/4] Deploying to $InstallDir ..." -ForegroundColor Yellow

if (-not (Test-Path $InstallBinDir)) {
    New-Item $InstallBinDir -ItemType Directory -Force | Out-Null
}

# Module files
Copy-Item "$ModuleDir\MarkdownPointer.psd1" $InstallDir -Force
Copy-Item "$ModuleDir\MarkdownPointer.psm1" $InstallDir -Force
Copy-Item "$ModuleDir\LICENSE" $InstallDir -Force -ErrorAction SilentlyContinue

# App binary
Copy-Item "$BuildDir\app\mdp.exe" $InstallBinDir -Force

# MCP binary
if (-not $AppOnly) {
    $mcpExe = Get-ChildItem "$BuildDir\mcp" -Filter '*.exe' | Select-Object -First 1
    Copy-Item $mcpExe.FullName "$InstallBinDir\mdp-mcp.exe" -Force
}

Write-Host "`n=== Deployed to $InstallDir ===" -ForegroundColor Green
Get-ChildItem "$InstallBinDir\*.exe" | ForEach-Object {
    $size = if ($_.Length -gt 1MB) { "{0:N1} MB" -f ($_.Length / 1MB) } else { "{0:N0} KB" -f ($_.Length / 1KB) }
    Write-Host "  $($_.Name) ($size)" -ForegroundColor Gray
}
