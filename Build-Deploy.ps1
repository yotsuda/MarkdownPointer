<#
.SYNOPSIS
    Build and package MarkdownPointer for release.
.DESCRIPTION
    Builds the MarkdownPointer application and MCP server, then creates
    release artifacts for GitHub Releases and NuGet.
    
    Output structure:
    dist/
    ├── MarkdownPointer-win-x64.zip       # WPF app + MCP server bundle
    ├── MarkdownPointer.Mcp-win-x64.zip   # MCP server standalone
    ├── MarkdownPointer.Mcp-linux-x64.zip # MCP server for Linux
    ├── MarkdownPointer.Mcp-osx-x64.zip   # MCP server for macOS Intel
    ├── MarkdownPointer.Mcp-osx-arm64.zip # MCP server for macOS Apple Silicon
    └── MarkdownPointer.Mcp.x.x.x.nupkg   # NuGet package (dotnet tool)

.PARAMETER SkipBuild
    Skip the build step and only package existing outputs.
.PARAMETER Platform
    Target platforms to build. Default: win-x64, linux-x64, osx-x64, osx-arm64
.PARAMETER NuGetOnly
    Only create NuGet package, skip platform-specific builds.
.EXAMPLE
    .\Build-Deploy.ps1
    # Full build and package for all platforms
.EXAMPLE
    .\Build-Deploy.ps1 -Platform win-x64
    # Build only for Windows x64
.EXAMPLE
    .\Build-Deploy.ps1 -NuGetOnly
    # Create NuGet package only
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [string[]]$Platform = @('win-x64'),
    [switch]$NuGetOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ProjectRoot = $PSScriptRoot
$AppProject = Join-Path $ProjectRoot 'MarkdownPointer\MarkdownPointer.App.csproj'
$McpProject = Join-Path $ProjectRoot 'MarkdownPointer.Mcp\MarkdownPointer.Mcp.csproj'
$DistDir = Join-Path $ProjectRoot 'dist'

# Get version from App project
$csprojContent = Get-Content $AppProject -Raw
if ($csprojContent -match '<Version>([^<]+)</Version>') {
    $Version = $Matches[1]
} else {
    $Version = '0.1.0'
}

Write-Host '=== MarkdownPointer Build & Package ===' -ForegroundColor Cyan
Write-Host "Version: $Version" -ForegroundColor DarkGray

# Step 1: Stop running processes
Write-Host "`n[1/4] Stopping running processes..." -ForegroundColor Yellow
$processes = @(Get-Process -Name 'MarkdownPointer', 'MarkdownPointer.Mcp' -ErrorAction Ignore)
if ($processes.Count -gt 0) {
    $processes | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    Write-Host "      Stopped $($processes.Count) process(es)." -ForegroundColor Green
} else {
    Write-Host '      No running processes found.' -ForegroundColor DarkGray
}

# Step 2: Clean dist directory
Write-Host "`n[2/4] Preparing dist directory..." -ForegroundColor Yellow
if (Test-Path $DistDir) {
    Remove-Item $DistDir -Recurse -Force
}
New-Item $DistDir -ItemType Directory -Force | Out-Null
Write-Host "      Created: $DistDir" -ForegroundColor DarkGray

if (-not $SkipBuild) {
    # Step 3: Build
    Write-Host "`n[3/4] Building projects..." -ForegroundColor Yellow
    
    if (-not $NuGetOnly) {
        # Build WPF App (Windows only)
        if ($Platform -contains 'win-x64') {
            Write-Host "      Building MarkdownPointer.App (win-x64)..." -ForegroundColor DarkGray
            dotnet publish $AppProject -c Release -r win-x64 --no-self-contained -o "$DistDir\app-win-x64"
            if ($LASTEXITCODE -ne 0) { throw "App build failed" }
        }
        
        # Build MCP Server for each platform
        foreach ($rid in $Platform) {
            Write-Host "      Building MarkdownPointer.Mcp ($rid)..." -ForegroundColor DarkGray
            dotnet publish $McpProject -c Release -r $rid --no-self-contained -o "$DistDir\mcp-$rid"
            if ($LASTEXITCODE -ne 0) { throw "MCP build failed for $rid" }
        }
    }

    Write-Host '      Build succeeded.' -ForegroundColor Green
} else {
    Write-Host "`n[3/4] Skipping build (SkipBuild specified)" -ForegroundColor DarkGray
}

# Step 4: Create release archives
Write-Host "`n[4/4] Creating release archives..." -ForegroundColor Yellow

if (-not $NuGetOnly) {
    # Windows bundle (App + MCP)
    if ($Platform -contains 'win-x64' -and (Test-Path "$DistDir\app-win-x64")) {
        $folderName = "MarkdownPointer-$Version"
        $bundleDir = "$DistDir\$folderName"
        New-Item $bundleDir -ItemType Directory -Force | Out-Null
        
        # Copy App files
        Copy-Item "$DistDir\app-win-x64\*" $bundleDir -Recurse
        
        # Copy MCP server
        $mcpExe = Get-ChildItem "$DistDir\mcp-win-x64" -Filter '*.exe' | Select-Object -First 1
        if ($mcpExe) {
            Copy-Item $mcpExe.FullName "$bundleDir\MarkdownPointer.Mcp.exe"
        }
        
        # Copy README and LICENSE
        Copy-Item "$ProjectRoot\README.md" $bundleDir -ErrorAction SilentlyContinue
        Copy-Item "$ProjectRoot\LICENSE" $bundleDir -ErrorAction SilentlyContinue
        
        # Create zip (include folder)
        $zipName = "MarkdownPointer-$Version-win-x64.zip"
        $zipPath = "$DistDir\$zipName"
        Compress-Archive -Path $bundleDir -DestinationPath $zipPath -Force
        Write-Host "      Created: $zipName" -ForegroundColor DarkGray
        
        Remove-Item $bundleDir -Recurse -Force
    }
    
    # Cleanup MCP build directory
    foreach ($rid in $Platform) {
        $mcpDir = "$DistDir\mcp-$rid"
        if (Test-Path $mcpDir) {
            Remove-Item $mcpDir -Recurse -Force
        }
    }
    
    # Cleanup app build directory
    if (Test-Path "$DistDir\app-win-x64") {
        Remove-Item "$DistDir\app-win-x64" -Recurse -Force
    }
}

# Show results
Write-Host "`n=== Package completed ===" -ForegroundColor Cyan
Write-Host "Output directory: $DistDir" -ForegroundColor DarkGray
Get-ChildItem $DistDir | ForEach-Object {
    $size = if ($_.Length -gt 1MB) { "{0:N1} MB" -f ($_.Length / 1MB) } else { "{0:N0} KB" -f ($_.Length / 1KB) }
    Write-Host "  $($_.Name) ($size)" -ForegroundColor Gray
}

# Show installation instructions
Write-Host "`n=== Installation Instructions ===" -ForegroundColor Cyan
Write-Host @"

## GitHub Releases
Upload the zip file to GitHub Releases.

## Claude Desktop Configuration
Add to claude_desktop_config.json:

{
  "mcpServers": {
    "MarkdownPointer": {
      "command": "C:\\path\\to\\MarkdownPointer.Mcp.exe"
    }
  }
}
"@ -ForegroundColor DarkGray
