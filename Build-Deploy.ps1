<#
.SYNOPSIS
    Build and package MarkdownViewer for release.
.DESCRIPTION
    Builds the MarkdownViewer application and MCP server, then creates
    release artifacts for GitHub Releases and NuGet.
    
    Output structure:
    dist/
    ├── MarkdownViewer-win-x64.zip       # WPF app + MCP server bundle
    ├── MarkdownViewer.Mcp-win-x64.zip   # MCP server standalone
    ├── MarkdownViewer.Mcp-linux-x64.zip # MCP server for Linux
    ├── MarkdownViewer.Mcp-osx-x64.zip   # MCP server for macOS Intel
    ├── MarkdownViewer.Mcp-osx-arm64.zip # MCP server for macOS Apple Silicon
    └── MarkdownViewer.Mcp.x.x.x.nupkg   # NuGet package (dotnet tool)

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
    [string[]]$Platform = @('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64'),
    [switch]$NuGetOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ProjectRoot = $PSScriptRoot
$AppProject = Join-Path $ProjectRoot 'MarkdownViewer\MarkdownViewer.App.csproj'
$McpProject = Join-Path $ProjectRoot 'MarkdownViewer.Mcp\MarkdownViewer.Mcp.csproj'
$DistDir = Join-Path $ProjectRoot 'dist'

# Get version from MCP project
$csprojContent = Get-Content $McpProject -Raw
if ($csprojContent -match '<Version>([^<]+)</Version>') {
    $Version = $Matches[1]
} else {
    $Version = '0.1.0'
}

Write-Host '=== MarkdownViewer Build & Package ===' -ForegroundColor Cyan
Write-Host "Version: $Version" -ForegroundColor DarkGray

# Step 1: Stop running processes
Write-Host "`n[1/4] Stopping running processes..." -ForegroundColor Yellow
$processes = @(Get-Process -Name 'MarkdownViewer', 'MarkdownViewer.Mcp' -ErrorAction Ignore)
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
            Write-Host "      Building MarkdownViewer.App (win-x64)..." -ForegroundColor DarkGray
            dotnet publish $AppProject -c Release -r win-x64 --self-contained -o "$DistDir\app-win-x64"
            if ($LASTEXITCODE -ne 0) { throw "App build failed" }
        }
        
        # Build MCP Server for each platform
        foreach ($rid in $Platform) {
            Write-Host "      Building MarkdownViewer.Mcp ($rid)..." -ForegroundColor DarkGray
            dotnet publish $McpProject -c Release -r $rid --self-contained -o "$DistDir\mcp-$rid"
            if ($LASTEXITCODE -ne 0) { throw "MCP build failed for $rid" }
        }
    }
    
    # Build NuGet package
    Write-Host "      Creating NuGet package..." -ForegroundColor DarkGray
    dotnet pack $McpProject -c Release -o $DistDir
    if ($LASTEXITCODE -ne 0) { throw "NuGet pack failed" }
    
    Write-Host '      Build succeeded.' -ForegroundColor Green
} else {
    Write-Host "`n[3/4] Skipping build (SkipBuild specified)" -ForegroundColor DarkGray
}

# Step 4: Create release archives
Write-Host "`n[4/4] Creating release archives..." -ForegroundColor Yellow

if (-not $NuGetOnly) {
    # Windows bundle (App + MCP)
    if ($Platform -contains 'win-x64' -and (Test-Path "$DistDir\app-win-x64")) {
        $bundleDir = "$DistDir\bundle-win-x64"
        New-Item $bundleDir -ItemType Directory -Force | Out-Null
        
        # Copy App files
        Copy-Item "$DistDir\app-win-x64\*" $bundleDir -Recurse
        
        # Copy MCP server
        $mcpExe = Get-ChildItem "$DistDir\mcp-win-x64" -Filter '*.exe' | Select-Object -First 1
        if ($mcpExe) {
            Copy-Item $mcpExe.FullName "$bundleDir\MarkdownViewer.Mcp.exe"
        }
        
        # Copy README and LICENSE
        Copy-Item "$ProjectRoot\README.md" $bundleDir -ErrorAction SilentlyContinue
        Copy-Item "$ProjectRoot\LICENSE" $bundleDir -ErrorAction SilentlyContinue
        
        # Create zip
        $zipPath = "$DistDir\MarkdownViewer-win-x64.zip"
        Compress-Archive -Path "$bundleDir\*" -DestinationPath $zipPath -Force
        Write-Host "      Created: MarkdownViewer-win-x64.zip" -ForegroundColor DarkGray
        
        Remove-Item $bundleDir -Recurse -Force
    }
    
    # MCP server standalone archives
    foreach ($rid in $Platform) {
        $mcpDir = "$DistDir\mcp-$rid"
        if (Test-Path $mcpDir) {
            # Find the executable
            $exePattern = if ($rid -like 'win-*') { '*.exe' } else { 'MarkdownViewer.Mcp' }
            $mcpExe = Get-ChildItem $mcpDir -Filter $exePattern | Where-Object { $_.Name -notlike '*.dll' } | Select-Object -First 1
            
            # Create staging directory
            $stageDir = "$DistDir\stage-mcp-$rid"
            New-Item $stageDir -ItemType Directory -Force | Out-Null
            
            if ($mcpExe) {
                $targetName = if ($rid -like 'win-*') { 'markdownviewer-mcp.exe' } else { 'markdownviewer-mcp' }
                Copy-Item $mcpExe.FullName "$stageDir\$targetName"
            }
            
            # Copy README and LICENSE
            Copy-Item "$ProjectRoot\README.md" $stageDir -ErrorAction SilentlyContinue
            Copy-Item "$ProjectRoot\LICENSE" $stageDir -ErrorAction SilentlyContinue
            
            # Create zip
            $zipPath = "$DistDir\MarkdownViewer.Mcp-$rid.zip"
            Compress-Archive -Path "$stageDir\*" -DestinationPath $zipPath -Force
            Write-Host "      Created: MarkdownViewer.Mcp-$rid.zip" -ForegroundColor DarkGray
            
            # Cleanup
            Remove-Item $stageDir -Recurse -Force
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
Upload the zip files to GitHub Releases.

## NuGet (dotnet tool)
Publish to NuGet.org:
  dotnet nuget push dist\MarkdownViewer.Mcp.$Version.nupkg -k <API_KEY> -s https://api.nuget.org/v3/index.json

Install as dotnet tool:
  dotnet tool install --global MarkdownViewer.Mcp

## Claude Desktop Configuration
Add to claude_desktop_config.json:

{
  "mcpServers": {
    "markdownviewer": {
      "command": "markdownviewer-mcp"
    }
  }
}

Or with explicit path (Windows):
{
  "mcpServers": {
    "markdownviewer": {
      "command": "C:\\path\\to\\markdownviewer-mcp.exe"
    }
  }
}
"@ -ForegroundColor DarkGray
