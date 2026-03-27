using System.Collections.Generic;
using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using MarkdownPointer.Mcp.Services;

namespace MarkdownPointer.Mcp.Tools;

[McpServerToolType]
public class SlideTools
{
    [McpServerTool]
    [Description("Get structured text description of slide shapes and content. Very low token cost. Use this first before requesting an image.")]
    public static string GetSlideInfo(
        SlideService service,
        [Description("Slide index (0-based). Omit to get all slides.")] int? slide_index = null)
    {
        return service.GetSlideInfo(slide_index);
    }

    [McpServerTool]
    [Description("Get a rendered slide image. Use small width (320-480) to save tokens. Only request when you need to visually verify layout.")]
    public static IEnumerable<ContentBlock> GetSlideImage(
        SlideService service,
        [Description("Slide index (0-based)")] int slide_index,
        [Description("Image width in pixels. Default 480 (270p). Use 320 for quick check, 960 for detail.")] int width = 480)
    {
        try
        {
            var bytes = service.GetSlideImage(slide_index, width);
            if (bytes is null)
                return [new TextContentBlock { Text = "Error: Slide not found or no deck loaded." }];

            return [ImageContentBlock.FromBytes(bytes, "image/jpeg")];
        }
        catch (Exception ex)
        {
            return [new TextContentBlock { Text = $"Error in GetSlideImage: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}" }];
        }
    }

    [McpServerTool]
    [Description("Add or update tags for an image in assets/index.json. Use this to annotate extracted images with content type, subject, and usage hints. Always use this tool (not manual file edits) to ensure consistent index format.")]
    public static string TagAsset(
        SlideService service,
        [Description("Path to the assets/ directory containing index.json")] string assets_dir,
        [Description("Image filename relative to assets/ (e.g. 'abc123.png' or 'media/image1.png')")] string image,
        [Description("Comma-separated tags: content type (screenshot/diagram/chart/photo/logo/icon), subject (architecture-diagram, login-screen), usage (hero/inline/background/decorative)")] string tags,
        [Description("Optional short description of the image content")] string? description = null)
    {
        try
        {
            var tagArray = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return service.TagAsset(assets_dir, image, tagArray, description);
        }
        catch (Exception ex)
        {
            return $"Error tagging {image}: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Import PPTX/DOCX files and convert to Markdown. Accepts a file path or folder path (imports all .pptx/.docx in the folder). Skips files whose .md output already exists and is newer than the source. Extracts text, tables, and images (saved to assets/ folder with index.json metadata). PPTX uses SlideKit, DOCX uses Pandoc.")]
    public static string ImportDocument(
        SlideService service,
        [Description("Absolute path to a .pptx/.docx file, or a folder containing them")] string path,
        [Description("Optional: output path for the .md file (single-file mode only). Defaults to same name with .md extension.")] string? output_path = null)
    {
        try
        {
            // Folder mode: collect all .docx/.pptx files
            List<string> files;
            if (Directory.Exists(path))
            {
                files = Directory.GetFiles(path, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f =>
                    {
                        var e = Path.GetExtension(f).ToLowerInvariant();
                        return e is ".pptx" or ".docx";
                    })
                    .ToList();

                if (files.Count == 0)
                    return $"No .pptx or .docx files found in {path}";

                // Folder mode ignores output_path
                output_path = null;
            }
            else
            {
                files = [path];
            }

            var results = new System.Text.StringBuilder();
            var skipped = new List<string>();
            var imported = new List<string>();
            var allImageFiles = new List<(string assetsDir, List<string> images)>();

            foreach (var file in files)
            {
                var mdPath = output_path ?? Path.ChangeExtension(file, ".md");

                // Skip if .md exists and is newer than source
                if (File.Exists(mdPath))
                {
                    var sourceTime = File.GetLastWriteTimeUtc(file);
                    var mdTime = File.GetLastWriteTimeUtc(mdPath);
                    if (mdTime > sourceTime)
                    {
                        skipped.Add(Path.GetFileName(file));
                        continue;
                    }
                }

                var ext = Path.GetExtension(file).ToLowerInvariant();
                var md = ext switch
                {
                    ".pptx" => service.ImportPptx(file, output_path),
                    ".docx" => service.ImportDocx(file, output_path),
                    _ => throw new ArgumentException($"Unsupported file type '{ext}'. Use .pptx or .docx.")
                };

                imported.Add(Path.GetFileName(file));

                var assetsDir = Path.Combine(Path.GetDirectoryName(file)!, "assets");

                results.AppendLine($"=== Import Complete: {Path.GetFileName(file)} ===");
                results.AppendLine($"Source: {file}");
                results.AppendLine($"Markdown: {mdPath}");

                // List extracted images
                var imageFiles = new List<string>();
                if (Directory.Exists(assetsDir))
                {
                    imageFiles = Directory.GetFiles(assetsDir, "*.*", SearchOption.AllDirectories)
                        .Where(f => !f.EndsWith("index.json", StringComparison.OrdinalIgnoreCase))
                        .Select(f => Path.GetRelativePath(Path.GetDirectoryName(file)!, f).Replace('\\', '/'))
                        .ToList();
                    if (imageFiles.Count > 0)
                    {
                        results.AppendLine($"Assets directory: {assetsDir}");
                        results.AppendLine($"Extracted images ({imageFiles.Count}):");
                        foreach (var img in imageFiles)
                            results.AppendLine($"  - {img}");
                        allImageFiles.Add((assetsDir, imageFiles));
                    }
                }

                results.AppendLine();
                results.AppendLine("=== Markdown Content ===");
                results.AppendLine(md);
                results.AppendLine();
            }

            // Summary
            var summary = new System.Text.StringBuilder();
            summary.AppendLine($"=== Summary ===");
            summary.AppendLine($"Imported: {imported.Count}, Skipped (up-to-date): {skipped.Count}");
            if (skipped.Count > 0)
                summary.AppendLine($"Skipped: {string.Join(", ", skipped)}");
            summary.AppendLine();

            // Instructions for AI to tag images
            if (allImageFiles.Count > 0)
            {
                summary.AppendLine("=== ACTION REQUIRED ===");
                summary.AppendLine("Read each image listed above and call tag_asset for each with:");
                summary.AppendLine("  - tags: content type (screenshot/diagram/chart/photo/logo/icon),");
                summary.AppendLine("          subject (e.g. architecture-diagram, login-screen),");
                summary.AppendLine("          usage hint (hero/inline/background/decorative)");
                summary.AppendLine("  - description: short description of the image content");
                foreach (var (assetsDir, _) in allImageFiles)
                    summary.AppendLine($"  - assets_dir: {assetsDir}");
            }

            return summary.ToString() + results.ToString();
        }
        catch (Exception ex)
        {
            return $"Error importing {path}: {ex.Message}";
        }
    }
}
