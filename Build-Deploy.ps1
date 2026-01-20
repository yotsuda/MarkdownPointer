<#
.SYNOPSIS
    Build and deploy MarkdownViewer to PowerShell module directory.
.DESCRIPTION
    Builds the MarkdownViewer project in Release configuration,
    stops any running MarkdownViewer processes, and copies the
    output to the PowerShell module directory.
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ProjectRoot = $PSScriptRoot
$ProjectFile = Join-Path $ProjectRoot 'MarkdownViewer\MarkdownViewer.App.csproj'
$BuildOutput = Join-Path $ProjectRoot 'MarkdownViewer\bin\Release\net8.0-windows\win-x64'
$ModuleSource = Join-Path $ProjectRoot 'Module'
$DeployTarget = 'C:\Program Files\PowerShell\7\Modules\MarkdownViewer'
$BinTarget = Join-Path $DeployTarget 'bin'

# Files to copy to bin folder (exclude xml documentation)
$BinFiles = @(
    'MarkdownViewer.exe'
    'MarkdownViewer.dll'
    'MarkdownViewer.deps.json'
    'MarkdownViewer.runtimeconfig.json'
    'Markdig.dll'
    'Microsoft.Web.WebView2.Core.dll'
    'Microsoft.Web.WebView2.WinForms.dll'
    'Microsoft.Web.WebView2.Wpf.dll'
    'WebView2Loader.dll'
)

# Module files to copy to root
$ModuleFiles = @(
    'MarkdownViewer.psd1'
    'MarkdownViewer.psm1'
    'LICENSE'
)

Write-Host '=== MarkdownViewer Build & Deploy ===' -ForegroundColor Cyan

# Step 1: Build
if (-not $SkipBuild) {
    Write-Host "`n[1/3] Building project..." -ForegroundColor Yellow
    dotnet build $ProjectFile -c Release --no-incremental
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed with exit code $LASTEXITCODE"
    }
    Write-Host '      Build succeeded.' -ForegroundColor Green
} else {
    Write-Host "`n[1/3] Skipping build (SkipBuild specified)" -ForegroundColor DarkGray
}

# Step 2: Stop running processes
Write-Host "`n[2/3] Stopping MarkdownViewer processes..." -ForegroundColor Yellow
$processes = @(Get-Process -Name 'MarkdownViewer' -ErrorAction Ignore)
if ($processes.Count -gt 0) {
    $processes | Stop-Process -Force
    Write-Host "      Stopped $($processes.Count) process(es)." -ForegroundColor Green
    Start-Sleep -Milliseconds 500
} else {
    Write-Host '      No running processes found.' -ForegroundColor DarkGray
}

# Step 3: Deploy
Write-Host "`n[3/3] Deploying files..." -ForegroundColor Yellow

# Copy bin files
foreach ($file in $BinFiles) {
    $src = Join-Path $BuildOutput $file
    $dst = Join-Path $BinTarget $file
    if (Test-Path $src) {
        Copy-Item $src $dst -Force
        Write-Host "      bin\$file" -ForegroundColor DarkGray
    } else {
        Write-Warning "File not found: $src"
    }
}

# Copy module files
foreach ($file in $ModuleFiles) {
    $src = Join-Path $ModuleSource $file
    $dst = Join-Path $DeployTarget $file
    if (Test-Path $src) {
        Copy-Item $src $dst -Force
        Write-Host "      $file" -ForegroundColor DarkGray
    } else {
        Write-Warning "File not found: $src"
    }
}

Write-Host "`n=== Deploy completed ===" -ForegroundColor Cyan
Write-Host "Target: $DeployTarget" -ForegroundColor DarkGray
