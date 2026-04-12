# MarkdownPointer

**Vibe writing for Markdown.** Point at anything, tell AI to fix it.

MarkdownPointer renders your Markdown and lets you click any element — headings, code blocks, table cells, Mermaid diagram nodes, KaTeX math — to copy a `filepath:line` reference. Paste it into your AI prompt, and the AI knows exactly where to look. This npm package bundles the MCP server and the viewer app — no separate installation needed.

<div align="center">
  <img width="640" alt="social-image" src="https://github.com/user-attachments/assets/cdae3548-1e23-4639-9b38-3e03c5c2a337" />
</div>

## Install

### Claude Code

```bash
claude mcp add mdp -- npx -y markdown-pointer
```

### Claude Desktop

Add to your Claude Desktop config (`%APPDATA%\Claude\claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "mdp": {
      "command": "npx",
      "args": ["-y", "markdown-pointer"]
    }
  }
}
```

### Other MCP Clients

Use `npx -y markdown-pointer` as the command in your MCP client's configuration.

## Usage

Ask Claude:

- "open README.md in mdp"
- "show the report in mdp and scroll to line 50"
- "export report.md to docx"
- "export slides.md to pptx"
- "import presentation.pptx to markdown"
- "show me slide 3 of slides.md"

## MCP Tools

| Tool | Description |
|------|-------------|
| `show_markdown` | Open files and scroll to a line |
| `get_status` | Get current window/tab state |
| `slide_control` | Navigate reveal.js slides |
| `get_slide_info` | Get slide shapes and content as text |
| `get_slide_image` | Get a slide as PNG image (requires PowerPoint) |
| `export_document` | Export to .pptx (built-in) or .docx (Pandoc) |
| `import_document` | Import .docx/.pptx to Markdown + extract images |
| `tag_asset` | Tag imported files and images in index.json |

## Features

| Feature | Description |
|---------|-------------|
| Point & Prompt | Click any rendered element to copy `filepath:line` to clipboard |
| Mermaid Diagrams | Flowchart, Sequence, Class, State, ER, Gantt, Pie, Git graph, Mindmap |
| KaTeX Math | Inline `$...$` and block `$$...$$` |
| SVG | Embedded font support |
| Live Reload | Auto-refresh on file changes |
| Export | `.pptx` (built-in Open XML), `.docx` (via Pandoc). Mermaid/SVG rendered as images |

## Requirements

- Windows 10/11
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

## Also Available

Install via [PowerShell Gallery](https://www.powershellgallery.com/packages/MarkdownPointer) for PowerShell integration:

```powershell
Install-Module MarkdownPointer
```

## License

MIT
