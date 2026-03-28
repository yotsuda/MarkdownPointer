using MarkdownPointer.Mcp;
using MarkdownPointer.Mcp.Services;

namespace MarkdownPointer.Tests;

public class VersionCheckTests : IDisposable
{
    private readonly string _tempRoot;

    public VersionCheckTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"MdpVersionTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, true); } catch { }
    }

    // --- DetectNewerVersion tests ---

    [Fact]
    public void DetectNewerVersion_NoSiblingVersions_ReturnsNull()
    {
        // Layout: MarkdownPointer/0.10.0/bin/
        var binDir = CreateVersionLayout("0.10.0");

        var result = Program.DetectNewerVersion(binDir + Path.DirectorySeparatorChar);

        Assert.Null(result);
    }

    [Fact]
    public void DetectNewerVersion_OlderSiblingOnly_ReturnsNull()
    {
        // Layout: MarkdownPointer/0.10.0/bin/ and 0.9.0/
        var binDir = CreateVersionLayout("0.10.0");
        CreateSiblingVersion("0.9.0");

        var result = Program.DetectNewerVersion(binDir + Path.DirectorySeparatorChar);

        Assert.Null(result);
    }

    [Fact]
    public void DetectNewerVersion_NewerSiblingExists_ReturnsWarning()
    {
        // Layout: MarkdownPointer/0.10.0/bin/ and 0.11.0/bin/
        var binDir = CreateVersionLayout("0.10.0");
        CreateSiblingVersion("0.11.0");

        var result = Program.DetectNewerVersion(binDir + Path.DirectorySeparatorChar);

        Assert.NotNull(result);
        Assert.Contains("v0.10.0", result);
        Assert.Contains("v0.11.0", result);
        Assert.Contains("MCP config is outdated", result);
        Assert.Contains("Register-MdpToClaudeCode", result);
    }

    [Fact]
    public void DetectNewerVersion_MultipleNewerVersions_ReportsLatest()
    {
        var binDir = CreateVersionLayout("0.10.0");
        CreateSiblingVersion("0.11.0");
        CreateSiblingVersion("0.12.0");
        CreateSiblingVersion("0.9.0");

        var result = Program.DetectNewerVersion(binDir + Path.DirectorySeparatorChar);

        Assert.NotNull(result);
        Assert.Contains("v0.12.0", result);
        Assert.DoesNotContain("v0.11.0", result!.Split("→")[1]);
    }

    [Fact]
    public void DetectNewerVersion_NotVersionedDirectory_ReturnsNull()
    {
        // Layout: SomeDir/bin/ (no version in path)
        var binDir = Path.Combine(_tempRoot, "bin");
        Directory.CreateDirectory(binDir);

        var result = Program.DetectNewerVersion(binDir + Path.DirectorySeparatorChar);

        Assert.Null(result);
    }

    // --- ThrowIfPathMismatch tests ---

    [Fact]
    public void ThrowIfPathMismatch_SameDirectory_NoException()
    {
        NamedPipeClient.ThrowIfPathMismatch(
            @"C:\Modules\MarkdownPointer\0.11.0\bin\mdp.exe",
            @"C:\Modules\MarkdownPointer\0.11.0\bin\mdp.exe");
    }

    [Fact]
    public void ThrowIfPathMismatch_SameDirectoryCaseInsensitive_NoException()
    {
        NamedPipeClient.ThrowIfPathMismatch(
            @"C:\Modules\MarkdownPointer\bin\mdp.exe",
            @"c:\modules\markdownpointer\bin\mdp.exe");
    }

    [Fact]
    public void ThrowIfPathMismatch_DifferentVersion_ThrowsWithMessage()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            NamedPipeClient.ThrowIfPathMismatch(
                @"C:\Modules\MarkdownPointer\0.11.0\bin\mdp.exe",
                @"C:\Modules\MarkdownPointer\0.10.0\bin\mdp.exe"));

        Assert.Contains("Version mismatch", ex.Message);
        Assert.Contains("0.10.0", ex.Message);
        Assert.Contains("0.11.0", ex.Message);
    }

    [Fact]
    public void ThrowIfPathMismatch_CompletelyDifferentPath_ThrowsWithMessage()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            NamedPipeClient.ThrowIfPathMismatch(
                @"C:\Modules\MarkdownPointer\bin\mdp.exe",
                @"D:\OldTools\mdp.exe"));

        Assert.Contains("Version mismatch", ex.Message);
        Assert.Contains("Register-MdpToClaudeCode", ex.Message);
    }

    // --- Helpers ---

    private string CreateVersionLayout(string version)
    {
        // Creates: _tempRoot/MarkdownPointer/<version>/bin/
        var moduleRoot = Path.Combine(_tempRoot, "MarkdownPointer");
        var binDir = Path.Combine(moduleRoot, version, "bin");
        Directory.CreateDirectory(binDir);
        return binDir;
    }

    private void CreateSiblingVersion(string version)
    {
        var moduleRoot = Path.Combine(_tempRoot, "MarkdownPointer");
        Directory.CreateDirectory(Path.Combine(moduleRoot, version));
    }
}
