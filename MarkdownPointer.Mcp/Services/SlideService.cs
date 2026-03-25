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
    private readonly DeckParser _parser = new();
    private readonly PptxRenderer _renderer = new();
    private readonly PptxImporter _importer = new();

    private Deck? _deck;
    private string _sourceFile = "";
    private string _outputDir = "";

    public Deck? CurrentDeck => _deck;

    public string Load(string yamlPath)
    {
        _deck = _parser.ParseFile(yamlPath);
        _sourceFile = yamlPath;
        _outputDir = Path.Combine(Path.GetDirectoryName(yamlPath)!, ".pinpoint");
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
            return $"Loaded {_deck.Slides.Count} slides from {Path.GetFileName(yamlPath)} (no slide images: {ex.Message})";
        }

        return $"Loaded {_deck.Slides.Count} slides from {Path.GetFileName(yamlPath)}";
    }

    private const long EmuPerPt = 12700;

    public string GetSlideInfo(int? slideIndex = null)
    {
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

    public string UpdateYaml(string yamlPath, string newContent)
    {
        File.WriteAllText(yamlPath, newContent);
        return Load(yamlPath);
    }

    public string ImportPptx(string pptxPath, string? outputMdPath = null)
    {
        outputMdPath ??= Path.ChangeExtension(pptxPath, ".md");
        var assetsDir = Path.Combine(Path.GetDirectoryName(pptxPath)!, "assets");
        var deck = _importer.Convert(pptxPath, assetsDir);
        var mdConverter = new SlideKit.Parsing.DeckToMarkdownConverter();
        var md = mdConverter.Convert(deck);
        File.WriteAllText(outputMdPath, md);
        return md;
    }

    private static string Truncate(string? s, int maxLen)
    {
        if (s is null) return "";
        return s.Length <= maxLen ? s : s[..(maxLen - 3)] + "...";
    }
}
