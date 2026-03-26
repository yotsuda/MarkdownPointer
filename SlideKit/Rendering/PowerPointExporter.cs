using System.Diagnostics;
using System.Runtime.Versioning;

namespace SlideKit.Rendering;

[SupportedOSPlatform("windows")]
public static class PowerPointExporter
{
    public static void ExportSlideImages(string pptxPath, string outputDir, int width = 1920, int height = 1080)
    {
        Directory.CreateDirectory(outputDir);

        // Find Export-Slides.ps1 next to the host executable
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "Export-Slides.ps1");

        if (!File.Exists(scriptPath))
            throw new FileNotFoundException("Export-Slides.ps1 not found", scriptPath);

        var psi = new ProcessStartInfo
        {
            FileName = "pwsh.exe",
            ArgumentList =
            {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", scriptPath,
                "-PptxPath", Path.GetFullPath(pptxPath),
                "-OutputDir", Path.GetFullPath(outputDir),
                "-Width", width.ToString(),
                "-Height", height.ToString(),
            },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(60000);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"PowerPoint export failed (exit {proc.ExitCode}): {stderr}");
    }

    public static bool IsAvailable()
    {
        return Type.GetTypeFromProgID("PowerPoint.Application") is not null;
    }
}
