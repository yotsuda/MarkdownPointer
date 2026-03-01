# MarkdownPointer PowerShell Module

$script:PipeName = "MarkdownPointer_Pipe"
$script:ExePath = Join-Path $PSScriptRoot "bin\mdp.exe"

function Send-MarkdownPointerCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [hashtable]$Message,
        
        [int]$Retries = 3,
        
        [int]$TimeoutMs = 10000
    )
    
    $json = $Message | ConvertTo-Json -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    
    $ErrorActionPreference = 'SilentlyContinue'
    
    for ($i = 0; $i -lt $Retries; $i++) {
        $client = $null
        try {
            $client = [System.IO.Pipes.NamedPipeClientStream]::new(".", $script:PipeName, [System.IO.Pipes.PipeDirection]::InOut)
            $client.Connect($TimeoutMs)
            $client.Write($bytes, 0, $bytes.Length)
            $client.Flush()
            
            # Read response
            $buffer = [byte[]]::new(4096)
            $bytesRead = $client.Read($buffer, 0, $buffer.Length)
            $client.Close()
            
            if ($bytesRead -gt 0) {
                $responseJson = [System.Text.Encoding]::UTF8.GetString($buffer, 0, $bytesRead)
                return $responseJson | ConvertFrom-Json
            }
            return $null
        }
        catch {
            # Silently retry on timeout
            if ($client) {
                try { $client.Close() } catch { }
            }
            if ($i -lt $Retries - 1) {
                Start-Sleep -Milliseconds 500
            }
        }
    }
    # Return null silently instead of throwing error
    return $null
}

function Start-MarkdownPointer {
    [CmdletBinding()]
    param()
    
    if (-not (Test-Path $script:ExePath)) {
        throw "mdp.exe not found at: $script:ExePath"
    }
    
    Start-Process -FilePath $script:ExePath -WindowStyle Normal
    
    # Wait for the pipe to become available
    $timeout = 5
    $elapsed = 0
    while ($elapsed -lt $timeout) {
        Start-Sleep -Milliseconds 200
        $elapsed += 0.2
        $proc = $null
        $proc = Get-Process -Name mdp -ErrorAction Ignore
        if ($proc) {
            Start-Sleep -Milliseconds 500  # Extra wait for pipe initialization
            return
        }
    }
    throw "MarkdownPointer failed to start within $timeout seconds"
}
function _WriteTempFromProvider {
    # Read content from a non-filesystem provider path via Get-Content and write to a temp file.
    param([string]$ProviderPath)
    try {
        $content = (Get-Content -LiteralPath $ProviderPath -ErrorAction Stop) -join "`n"
        $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "MarkdownPointer"
        if (-not (Test-Path $tempDir)) {
            New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
        }
        $fileName = [System.IO.Path]::GetFileName($ProviderPath)
        if (-not $fileName) { $fileName = "provider_content.md" }
        $tempFile = Join-Path $tempDir $fileName
        Set-Content -Path $tempFile -Value $content -Encoding UTF8 -NoNewline
        return $tempFile
    }
    catch {
        Write-Error "Failed to read from provider path: $ProviderPath — $_" -Category ReadError -TargetObject $ProviderPath
        return $null
    }
}

function Show-MarkdownPointer {
    <#
    .SYNOPSIS
    Opens a Markdown file or content in MarkdownPointer.
    
    .DESCRIPTION
    Opens the specified Markdown file or renders Markdown content directly in MarkdownPointer. 
    If MarkdownPointer is not running, it will be started automatically.
    When a string is piped, it's treated as Markdown content if it doesn't exist as a file path.
    
    .PARAMETER Path
    The path to the Markdown file to open, or Markdown content as a string.
    
    .PARAMETER Line
    The line number to scroll to after opening the file.
    
    .PARAMETER Title
    Custom title for the tab when displaying Markdown content directly. Defaults to "Preview".
    
    .EXAMPLE
    Show-Markdown .\README.md
    
    .EXAMPLE
    Show-Markdown .\README.md -Line 50
    
    .EXAMPLE
    Get-ChildItem *.md | Show-Markdown
    
    .EXAMPLE
    "# Hello World`n`nThis is **bold** text." | Show-Markdown
    
    .EXAMPLE
    @"
    # Report
    
    | Item | Value |
    |------|-------|
    | CPU  | 80%   |
    "@ | Show-Markdown -Title "System Report"
    #>
    [CmdletBinding()]
    param(
        [Parameter(ValueFromPipeline, ValueFromPipelineByPropertyName, Position = 0)]
        [Alias("FullName")]
        [string[]]$Path,
        
        [Parameter(Position = 1)]
        [int]$Line,
        
        [Parameter()]
        [string]$Title = "Preview"
    )
    
    begin {
        # Check if MarkdownPointer is running
        $process = Get-Process -Name mdp -ErrorAction Ignore
        if (-not $process) {
            Start-MarkdownPointer
        }

        # Collect paths and content lines
        $filePaths = [System.Collections.Generic.List[string]]::new()
        $contentLines = [System.Collections.Generic.List[string]]::new()
        $isContentMode = $false
    }

    process {
        if (-not $Path) { return }
        foreach ($p in $Path) {
            $resolved = @(Resolve-Path -Path $p -ErrorAction Ignore)

            if ($resolved.Count -gt 0) {
                if ($resolved[0].Provider.Name -eq 'FileSystem') {
                    foreach ($r in $resolved) { $filePaths.Add($r.Path) }
                }
                else {
                    # Non-filesystem provider path: read via Get-Content → temp file
                    foreach ($r in $resolved) {
                        $tempPath = _WriteTempFromProvider $r.Path
                        if ($tempPath) { $filePaths.Add($tempPath) }
                    }
                }
            }
            elseif ($MyInvocation.ExpectingInput) {
                # Pipeline input that's not a valid path - treat as markdown content
                $isContentMode = $true
                $contentLines.Add($p)
            }
            else {
                Write-Error "File not found: $p" -Category ObjectNotFound -TargetObject $p
            }
        }
    }

    end {
        # No files or content - just bring window to front
        if ($filePaths.Count -eq 0 -and -not $isContentMode) {
            Send-MarkdownPointerCommand -Message @{ Command = "activate" } | Out-Null
            return
        }

        # Open collected file paths in a single pipe call
        if ($filePaths.Count -gt 0) {
            $message = @{
                Command = "open"
                Paths = [string[]]$filePaths
            }

            if ($PSBoundParameters.ContainsKey('Line')) {
                $message.Line = $Line
            }

            $result = Send-MarkdownPointerCommand -Message $message

            if ($result) {
                if ($result.Errors) {
                    $result.Errors | ForEach-Object { Write-Warning $_ }
                }
                $filePaths | ForEach-Object { "Opened: $_" }
                foreach ($w in $result.Windows) {
                    foreach ($t in $w.Tabs) {
                        if ($t.Errors) {
                            $t.Errors | ForEach-Object { Write-Warning "$($t.Path): $_" }
                        }
                    }
                }
            }
        }

        # Handle inline markdown content
        if ($isContentMode -and $contentLines.Count -gt 0) {
            $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "MarkdownPointer"
            if (-not (Test-Path $tempDir)) {
                New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
            }

            $safeTitle = $Title -replace '[\\/:*?"<>|]', '_'
            $tempFile = Join-Path $tempDir "$safeTitle.md"

            $contentLines -join "`n" | Set-Content -Path $tempFile -Encoding UTF8

            $message = @{
                Command = "openTemp"
                Path = $tempFile
                Title = $Title
            }

            if ($PSBoundParameters.ContainsKey('Line')) {
                $message.Line = $Line
            }

            $result = Send-MarkdownPointerCommand -Message $message

            if ($result) {
                if ($result.Errors) {
                    $result.Errors | ForEach-Object { Write-Warning $_ }
                }
                "Opened preview: $Title"
            }
        }
    }
}
function Get-MarkdownPointerMCPPath {
    <#
    .SYNOPSIS
    Returns the path to the MarkdownPointer MCP server executable.

    .DESCRIPTION
    Returns the full path to mdp-mcp.exe bundled with this module.
    Use this to register MarkdownPointer as an MCP server in Claude Code.

    .PARAMETER Escape
    Escape backslashes in the path (e.g. for JSON config files).

    .EXAMPLE
    claude mcp add MarkdownPointer -s user -- "$(Get-MarkdownPointerMCPPath)"

    .EXAMPLE
    Get-MarkdownPointerMCPPath -Escape
    # Returns: C:\\program files\\powershell\\7\\Modules\\MarkdownPointer\\bin\\mdp-mcp.exe
    #>
    [CmdletBinding()]
    param(
        [switch]$Escape
    )

    $mcpPath = Join-Path (Get-Module MarkdownPointer).ModuleBase "bin\mdp-mcp.exe"
    if (-not (Test-Path $mcpPath)) {
        throw "mdp-mcp.exe not found at: $mcpPath"
    }
    if ($Escape) {
        return $mcpPath -replace '\\', '\\'
    }
    return $mcpPath
}

function ConvertTo-Docx {
    <#
    .SYNOPSIS
    Convert files to .docx using Pandoc.

    .DESCRIPTION
    Converts one or more files to Word documents (.docx) using Pandoc.
    Input format is auto-detected by Pandoc from the file extension.
    Supports wildcards. Output files are placed alongside the source files by default.

    .PARAMETER Path
    Path(s) to Markdown files. Supports wildcards.

    .PARAMETER OutputDirectory
    Optional output directory. Defaults to each source file's directory.

    .EXAMPLE
    ConvertTo-Docx .\README.md

    .EXAMPLE
    ConvertTo-Docx .\docs\*.md

    .EXAMPLE
    ConvertTo-Docx .\docs\*.md -OutputDirectory .\out
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0, ValueFromPipeline)]
        [string[]]$Path,

        [string]$OutputDirectory
    )

    begin {
        $pandoc = Get-Command pandoc -ErrorAction SilentlyContinue
        if (-not $pandoc) {
            throw "Pandoc is not installed. Install from https://pandoc.org/installing.html"
        }
        if ($OutputDirectory) {
            $OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
            if (-not (Test-Path $OutputDirectory)) {
                New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
            }
        }
    }

    process {
        foreach ($p in $Path) {
            $resolved = @(Resolve-Path -Path $p -ErrorAction SilentlyContinue)
            if ($resolved.Count -eq 0) {
                Write-Warning "No files found: $p"
                continue
            }
            foreach ($file in $resolved) {
                $filePath = $file.Path
                if ($OutputDirectory) {
                    $outPath = Join-Path $OutputDirectory ([System.IO.Path]::ChangeExtension([System.IO.Path]::GetFileName($filePath), '.docx'))
                } else {
                    $outPath = [System.IO.Path]::ChangeExtension($filePath, '.docx')
                }
                $result = & pandoc -t docx -o $outPath $filePath 2>&1
                if ($LASTEXITCODE -eq 0) {
                    [PSCustomObject]@{
                        Source = $filePath
                        Output = $outPath
                    }
                } else {
                    Write-Error "Failed to convert ${filePath}: $result"
                }
            }
        }
    }
}

New-Alias -Name mdp -Value Show-MarkdownPointer

Export-ModuleMember -Function Show-MarkdownPointer, Get-MarkdownPointerMCPPath, ConvertTo-Docx -Alias mdp