@{
    RootModule = 'MarkdownPointer.psm1'
    ModuleVersion = '0.9.0'
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
  claude mcp add mdp -s user -- "$(Get-MarkdownPointerMCPPath)"

MCP setup for Claude Desktop (add to claude_desktop_config.json):
  { "mcpServers": { "mdp": { "command": "C:\\...\\mdp-mcp.exe" } } }
  Use Get-MarkdownPointerMCPPath -Escape to get the path with escaped backslashes.

Example prompts for AI:
  "open README.md in mdp"
  "show the report in mdp and scroll to line 50"
  "export report.md to docx"
  "Fix the grammar here [ref]"
  "Translate this section to Japanese [ref]"
  "Add a code example after this section [ref]"
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
0.9.0
- Redesign welcome screen: action-first layout with recent folders/files front and center
- VS Code button opens at the current viewport line (code -g)
- Show full path in status bar when hovering recent items and hyperlinks
- Fix arrow key slide navigation not working after button click
- Disable slide view button when no file is open
- Increase max recent folders from 5 to 10
- Pre-compile SlideService regexes, fix null baseDir, block path traversal

0.8.0
- Slide view: toggle with 🎞 button to preview Markdown as reveal.js slides (Pandoc)
- Slide themes: right-click 🎞 to choose from 12 reveal.js themes
- Unified Export button: .docx and .pptx via Pandoc, with optional template (--reference-doc)
- Spinner animation in status bar during slide rendering and export
- Remove pan/scroll mode (✋) — mouse wheel suffices
- Use GitHub-compatible AutoIdentifiers for heading anchors

0.7.0
- Ctrl+W and Ctrl+Tab shortcuts shown on welcome screen (two-column layout)
- Fix Ctrl+Tab not working when WebView2 has focus
- Fix file unpin restoring to wrong position (now matches folder behavior)
- Fix hidden folders consuming display slots in recent folders list
- Show status message when revealing file in Explorer
- Use consistent status message (not MessageBox) for missing link targets
- Larger close button on tabs matching recent list style
- Hide focus visual on tab items

0.6.0
- Ctrl+G to jump to a specific line number (Go to Line dialog)
- Status bar file path click reveals file in Explorer instead of opening file dialog
- Show error in status bar when Mermaid/KaTeX libraries fail to load offline
- Keyboard shortcut list on welcome screen (Ctrl+O, Ctrl+F, Ctrl+G, Ctrl+P)
- Fix version mismatch check to report the latest installed version

0.5.0
- Pin support for recent files and folders
- Ctrl+P to print via system print dialog
- Unified context menu style across pointing and pan modes
- Save Mermaid diagrams as PNG (Save as Image... context menu)
- Per-diagram Ctrl+wheel zoom for Mermaid diagrams with scroll container
- CSS zoom for page-level zoom (replaced WebView2 ZoomFactor)

0.4.0
- Support non-filesystem PowerShell provider paths via Get-Content fallback

0.3.0
- Add app icon (Emerald green face motif with transparent cutouts)
- Fix local images not displaying in rendered preview (base64 data URI inlining)
- Faster startup: show window immediately with splash placeholder
- Show error in status bar when opening a missing recent file or folder

0.2.1
- Fix MCP server name in setup instructions (MarkdownPointer -> mdp)

0.2.0
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
