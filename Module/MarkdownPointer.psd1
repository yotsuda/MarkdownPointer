@{
    RootModule = 'MarkdownPointer.psm1'
    ModuleVersion = '0.2.0'
    GUID = '4c50c9c4-d155-457d-a3a3-e3952253b51d'
    Author = 'Yoshifumi Tsuda'
    Copyright = '(c) 2025-2026 Yoshifumi Tsuda. All rights reserved.'
    Description = @'
Vibe editing for Markdown. Point at anything, tell AI to fix it.

Renders Markdown with Mermaid diagrams, KaTeX math, and SVG. Click any element to copy a filepath:line reference for AI prompts. Includes MCP server for Claude Code / Claude Desktop.

Requirements:
  .NET 8 Desktop Runtime - https://dotnet.microsoft.com/download/dotnet/8.0
  Pandoc (optional, for .docx export) - https://pandoc.org

Quick start:
  mdp .\README.md           # Open a file
  mdp .\docs\*.md           # Open multiple files
  ConvertTo-Docx .\*.md     # Convert to .docx via Pandoc

MCP setup for Claude Code:
  claude mcp add MarkdownPointer -s user -- "$(Get-MarkdownPointerMCPPath)"

MCP setup for Claude Desktop (add to claude_desktop_config.json):
  { "mcpServers": { "MarkdownPointer": { "command": "C:\\...\\mdp-mcp.exe" } } }
  Use Get-MarkdownPointerMCPPath -Escape to get the path with escaped backslashes.

Example prompts for AI:
  "open README.md in mdp"
  "show the report in mdp and scroll to line 50"
  "export report.md to docx"
'@
    PowerShellVersion = '7.4'
    FunctionsToExport = @('Show-MarkdownPointer', 'Get-MarkdownPointerMCPPath', 'ConvertTo-Docx')
    CmdletsToExport = @()
    VariablesToExport = @()
    AliasesToExport = @('mdp')
    PrivateData = @{
        PSData = @{
            Tags = @('Markdown', 'Viewer', 'Preview', 'MCP', 'Claude', 'AI', 'WPF', 'Mermaid', 'KaTeX', 'Pandoc')
            LicenseUri = 'https://github.com/yotsuda/MarkdownPointer/blob/master/LICENSE'
            ProjectUri = 'https://github.com/yotsuda/MarkdownPointer'
        }
    }
}
