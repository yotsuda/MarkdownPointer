# MarkdownPointer

A Markdown viewer designed for AI-assisted document review. Click any rendered element to copy its file path and line number to clipboard - perfect for pointing AI to specific locations in your documents.

## Key Feature: Pointing Mode

When reviewing AI-generated Markdown, click any element (headings, paragraphs, code blocks, Mermaid nodes, etc.) to instantly copy a reference like:

```
C:\docs\report.md:42
```

Paste this into your AI prompt to precisely point to the location that needs revision.

## Features

- **Pointing Mode** - Click any element to copy file path + line number
- **Mermaid Diagrams** - Flowchart, Sequence, Class, State, ER, Gantt, Pie, Git graph, Mindmap
- **KaTeX Math** - Inline (`$...$`) and block (`$$...$$`) math expressions
- **Multi-Tab Interface** - Open multiple files with drag-and-drop tab reordering
- **File Watching** - Auto-reload on file changes
- **MCP Server** - Integration with Claude Code, Claude Desktop and other MCP clients

## Requirements

- Windows 10/11
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0/runtime) (if not already installed)

## Installation

1. Download the latest zip from [Releases](https://github.com/yotsuda/MarkdownPointer/releases)
2. Extract to a folder (e.g., `C:\Tools\MarkdownPointer`)
3. Configure MCP Server for your AI client (see below)

You can also open files directly from the command line:

```cmd
C:\Tools\MarkdownPointer\mdp.exe README.md
```

<details>
<summary>Build from Source</summary>

```powershell
git clone https://github.com/yotsuda/MarkdownPointer.git
cd MarkdownPointer
.\Build-Deploy.ps1 -Platform win-x64
# Output: dist\MarkdownPointer-win-x64.zip
```

</details>

## MCP Server Setup

MarkdownPointer includes an MCP server for integration with Claude Code, Claude Desktop, and other MCP clients.

### Claude Code (Recommended)

```bash
claude mcp add mdp C:\Tools\MarkdownPointer\mdp-mcp.exe
```

Example prompts:

- "open README.md in mdp"
- "show the report in mdp and scroll to line 50"

### Other MCP Clients

mdp-mcp.exe is a standard MCP server using stdio transport. Configure your MCP client to run:

```
C:\Tools\MarkdownPointer\mdp-mcp.exe
```

For Claude Desktop, add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "mdp": {
      "command": "C:\\Tools\\MarkdownPointer\\mdp-mcp.exe"
    }
  }
}
```

### Available MCP Tools

| Tool | Description | Parameters |
|------|-------------|------------|
| `show_markdown` | Open a Markdown or SVG file | `path`, `line?` |

## Usage

### Pointing Mode

1. Click the **👆** button in the toolbar to enable pointing mode
2. Click any element in the rendered Markdown
3. The file path and line number are copied to clipboard
4. Paste into your AI prompt

### Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| `Ctrl+O` | Open file |
| `Ctrl+W` | Close current tab |
| `Ctrl+Tab` | Next tab |
| `Ctrl+Shift+Tab` | Previous tab |
| `F5` | Reload current file |
## License

MIT License