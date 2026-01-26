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
- **MCP Server** - Integration with Claude Desktop and other MCP clients
- **PowerShell Module** - Control via `Show-MarkdownPointer` cmdlet

## Requirements

- Windows 10/11
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0/runtime) (if not already installed)

## Installation

### Option 1: Download from GitHub Releases

1. Download `MarkdownPointer-win-x64.zip` from [Releases](https://github.com/yotsuda/MarkdownPointer/releases)
2. Extract to a folder (e.g., `C:\Tools\MarkdownPointer`)
3. Run `MarkdownPointer.exe`

### Option 2: Build from Source

```powershell
git clone https://github.com/yotsuda/MarkdownPointer.git
cd MarkdownPointer
.\Build-Deploy.ps1 -Platform win-x64
# Output: dist\MarkdownPointer-win-x64.zip
```

## MCP Server Setup

MarkdownPointer includes an MCP server for integration with Claude Desktop and other MCP clients.

### Claude Desktop Configuration

Add to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "MarkdownPointer": {
      "command": "C:\\Tools\\MarkdownPointer\\MarkdownPointer.Mcp.exe"
    }
  }
}
```

### Available MCP Tools

| Tool | Description | Parameters |
|------|-------------|------------|
| `show_markdown` | Open a Markdown file | `path`, `line?` |
| `show_markdown_content` | Display Markdown text | `content`, `title?` |
| `get_tabs` | List open tabs | none |

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
| `Ctrl+1-9` | Switch to tab 1-9 |
| `F5` | Reload current file |

### PowerShell Module

```powershell
Import-Module MarkdownPointer

# Open a file
Show-MarkdownPointer .\README.md

# Open and scroll to specific line
Show-MarkdownPointer .\README.md -Line 50

# Render content directly
"# Hello World" | Show-MarkdownPointer

# List open tabs
Get-MarkdownPointerTab
```

## License

MIT License