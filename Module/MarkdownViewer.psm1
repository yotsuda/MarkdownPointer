# MarkdownViewer PowerShell Module

$script:PipeName = "MarkdownViewer_Pipe"
$script:ExePath = Join-Path $PSScriptRoot "bin\MarkdownViewer.exe"

function Send-MarkdownViewerCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [hashtable]$Message,
        
        [int]$Retries = 3
    )
    
    $json = $Message | ConvertTo-Json -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    
    for ($i = 0; $i -lt $Retries; $i++) {
        try {
            $client = [System.IO.Pipes.NamedPipeClientStream]::new(".", $script:PipeName, [System.IO.Pipes.PipeDirection]::InOut)
            $client.Connect(2000)
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
            if ($i -lt $Retries - 1) {
                Start-Sleep -Milliseconds 500
            }
        }
    }
    return $null
}

function Start-MarkdownViewer {
    [CmdletBinding()]
    param()
    
    if (-not (Test-Path $script:ExePath)) {
        throw "MarkdownViewer.exe not found at: $script:ExePath"
    }
    
    Start-Process -FilePath $script:ExePath -WindowStyle Normal
    
    # Wait for the pipe to become available
    $timeout = 5
    $elapsed = 0
    while ($elapsed -lt $timeout) {
        Start-Sleep -Milliseconds 200
        $elapsed += 0.2
        $proc = $null
        $proc = Get-Process -Name MarkdownViewer -ErrorAction Ignore
        if ($proc) {
            Start-Sleep -Milliseconds 500  # Extra wait for pipe initialization
            return
        }
    }
    throw "MarkdownViewer failed to start within $timeout seconds"
}
function Show-Markdown {
    <#
    .SYNOPSIS
    Opens a Markdown file or content in MarkdownViewer.
    
    .DESCRIPTION
    Opens the specified Markdown file or renders Markdown content directly in MarkdownViewer. 
    If MarkdownViewer is not running, it will be started automatically.
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
        [Parameter(Mandatory, ValueFromPipeline, ValueFromPipelineByPropertyName, Position = 0)]
        [Alias("FullName")]
        [string[]]$Path,
        
        [Parameter(Position = 1)]
        [int]$Line,
        
        [Parameter()]
        [string]$Title = "Preview"
    )
    
    begin {
        # Check if MarkdownViewer is running
        $process = Get-Process -Name MarkdownViewer -ErrorAction Ignore
        if (-not $process) {
            Start-MarkdownViewer
        }
        
        # Collect content for inline markdown
        $contentLines = [System.Collections.Generic.List[string]]::new()
        $isContentMode = $false
    }
    
    process {
        foreach ($p in $Path) {
            # Check if this is a file path or markdown content
            $resolvedPath = Resolve-Path -Path $p -ErrorAction SilentlyContinue
            
            if ($resolvedPath) {
                # It's a file path
                $message = @{
                    Command = "open"
                    Path = $resolvedPath.Path
                }
                
                if ($PSBoundParameters.ContainsKey('Line')) {
                    $message.Line = $Line
                }
                
                $result = Send-MarkdownViewerCommand -Message $message
                
                if ($result) {
                    Write-Verbose "Opened: $($resolvedPath.Path)"
                }
            }
            else {
                # Not a valid file path - treat as markdown content
                $isContentMode = $true
                $contentLines.Add($p)
            }
        }
    }
    
    end {
        if ($isContentMode -and $contentLines.Count -gt 0) {
            # Create temp file with markdown content
            $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "MarkdownViewer"
            if (-not (Test-Path $tempDir)) {
                New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
            }
            
            $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
            $safeTitle = $Title -replace '[\\/:*?"<>|]', '_'
            $tempFile = Join-Path $tempDir "$safeTitle`_$timestamp.md"
            
            $contentLines -join "`n" | Set-Content -Path $tempFile -Encoding UTF8
            
            $message = @{
                Command = "openTemp"
                Path = $tempFile
                Title = $Title
            }
            
            if ($PSBoundParameters.ContainsKey('Line')) {
                $message.Line = $Line
            }
            
            $result = Send-MarkdownViewerCommand -Message $message
            
            if ($result) {
                Write-Verbose "Opened preview: $Title"
            }
        }
    }
}
function Get-MarkdownTab {
    <#
    .SYNOPSIS
    Gets the list of open tabs in MarkdownViewer.
    
    .DESCRIPTION
    Returns information about all open tabs including file path, title, and index.
    
    .EXAMPLE
    Get-MarkdownTab
    #>
    [CmdletBinding()]
    param()
    
    $result = Send-MarkdownViewerCommand -Message @{
        Command = "getTabs"
    }
    
    if ($result -and $result.Tabs) {
        $result.Tabs
    }
}

Export-ModuleMember -Function Show-Markdown, Get-MarkdownTab