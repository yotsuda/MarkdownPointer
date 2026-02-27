# MarkdownPointer

**Vibe editing for Markdown.** Point at anything, tell AI to fix it.

MarkdownPointer renders your Markdown and lets you click any element - headings, code blocks, table cells, Mermaid diagram nodes, KaTeX math - to copy a `filepath:line` reference. Paste it into your AI prompt, and the AI knows exactly where to look.

```
Fix the diagram at [C:\docs\report.md:42] mermaid diagram: graph LR
```

## Features

- **Point & Prompt** - Click any rendered element to copy `filepath:line` to clipboard
- **Mermaid Diagrams** - Flowchart, Sequence, Class, State, ER, Gantt, Pie, Git graph, Mindmap
- **KaTeX Math** - Inline `$...$` and block `$$...$$`
- **SVG** - Embedded font support
- **Live Reload** - Auto-refresh on file changes
- **Export** - `.docx` via Pandoc
- **MCP Server** - Let Claude open, navigate, and export your documents

## Install

```powershell
Install-PSResource MarkdownPointer
```

## Quick Start

```powershell
mdp .\README.md            # Open a file
mdp .\docs\*.md            # Open multiple files
Show-MarkdownPointer       # Just launch the viewer
```

## MCP Server Setup

Connect MarkdownPointer to Claude Code so your AI can open and navigate documents directly.

### Claude Code

```powershell
claude mcp add mdp -s user -- "$(Get-MarkdownPointerMCPPath)"
```

Then just ask Claude:

- "open README.md in mdp"
- "show the report in mdp and scroll to line 50"
- "export report.md to docx"

### Claude Desktop

Add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "mdp": {
      "command": "C:\\Program Files\\PowerShell\\7\\Modules\\MarkdownPointer\\0.2.0\\bin\\mdp-mcp.exe"
    }
  }
}
```

> Use `Get-MarkdownPointerMCPPath -Escape` to get the correct path for your environment.

### MCP Tools

| Tool | Description |
|------|-------------|
| `show_markdown` | Open files and scroll to a line |
| `export_docx` | Convert to .docx via Pandoc |

## Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| `Ctrl+O` | Open file |
| `Ctrl+W` / `Ctrl+F4` | Close tab |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Switch tabs |
| `Ctrl+Mouse Wheel` | Zoom |
| `F5` | Reload |

## Requirements

- Windows 10/11
- PowerShell 7.4+
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

<details>
<summary>Build from Source</summary>

```powershell
git clone https://github.com/yotsuda/MarkdownPointer.git
cd MarkdownPointer
.\Build-Deploy.ps1
```

</details>

## License

MIT
