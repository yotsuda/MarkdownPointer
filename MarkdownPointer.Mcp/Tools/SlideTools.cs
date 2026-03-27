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
    [Description("Add or update tags and description for a file in index.json. Works for both images (stored in 'images' section) and .md files (stored in 'documents' section). Always use this tool (not manual file edits) to ensure consistent index format.")]
    public static string TagAsset(
        SlideService service,
        [Description("Path to the .imported/ directory containing index.json")] string dir,
        [Description("Filename: .md filename (e.g. 'slides.pptx.md') for document summaries, or media path relative to dir (e.g. 'media/abc123.png')")] string file,
        [Description("Comma-separated tags. For images: content type (screenshot/diagram/chart/photo/logo/icon), subject, usage (hero/inline/background/decorative). For .md: topic tags (e.g. 'setup-guide, orchestrator, powershell')")] string tags,
        [Description("Short description. For images: what the image shows. For .md: summary of the document content.")] string? description = null)
    {
        try
        {
            var tagArray = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return service.TagAsset(dir, file, tagArray, description);
        }
        catch (Exception ex)
        {
            return $"Error tagging {file}: {ex.Message}";
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
            string? importedDir = null;
            var allMediaFiles = new List<string>();

            foreach (var file in files)
            {
                var impDir = Path.Combine(Path.GetDirectoryName(file)!, ".imported");
                importedDir ??= impDir;
                var mdPath = output_path ?? Path.Combine(impDir, Path.GetFileName(file) + ".md");

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
                    ".pptx" => service.ImportPptx(file, null),
                    ".docx" => service.ImportDocx(file, null),
                    _ => throw new ArgumentException($"Unsupported file type '{ext}'. Use .pptx or .docx.")
                };

                imported.Add(mdPath);

                // List extracted media
                var mediaDir = Path.Combine(impDir, "media");
                if (Directory.Exists(mediaDir))
                {
                    var mediaFiles = Directory.GetFiles(mediaDir, "*.*", SearchOption.AllDirectories)
                        .Select(f => Path.GetRelativePath(impDir, f).Replace('\\', '/'))
                        .ToList();
                    allMediaFiles.AddRange(mediaFiles);
                }
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== Summary ===");
            sb.AppendLine($"Imported: {imported.Count}, Skipped (up-to-date): {skipped.Count}");
            if (skipped.Count > 0)
                sb.AppendLine($"Skipped: {string.Join(", ", skipped)}");
            sb.AppendLine();

            if (imported.Count > 0)
            {
                sb.AppendLine("=== Imported ===");
                foreach (var mdPath in imported)
                    sb.AppendLine($"  {mdPath}");
                sb.AppendLine();
            }

            if (allMediaFiles.Count > 0)
            {
                sb.AppendLine($"=== Media ({allMediaFiles.Count} files) ===");
                foreach (var m in allMediaFiles)
                    sb.AppendLine($"  {m}");
                sb.AppendLine();
            }

            // Instructions for AI to tag assets
            if (imported.Count > 0 && importedDir != null)
            {
                sb.AppendLine("=== ACTION REQUIRED ===");
                sb.AppendLine($"dir: {importedDir}");
                sb.AppendLine();
                sb.AppendLine("1. Read each .md file above, then call tag_asset with:");
                sb.AppendLine("   - file: the .md filename (e.g. 'slides.pptx.md')");
                sb.AppendLine("   - tags: topic tags (e.g. 'setup-guide, orchestrator')");
                sb.AppendLine("   - description: one-line summary of the document");
                if (allMediaFiles.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("2. Read each media file, then call tag_asset with:");
                    sb.AppendLine("   - file: media path (e.g. 'media/abc123.png')");
                    sb.AppendLine("   - tags: content type (screenshot/diagram/chart/photo/logo/icon),");
                    sb.AppendLine("           subject, usage (hero/inline/background/decorative)");
                    sb.AppendLine("   - description: what the file contains");
                }
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error importing {path}: {ex.Message}";
        }
    }
}
