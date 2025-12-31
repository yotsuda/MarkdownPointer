# MarkdownViewer PowerShell Module

$script:PipeName = "MarkdownViewer_Pipe"
$script:ExePath = Join-Path $PSScriptRoot "bin\MarkdownViewer.exe"

function Send-MarkdownViewerCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [hashtable]$Message
    )
    
    $json = $Message | ConvertTo-Json -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    
    try {
        $client = [System.IO.Pipes.NamedPipeClientStream]::new(".", $script:PipeName, [System.IO.Pipes.PipeDirection]::InOut)
        $client.Connect(1000)
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
    }
    catch {
        return $null
    }
}

function Start-MarkdownViewer {
    [CmdletBinding()]
    param()
    
    if (-not (Test-Path $script:ExePath)) {
        throw "MarkdownViewer.exe not found at: $script:ExePath"
    }
    
    Start-Process -FilePath $script:ExePath -WindowStyle Normal
    Start-Sleep -Milliseconds 500
}

function Show-Markdown {
    <#
    .SYNOPSIS
    Opens a Markdown file in MarkdownViewer.
    
    .DESCRIPTION
    Opens the specified Markdown file in MarkdownViewer. If MarkdownViewer is not running, it will be started automatically.
    
    .PARAMETER Path
    The path to the Markdown file to open.
    
    .EXAMPLE
    Show-Markdown .\README.md
    
    .EXAMPLE
    Get-ChildItem *.md | Show-Markdown
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, ValueFromPipeline, ValueFromPipelineByPropertyName, Position = 0)]
        [Alias("FullName")]
        [string[]]$Path
    )
    
    begin {
        # Check if MarkdownViewer is running
        $process = Get-Process -Name MarkdownViewer -ErrorAction SilentlyContinue
        if (-not $process) {
            Start-MarkdownViewer
        }
    }
    
    process {
        foreach ($p in $Path) {
            $fullPath = Resolve-Path -Path $p -ErrorAction SilentlyContinue
            if (-not $fullPath) {
                Write-Error "File not found: $p"
                continue
            }
            
            $result = Send-MarkdownViewerCommand -Message @{
                Command = "open"
                Path = $fullPath.Path
            }
            
            if ($result) {
                Write-Verbose "Opened: $($fullPath.Path)"
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

Export-ModuleMember -Function Show-Markdown, Get-MarkdownTab, Start-MarkdownViewer