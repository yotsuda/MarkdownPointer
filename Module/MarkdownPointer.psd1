@{
    RootModule = 'MarkdownPointer.psm1'
    ModuleVersion = '0.2.0'
    GUID = '4c50c9c4-d155-457d-a3a3-e3952253b51d'
    Author = 'Yoshifumi Tsuda'
    Copyright = '(c) 2025 Yoshifumi Tsuda. All rights reserved.'
    Description = 'Markdown viewer with AI-assisted pointing mode. Click any element (headings, code lines, table cells, Mermaid diagrams, KaTeX math) to copy a [filepath:line] reference for AI prompts. Includes MCP server for Claude Code / Claude Desktop integration.'
    PowerShellVersion = '7.4'
    FunctionsToExport = @('Show-MarkdownPointer', 'Get-MarkdownPointerTab', 'Get-MarkdownPointerMCPPath')
    CmdletsToExport = @()
    VariablesToExport = @()
    AliasesToExport = @('mdp')
    PrivateData = @{
        PSData = @{
            Tags = @('Markdown', 'Viewer', 'Preview', 'MCP', 'Claude', 'AI', 'WPF', 'Mermaid', 'KaTeX')
            LicenseUri = 'https://github.com/yotsuda/MarkdownPointer/blob/master/LICENSE'
            ProjectUri = 'https://github.com/yotsuda/MarkdownPointer'
        }
    }
}
