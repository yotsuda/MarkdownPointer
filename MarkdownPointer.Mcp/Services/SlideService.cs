using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SlideKit.Models;
using SlideKit.Parsing;
using SlideKit.Rendering;

namespace MarkdownPointer.Mcp.Services;

public class SlideService
{
    private readonly MarkdownToDeckConverter _mdConverter = new();
    private readonly PptxRenderer _renderer = new();
    private readonly PptxImporter _importer = new();
    private readonly NamedPipeClient _pipeClient;

    private Deck? _deck;
    private string _sourceFile = "";
    private string _outputDir = "";

    public SlideService(NamedPipeClient pipeClient)
    {
        _pipeClient = pipeClient;
    }

    public Deck? CurrentDeck => _deck;

    public string Load(string path)
    {
        _deck = _mdConverter.ConvertFile(path);
        _sourceFile = path;
        _outputDir = Path.Combine(Path.GetDirectoryName(path)!, ".pinpoint");
        Directory.CreateDirectory(_outputDir);

        var pptxPath = Path.Combine(_outputDir, "preview.pptx");
        _renderer.Render(_deck, pptxPath);

        Thread.Sleep(500);

        try
        {
            PowerPointExporter.ExportSlideImages(pptxPath, _outputDir);
        }
        catch (Exception ex)
        {
            return $"Loaded {_deck.Slides.Count} slides from {Path.GetFileName(path)} (no slide images: {ex.Message})";
        }

        return $"Loaded {_deck.Slides.Count} slides from {Path.GetFileName(path)}";
    }

    private const long EmuPerPt = 12700;

    private void AutoLoadIfNeeded()
    {
        if (_deck is not null) return;
        try
        {
            var response = _pipeClient.SendCommandAsync(new PipeCommand { Command = "status" }).GetAwaiter().GetResult();
            if (response == null) return;
            var root = response.RootElement;
            if (!root.TryGetProperty("windows", out var windows)) return;
            foreach (var win in windows.EnumerateArray())
            {
                if (!win.TryGetProperty("tabs", out var tabs)) continue;
                foreach (var tab in tabs.EnumerateArray())
                {
                    if (tab.TryGetProperty("isSelected", out var sel) && sel.GetBoolean() &&
                        tab.TryGetProperty("path", out var pathProp))
                    {
                        var path = pathProp.GetString();
                        if (!string.IsNullOrEmpty(path) && Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase))
                        {
                            Load(path);
                            return;
                        }
                    }
                }
            }
        }
        catch { }
    }

    public string GetSlideInfo(int? slideIndex = null)
    {
        AutoLoadIfNeeded();
        if (_deck is null) return "No deck loaded. Call load_deck first.";

        var sb = new StringBuilder();
        string font = _deck.Theme.Font;
        var slides = slideIndex.HasValue
            ? _deck.Slides.Where((_, i) => i == slideIndex.Value)
            : _deck.Slides;

        int idx = slideIndex ?? 0;
        foreach (var slide in slides)
        {
            int i = slideIndex ?? idx;
            sb.AppendLine($"## Slide {i + 1}");
            sb.AppendLine($"  Shapes: {slide.Shapes.Count}");
            if (slide.Notes is not null)
                sb.AppendLine($"  Notes: {slide.Notes}");

            var warnings = new List<string>();

            foreach (var shape in slide.Shapes)
            {
                long xPt = shape.X / EmuPerPt;
                long yPt = shape.Y / EmuPerPt;
                long wPt = shape.Width / EmuPerPt;
                long hPt = shape.Height / EmuPerPt;

                sb.Append($"  [{shape.Type} ({xPt},{yPt}) {wPt}x{hPt}]");

                switch (shape.Type.ToLowerInvariant())
                {
                    case "textbox":
                        string text;
                        if (shape.Bullets is { Count: > 0 })
                        {
                            text = string.Join("\n", shape.Bullets.Select(b => $"\u2022 {b}"));
                            int textH = MeasureTextHeight(text, shape.FontSize ?? 1800, shape.Width, font);
                            sb.AppendLine($" text_h={textH} bullets={shape.Bullets.Count}");
                            foreach (var item in shape.Bullets)
                                sb.AppendLine($"    - {Truncate(item, 60)}");
                            if (textH > hPt)
                                warnings.Add($"  ⚠ textbox \"{Truncate(shape.Bullets[0], 30)}...\" overflows (text needs {textH}pt, box is {hPt}pt)");
                        }
                        else
                        {
                            text = shape.Text ?? "";
                            int textH = MeasureTextHeight(text, shape.FontSize ?? 1800, shape.Width, font);
                            sb.AppendLine($" text_h={textH} \"{Truncate(shape.Text, 60)}\"");
                            if (textH > hPt)
                                warnings.Add($"  ⚠ textbox \"{Truncate(shape.Text, 30)}\" overflows (text needs {textH}pt, box is {hPt}pt)");
                        }
                        break;
                    case "rectangle":
                        sb.AppendLine($" fill={shape.Fill}");
                        break;
                    case "table":
                        sb.AppendLine($" {shape.Headers?.Count ?? 0} cols x {shape.Rows?.Count ?? 0} rows");
                        break;
                    default:
                        sb.AppendLine();
                        break;
                }
            }

            // Overlap detection (skip rectangles — typically backgrounds)
            var contentShapes = slide.Shapes
                .Where(s => s.Type.ToLowerInvariant() != "rectangle")
                .ToList();
            for (int a = 0; a < contentShapes.Count; a++)
            {
                for (int b = a + 1; b < contentShapes.Count; b++)
                {
                    var sa = contentShapes[a];
                    var sb2 = contentShapes[b];
                    if (sa.X < sb2.X + sb2.Width && sa.X + sa.Width > sb2.X &&
                        sa.Y < sb2.Y + sb2.Height && sa.Y + sa.Height > sb2.Y)
                    {
                        var nameA = GetShapeLabel(sa);
                        var nameB = GetShapeLabel(sb2);
                        warnings.Add($"  ⚠ {sa.Type} \"{nameA}\" overlaps with {sb2.Type} \"{nameB}\"");
                    }
                }
            }

            if (warnings.Count > 0)
            {
                foreach (var w in warnings)
                    sb.AppendLine(w);
            }

            sb.AppendLine();
            idx++;
        }

        return sb.ToString();
    }

    private int MeasureTextHeight(string text, int fontSizeHundredths, long widthEmu, string font)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        double fontSize = fontSizeHundredths / 100.0;
        double maxWidthPx = widthEmu / 12192000.0 * 1920;

        var ft = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(font),
            fontSize,
            Brushes.Black,
            96);
        ft.MaxTextWidth = Math.Max(maxWidthPx, 1);

        return (int)Math.Ceiling(ft.Height / 2.0);
    }

    private static string GetShapeLabel(Shape shape)
    {
        return shape.Type.ToLowerInvariant() switch
        {
            "textbox" => Truncate(shape.Text ?? shape.Bullets?.FirstOrDefault(), 20) ?? shape.Type,
            "table" => $"table({shape.Headers?.Count ?? 0}x{shape.Rows?.Count ?? 0})",
            _ => shape.Type,
        };
    }

    public byte[]? GetSlideImage(int slideIndex, int width = 480)
    {
        AutoLoadIfNeeded();
        if (_deck is null || slideIndex < 0 || slideIndex >= _deck.Slides.Count)
            return null;

        var imagePath = Path.Combine(_outputDir, $"slide{slideIndex}.png");
        if (!File.Exists(imagePath)) return null;

        var original = new BitmapImage();
        original.BeginInit();
        original.CacheOption = BitmapCacheOption.OnLoad;
        original.UriSource = new Uri(imagePath);
        original.EndInit();
        original.Freeze();

        int height = (int)(width * 9.0 / 16.0);
        var resized = new TransformedBitmap(original,
            new ScaleTransform(
                (double)width / original.PixelWidth,
                (double)height / original.PixelHeight));

        var encoder = new JpegBitmapEncoder { QualityLevel = 60 };
        encoder.Frames.Add(BitmapFrame.Create(resized));

        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    public void ExportPptx(string mdPath, string outputPath)
    {
        var deck = _mdConverter.ConvertFile(mdPath);
        _renderer.Render(deck, outputPath, basePath: Path.GetDirectoryName(mdPath));
    }

    private static string GetImportedDir(string sourcePath)
    {
        return Path.Combine(Path.GetDirectoryName(sourcePath)!, ".imported");
    }

    private static string GetImportedMdPath(string sourcePath)
    {
        var importedDir = GetImportedDir(sourcePath);
        return Path.Combine(importedDir, Path.GetFileName(sourcePath) + ".md");
    }

    public string ImportPptx(string pptxPath, string? outputMdPath = null)
    {
        var importedDir = GetImportedDir(pptxPath);
        outputMdPath ??= GetImportedMdPath(pptxPath);
        var mediaDir = Path.Combine(importedDir, "media");
        Directory.CreateDirectory(mediaDir);

        var deck = _importer.Convert(pptxPath, mediaDir);
        var mdConverter = new SlideKit.Parsing.DeckToMarkdownConverter();
        var md = mdConverter.Convert(deck);
        File.WriteAllText(outputMdPath, md);
        return md;
    }

    public string ImportDocx(string docxPath, string? outputMdPath = null)
    {
        var importedDir = GetImportedDir(docxPath);
        outputMdPath ??= GetImportedMdPath(docxPath);
        var mediaDir = Path.Combine(importedDir, "media");
        Directory.CreateDirectory(mediaDir);

        // Use Pandoc to convert docx → markdown with image extraction
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "pandoc",
            Arguments = $"-f docx -t markdown --extract-media=\"{mediaDir}\" -o \"{outputMdPath}\" \"{docxPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start Pandoc");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Pandoc failed: {stderr.Trim()}");

        // Build media index
        BuildImageIndex(importedDir, mediaDir);

        return File.ReadAllText(outputMdPath);
    }

    private static void BuildImageIndex(string baseDir, string assetsDir)
    {
        if (!Directory.Exists(assetsDir)) return;
        var imageFiles = Directory.GetFiles(assetsDir, "*.*", SearchOption.AllDirectories)
            .Where(f => IsImageFile(f))
            .ToList();
        if (imageFiles.Count == 0) return;

        var index = new Dictionary<string, ImageIndexInfo>();
        foreach (var file in imageFiles)
        {
            var relativePath = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
            var bytes = File.ReadAllBytes(file);
            var (w, h) = GetImageDimensions(bytes);
            index[relativePath] = new ImageIndexInfo
            {
                Format = Path.GetExtension(file).TrimStart('.'),
                WidthPx = w,
                HeightPx = h,
                SizeKb = (int)(bytes.Length / 1024),
                Tags = InferTags(w, h, bytes.Length)
            };
        }

        var json = System.Text.Json.JsonSerializer.Serialize(
            new { images = index },
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        File.WriteAllText(Path.Combine(baseDir, "index.json"), json);
    }

    private static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".svg" or ".emf" or ".wmf" or ".tiff";
    }

    private static List<string> InferTags(int width, int height, long sizeBytes)
    {
        var tags = new List<string>();
        if (width > 0 && height > 0)
        {
            var aspect = (double)width / height;
            if (aspect > 2.0) tags.Add("wide");
            else if (aspect < 0.5) tags.Add("tall");
            if (width >= 1920) tags.Add("high-res");
            else if (width <= 200) tags.Add("icon");
            else if (width <= 64) tags.Add("thumbnail");
        }
        if (sizeBytes > 1024 * 1024) tags.Add("large-file");
        return tags;
    }

    private static (int Width, int Height) GetImageDimensions(byte[] bytes)
    {
        try
        {
            if (bytes.Length > 24 && bytes[0] == 0x89 && bytes[1] == 0x50) // PNG
            {
                int w = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                int h = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                return (w, h);
            }
            if (bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8) // JPEG
            {
                int i = 2;
                while (i < bytes.Length - 9)
                {
                    if (bytes[i] != 0xFF) { i++; continue; }
                    if (bytes[i + 1] is 0xC0 or 0xC2)
                    {
                        int h = (bytes[i + 5] << 8) | bytes[i + 6];
                        int w = (bytes[i + 7] << 8) | bytes[i + 8];
                        return (w, h);
                    }
                    int len = (bytes[i + 2] << 8) | bytes[i + 3];
                    i += 2 + len;
                }
            }
        }
        catch { }
        return (0, 0);
    }

    public string TagAsset(string dir, string file, string[] tags, string? description = null)
    {
        var indexPath = Path.Combine(dir, "index.json");
        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        // Load existing index
        var images = new Dictionary<string, ImageIndexInfo>();
        var documents = new Dictionary<string, DocumentIndexInfo>();
        if (File.Exists(indexPath))
        {
            var existing = System.Text.Json.JsonDocument.Parse(File.ReadAllText(indexPath));
            if (existing.RootElement.TryGetProperty("images", out var imagesNode))
                images = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, ImageIndexInfo>>(
                    imagesNode.GetRawText(), jsonOptions) ?? [];
            if (existing.RootElement.TryGetProperty("documents", out var docsNode))
                documents = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, DocumentIndexInfo>>(
                    docsNode.GetRawText(), jsonOptions) ?? [];
        }

        var isDocument = Path.GetExtension(file).ToLowerInvariant() is ".md";

        if (isDocument)
        {
            documents[file] = new DocumentIndexInfo
            {
                Description = description,
                Tags = tags.ToList()
            };
        }
        else
        {
            if (images.TryGetValue(file, out var entry))
            {
                entry.Tags = tags.ToList();
                entry.Description = description;
            }
            else
            {
                var filePath = Path.Combine(dir, file);
                var info = new ImageIndexInfo { Tags = tags.ToList(), Description = description };
                if (File.Exists(filePath))
                {
                    var bytes = File.ReadAllBytes(filePath);
                    var (w, h) = GetImageDimensions(bytes);
                    info.Format = Path.GetExtension(file).TrimStart('.');
                    info.WidthPx = w;
                    info.HeightPx = h;
                    info.SizeKb = (int)(bytes.Length / 1024);
                }
                images[file] = info;
            }
        }

        var json = System.Text.Json.JsonSerializer.Serialize(new { documents, images }, jsonOptions);
        File.WriteAllText(indexPath, json);

        return $"Tagged '{file}': [{string.Join(", ", tags)}]" +
               (description != null ? $" — {description}" : "");
    }

    private static string Truncate(string? s, int maxLen)
    {
        if (s is null) return "";
        return s.Length <= maxLen ? s : s[..(maxLen - 3)] + "...";
    }

    private class DocumentIndexInfo
    {
        public string? Description { get; set; }
        public List<string> Tags { get; set; } = [];
    }

    private class ImageIndexInfo
    {
        public string Format { get; set; } = "";
        public int WidthPx { get; set; }
        public int HeightPx { get; set; }
        public int SizeKb { get; set; }
        public string? Description { get; set; }
        public List<string> Tags { get; set; } = [];
    }
}
