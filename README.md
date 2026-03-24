# MarkdownPointer

**Vibe writing for Markdown.** Point at anything, tell AI to fix it.

MarkdownPointer renders your Markdown and lets you click any element - headings, code blocks, table cells, Mermaid diagram nodes, KaTeX math - to copy a `filepath:line` reference. Paste it into your AI prompt, and the AI knows exactly where to look.

**To change a node's color:**

<p align="center">
  <img width="40%" valign="middle" alt="Image" src="https://github.com/user-attachments/assets/af06577f-4f7c-40cd-b76e-e3426fd36699" />
  &nbsp;&nbsp;&nbsp;
  <img width="8%" valign="middle" alt="Image" src="https://github.com/user-attachments/assets/16b7d1ab-3799-421a-8d21-9ecdbc1ae66c" />
  &nbsp;&nbsp;&nbsp;
  <img width="40%" valign="middle" alt="Image" src="https://github.com/user-attachments/assets/daea5945-af62-47f7-8f02-a0206611d8e7" />
</p>

**Click the node, paste the reference into your prompt, and ask the AI — done.**

```
Color this node orange [c:\docs\architecture.md:6] mermaid node: mdp.exe
```

**More prompt examples:**
- Verify this section for technical accuracy [ref]
- Swap these two sections [ref] [ref]
- Delete this [ref]
- Simplify this paragraph [ref]
- Add a code example after this section [ref]
- Fix the grammar here [ref]
- Translate this section to Japanese [ref]

## Features

| Feature | Description |
|---------|-------------|
| Point & Prompt | Click any rendered element to copy `filepath:line` to clipboard |
| Mermaid Diagrams | Flowchart, Sequence, Class, State, ER, Gantt, Pie, Git graph, Mindmap |
| KaTeX Math | Inline `$...$` and block `$$...$$` |
| SVG | Embedded font support |
| Recent Files | Quick access with pin support |
| Tab Dock/Undock | Drag tabs between windows or detach to a new window |
| Always on Top | Pin the window above other apps for reference |
| Live Reload | Auto-refresh on file changes |
| Export | `.docx` / `.pptx` via Pandoc (Mermaid/SVG rendered as images) |
| MCP Server | Let Claude open, navigate, and export your documents |

## Install
In a [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/install-powershell-on-windows) console:
```powershell
Install-Module MarkdownPointer
```

## Quick Start

```powershell
mdp .\README.md    # Open a file
mdp .\docs\*.md    # Open multiple files
mdp                # Just launch the viewer
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

Run this in PowerShell to get the path for your environment:

```powershell
Get-MarkdownPointerMCPPath -Escape
```

Then add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "mdp": {
      "command": "<paste the output here>"
    }
  }
}
```

### MCP Tools

| Tool | Description |
|------|-------------|
| `show_markdown` | Open files and scroll to a line |
| `get_status` | Get current window/tab state |
| `slide_control` | Navigate reveal.js slides |
| `export_document` | Export to .docx / .pptx via Pandoc |

## Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| `Ctrl+O` | Open file |
| `Ctrl+F` | Find in page |
| `Ctrl+G` | Go to line |
| `Ctrl+P` | Print |
| `Ctrl+W` / `Ctrl+F4` | Close tab |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Switch tabs |
| `Mouse Wheel` | Scroll |
| `Ctrl+Mouse Wheel` | Zoom |
| `F5` | Reload |

## Requirements

- Windows 10/11
- [PowerShell 7.4+](https://learn.microsoft.com/powershell/scripting/install/install-powershell-on-windows)
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
