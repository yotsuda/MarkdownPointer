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
                foreach ($r in $resolved) { $filePaths.Add($r.Path) }
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
function Get-MarkdownPointerTab {
    <#
    .SYNOPSIS
    Gets the list of open tabs in MarkdownPointer.
    
    .DESCRIPTION
    Returns information about all open tabs including file path, title, and index.
    
    .EXAMPLE
    Get-MarkdownTab
    #>
    [CmdletBinding()]
    param()
    
    $result = Send-MarkdownPointerCommand -Message @{
        Command = "getTabs"
    }
    
    if ($result -and $result.Tabs) {
        $result.Tabs
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

New-Alias -Name mdp -Value Show-MarkdownPointer

Export-ModuleMember -Function Show-MarkdownPointer, Get-MarkdownPointerTab, Get-MarkdownPointerMCPPath -Alias mdp