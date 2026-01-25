@{
    RootModule = 'MarkdownPointer.psm1'
    ModuleVersion = '0.1.0'
    GUID = 'a1b2c3d4-e5f6-7890-abcd-ef1234567890'
    Author = 'Yoshifumi Tsuda'
    Description = 'PowerShell module for controlling MarkdownPointer'
    PowerShellVersion = '5.1'
    FunctionsToExport = @('Show-MarkdownPointer', 'Get-MarkdownPointerTab')
    CmdletsToExport = @()
    VariablesToExport = @()
    AliasesToExport = @()
    PrivateData = @{
        PSData = @{
            Tags = @('Markdown', 'Viewer', 'Preview')
            ProjectUri = 'https://github.com/yotsuda/MarkdownPointer'
        }
    }
}