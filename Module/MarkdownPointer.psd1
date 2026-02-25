@{
    RootModule = 'MarkdownPointer.psm1'
    ModuleVersion = '0.2.0'
    GUID = '4c50c9c4-d155-457d-a3a3-e3952253b51d'
    Author = 'Yoshifumi Tsuda'
    Copyright = '(c) 2025-2026 Yoshifumi Tsuda. All rights reserved.'
    Description = @'
Vibe editing for Markdown. Point at anything, tell AI to fix it.

Click any element in the rendered preview to copy a location reference:
  "Rewrite this: [C:\docs\notes.md:42] ## My Section"

Renders Markdown with Mermaid diagrams, KaTeX math, and SVG. Includes MCP server for Claude Code / Claude Desktop.

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
            ReleaseNotes = @'
Initial release on PowerShell Gallery.

- ConvertTo-Docx: export Markdown to .docx via Pandoc
- SVG file support
- Open multiple files in a single command
- Recent files history
- File path and hover line number display in status bar
- Faster startup via named pipe readiness probing
- Auto-switch to background tab when its file is updated
- Show render errors in console via Show-MarkdownPointer
- Warn when MCP config references outdated module version
- Require PowerShell 7.4+ to avoid conflicts with PowerShell.MCP
- Fix Ctrl+W not working on consecutive presses
- Fix WebView2 user data folder routing
- Fix Mermaid bidirectional arrow double-encoding
'@
        }
    }
}
