using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentFormat.OpenXml.Packaging;
using SlideKit.Models;
using P = DocumentFormat.OpenXml.Presentation;
using D = DocumentFormat.OpenXml.Drawing;

namespace SlideKit.Rendering;

public class PptxImporter
{
    private const long EmuPerPt = 12700;

    public Deck Convert(string pptxPath, string? assetsDir = null)
    {
        using var pres = PresentationDocument.Open(pptxPath, false);
        var presentationPart = pres.PresentationPart
            ?? throw new InvalidOperationException("No PresentationPart found.");

        var deck = new Deck();
        var imageCtx = assetsDir is not null ? new ImageExportContext(assetsDir, pptxPath) : null;

        // Extract theme info from first slide master
        ExtractTheme(presentationPart, deck);

        // Extract slides in order
        var slideIdList = presentationPart.Presentation?.SlideIdList;
        if (slideIdList is null) return deck;

        int slideIndex = 0;
        foreach (var slideId in slideIdList.ChildElements.OfType<P.SlideId>())
        {
            var slidePart = (SlidePart)presentationPart.GetPartById(slideId.RelationshipId!);
            var yamlSlide = ConvertSlide(slidePart, imageCtx, slideIndex);
            deck.Slides.Add(yamlSlide);
            slideIndex++;
        }

        imageCtx?.SaveIndex();

        return deck;
    }

    private static void ExtractTheme(PresentationPart presentationPart, Deck deck)
    {
        var themePart = presentationPart.SlideMasterParts
            .FirstOrDefault()?.ThemePart;
        if (themePart?.Theme?.ThemeElements?.ColorScheme is not { } colorScheme)
            return;

        // Extract key colors
        var dark2 = GetColorFromElement(colorScheme.Dark2Color);
        var light2 = GetColorFromElement(colorScheme.Light2Color);
        var accent1 = GetColorFromElement(colorScheme.Accent1Color);

        if (dark2 is not null) deck.Theme.Colors["primary"] = dark2;
        if (accent1 is not null) deck.Theme.Colors["accent"] = accent1;
        if (light2 is not null) deck.Theme.Colors["light"] = light2;

        // Extract font
        var fontScheme = themePart.Theme.ThemeElements.FontScheme;
        var majorFont = (string?)(fontScheme?.MajorFont?.LatinFont?.Typeface
                     ?? fontScheme?.MajorFont?.EastAsianFont?.Typeface);
        if (majorFont is not null)
            deck.Theme.Font = majorFont;
    }

    private static string? GetColorFromElement(D.Color2Type? colorElement)
    {
        if (colorElement is null) return null;
        var rgb = colorElement.GetFirstChild<D.RgbColorModelHex>();
        if (rgb?.Val?.Value is not null) return rgb.Val.Value;
        var sys = colorElement.GetFirstChild<D.SystemColor>();
        return sys?.LastColor?.Value;
    }

    private static Slide ConvertSlide(SlidePart slidePart, ImageExportContext? imageCtx, int slideIndex)
    {
        var yamlSlide = new Slide();
        var shapeTree = slidePart.Slide?.CommonSlideData?.ShapeTree;
        if (shapeTree is null) return yamlSlide;

        // Collect slide text for image context tagging
        var slideTexts = new List<string>();

        // Shapes
        foreach (var shape in shapeTree.ChildElements)
        {
            switch (shape)
            {
                case P.Shape sp:
                    var yamlShape = ConvertShape(sp);
                    if (yamlShape is not null)
                    {
                        yamlSlide.Shapes.Add(yamlShape);
                        if (yamlShape.Text is not null)
                            slideTexts.Add(yamlShape.Text);
                        if (yamlShape.Bullets is not null)
                            slideTexts.AddRange(yamlShape.Bullets);
                    }
                    break;

                case P.GraphicFrame gf:
                    var tableShape = ConvertGraphicFrame(gf);
                    if (tableShape is not null)
                        yamlSlide.Shapes.Add(tableShape);
                    break;

                case P.Picture pic:
                    var imageShape = ConvertPicture(pic, slidePart, imageCtx, slideIndex, slideTexts);
                    if (imageShape is not null)
                        yamlSlide.Shapes.Add(imageShape);
                    break;
            }
        }

        // Speaker notes
        var notesPart = slidePart.NotesSlidePart;
        if (notesPart is not null)
        {
            var notesText = ExtractNotesText(notesPart);
            if (!string.IsNullOrWhiteSpace(notesText))
                yamlSlide.Notes = notesText;
        }

        return yamlSlide;
    }

    private static Shape? ConvertShape(P.Shape shape)
    {
        var spProps = shape.ShapeProperties;
        var xfrm = spProps?.GetFirstChild<D.Transform2D>();
        if (xfrm is null) return null;

        var offset = xfrm.Offset;
        var extents = xfrm.Extents;
        if (offset is null || extents is null) return null;

        var textBody = shape.TextBody;
        bool hasText = textBody is not null && HasMeaningfulText(textBody);

        var yamlShape = new Shape
        {
            X = (offset.X ?? 0) ,
            Y = (offset.Y ?? 0) ,
            Width = (extents.Cx ?? 0) ,
            Height = (extents.Cy ?? 0) ,
        };

        if (hasText)
        {
            yamlShape.Type = "textbox";
            ExtractTextProperties(textBody!, yamlShape);
        }
        else
        {
            yamlShape.Type = "rectangle";
            var solidFill = spProps?.GetFirstChild<D.SolidFill>();
            yamlShape.Fill = GetFillColor(solidFill);
        }

        return yamlShape;
    }

    private static bool HasMeaningfulText(P.TextBody textBody)
    {
        foreach (var para in textBody.Elements<D.Paragraph>())
        {
            foreach (var run in para.Elements<D.Run>())
            {
                var text = run.GetFirstChild<D.Text>()?.Text;
                if (!string.IsNullOrWhiteSpace(text))
                    return true;
            }
        }
        return false;
    }

    private static void ExtractTextProperties(P.TextBody textBody, Shape yamlShape)
    {
        var paragraphs = textBody.Elements<D.Paragraph>().ToList();

        // Check if it's a bullet list
        bool isBullets = paragraphs.Count > 1
            && paragraphs.Any(p =>
                p.ParagraphProperties?.GetFirstChild<D.CharacterBullet>() is not null
                || p.ParagraphProperties?.GetFirstChild<D.AutoNumberedBullet>() is not null
                || (p.ParagraphProperties?.Indent is not null && p.ParagraphProperties.Indent < 0));

        if (isBullets)
        {
            yamlShape.Bullets = [];
            foreach (var para in paragraphs)
            {
                var text = ExtractParagraphText(para);
                if (!string.IsNullOrWhiteSpace(text))
                    yamlShape.Bullets.Add(text);
            }

            // Extract bullet char
            var bulletChar = paragraphs
                .Select(p => p.ParagraphProperties?.GetFirstChild<D.CharacterBullet>()?.Char)
                .FirstOrDefault(c => c is not null);
            if (bulletChar is not null && bulletChar != "\u2022")
                yamlShape.BulletChar = bulletChar;
        }
        else
        {
            // Single/multi-line text - join paragraphs
            var allText = string.Join("\n", paragraphs
                .Select(ExtractParagraphText)
                .Where(t => !string.IsNullOrEmpty(t)));
            yamlShape.Text = allText;
        }

        // Extract formatting from first run
        var firstRun = paragraphs
            .SelectMany(p => p.Elements<D.Run>())
            .FirstOrDefault();

        if (firstRun is not null)
        {
            var runProps = firstRun.RunProperties;
            if (runProps is not null)
            {
                if (runProps.FontSize is not null)
                    yamlShape.FontSize = runProps.FontSize;
                if (runProps.Bold is { HasValue: true } bv && bv.Value)
                    yamlShape.Bold = true;

                var fill = runProps.GetFirstChild<D.SolidFill>();
                var color = GetFillColor(fill);
                if (color is not null)
                    yamlShape.Color = color;
            }
        }

        // Extract alignment from first paragraph
        var firstParaProps = paragraphs.FirstOrDefault()?.ParagraphProperties;
        if (firstParaProps?.Alignment?.Value is { } align)
        {
            if (align == D.TextAlignmentTypeValues.Center) yamlShape.Alignment = "center";
            else if (align == D.TextAlignmentTypeValues.Right) yamlShape.Alignment = "right";
            else if (align == D.TextAlignmentTypeValues.Left) yamlShape.Alignment = "left";
        }
    }

    private static string ExtractParagraphText(D.Paragraph paragraph)
    {
        var texts = paragraph.Elements<D.Run>()
            .Select(r => r.GetFirstChild<D.Text>()?.Text ?? "");
        return string.Join("", texts);
    }

    private static Shape? ConvertGraphicFrame(P.GraphicFrame graphicFrame)
    {
        var graphic = graphicFrame.GetFirstChild<D.Graphic>();
        var graphicData = graphic?.GraphicData;
        if (graphicData?.Uri != "http://schemas.openxmlformats.org/drawingml/2006/table")
            return null;

        var table = graphicData.GetFirstChild<D.Table>();
        if (table is null) return null;

        // Get position
        var xfrm = graphicFrame.GetFirstChild<P.Transform>();
        var offset = xfrm?.Offset;
        var extents = xfrm?.Extents;

        var yamlShape = new Shape
        {
            Type = "table",
            X = (offset?.X ?? 0) ,
            Y = (offset?.Y ?? 0) ,
            Width = (extents?.Cx ?? 0) ,
            Height = (extents?.Cy ?? 0) ,
            Headers = [],
            Rows = [],
        };

        var tableRows = table.Elements<D.TableRow>().ToList();
        if (tableRows.Count == 0) return yamlShape;

        // First row = headers (if table has FirstRow property)
        var tableProps = table.GetFirstChild<D.TableProperties>();
        bool hasHeaderRow = tableProps?.FirstRow is not null && tableProps.FirstRow.HasValue
            ? tableProps.FirstRow.Value : true;
        int startRow = 0;

        if (hasHeaderRow && tableRows.Count > 0)
        {
            yamlShape.Headers = ExtractTableRowTexts(tableRows[0]);
            startRow = 1;

            // Extract header styling
            var firstCell = tableRows[0].Elements<D.TableCell>().FirstOrDefault();
            var cellProps = firstCell?.TableCellProperties;
            var cellFill = cellProps?.GetFirstChild<D.SolidFill>();
            var headerFill = GetFillColor(cellFill);
            if (headerFill is not null)
                yamlShape.HeaderFill = headerFill;

            var firstRunProps = firstCell?.TextBody?.Elements<D.Paragraph>()
                .SelectMany(p => p.Elements<D.Run>())
                .FirstOrDefault()?.RunProperties;
            var headerColor = GetFillColor(firstRunProps?.GetFirstChild<D.SolidFill>());
            if (headerColor is not null)
                yamlShape.HeaderColor = headerColor;
        }

        for (int r = startRow; r < tableRows.Count; r++)
            yamlShape.Rows.Add(ExtractTableRowTexts(tableRows[r]));

        // Extract border color from first cell
        var borderCell = tableRows[0].Elements<D.TableCell>().FirstOrDefault();
        var topBorder = borderCell?.TableCellProperties?.GetFirstChild<D.TopBorderLineProperties>();
        var borderColor = GetFillColor(topBorder?.GetFirstChild<D.SolidFill>());
        if (borderColor is not null)
            yamlShape.BorderColor = borderColor;

        return yamlShape;
    }

    private static List<string> ExtractTableRowTexts(D.TableRow row)
    {
        return row.Elements<D.TableCell>()
            .Select(cell =>
            {
                var texts = cell.TextBody?.Elements<D.Paragraph>()
                    .SelectMany(p => p.Elements<D.Run>())
                    .Select(r => r.GetFirstChild<D.Text>()?.Text ?? "");
                return string.Join("", texts ?? []);
            })
            .ToList();
    }

    private static string? GetFillColor(D.SolidFill? fill)
    {
        if (fill is null) return null;
        var rgb = fill.GetFirstChild<D.RgbColorModelHex>();
        if (rgb?.Val?.Value is not null) return rgb.Val.Value;
        // SchemeColor would require theme resolution - skip for now
        return null;
    }

    private static string? ExtractNotesText(NotesSlidePart notesPart)
    {
        var shapeTree = notesPart.NotesSlide?.CommonSlideData?.ShapeTree;
        if (shapeTree is null) return null;

        foreach (var shape in shapeTree.Elements<P.Shape>())
        {
            // Find the notes placeholder (body type)
            var placeholder = shape.NonVisualShapeProperties
                ?.ApplicationNonVisualDrawingProperties
                ?.GetFirstChild<P.PlaceholderShape>();
            if (placeholder?.Type is null || placeholder.Type.Value != P.PlaceholderValues.Body) continue;

            var textBody = shape.TextBody;
            if (textBody is null) continue;

            var texts = textBody.Elements<D.Paragraph>()
                .Select(ExtractParagraphText)
                .Where(t => !string.IsNullOrEmpty(t));
            return string.Join("\n", texts);
        }

        return null;
    }

    private static Shape? ConvertPicture(P.Picture pic, SlidePart slidePart,
        ImageExportContext? imageCtx, int slideIndex, List<string> slideTexts)
    {
        var xfrm = pic.ShapeProperties?.GetFirstChild<D.Transform2D>();
        if (xfrm is null) return null;

        var offset = xfrm.Offset;
        var extents = xfrm.Extents;
        if (offset is null || extents is null) return null;

        var blipFill = pic.GetFirstChild<P.BlipFill>();
        var blip = blipFill?.Blip;
        if (blip?.Embed?.Value is not { } embedId) return null;

        var imagePart = (ImagePart)slidePart.GetPartById(embedId);
        string? source = null;

        if (imageCtx is not null)
        {
            var context = string.Join(" — ", slideTexts.Take(3).Select(t =>
                t.Length > 50 ? t[..50] : t));
            source = imageCtx.ExportImage(imagePart, slideIndex, context);
        }

        return new Shape
        {
            Type = "image",
            X = (offset.X ?? 0) ,
            Y = (offset.Y ?? 0) ,
            Width = (extents.Cx ?? 0) ,
            Height = (extents.Cy ?? 0) ,
            Source = source,
        };
    }
}

internal class ImageExportContext
{
    private readonly string _assetsDir;
    private readonly string _pptxFileName;
    private readonly Dictionary<string, ImageIndexEntry> _index;
    private readonly string _indexPath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public ImageExportContext(string assetsDir, string pptxPath)
    {
        _assetsDir = assetsDir;
        _pptxFileName = Path.GetFileName(pptxPath);
        _indexPath = Path.Combine(Path.GetDirectoryName(assetsDir)!, "index.json");
        _index = LoadExistingIndex();
    }

    public string ExportImage(ImagePart imagePart, int slideIndex, string context)
    {
        Directory.CreateDirectory(_assetsDir);

        // Read image bytes and compute hash
        using var stream = imagePart.GetStream();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();

        var hash = System.Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
        var ext = GetExtension(imagePart.ContentType);
        var fileName = $"{hash}{ext}";
        var filePath = Path.Combine(_assetsDir, fileName);

        // Write image file (skip if already exists — same hash = same content)
        if (!File.Exists(filePath))
            File.WriteAllBytes(filePath, bytes);

        // Update index
        var relativePath = $"media/{fileName}";

        if (_index.TryGetValue(relativePath, out var entry))
        {
            // Add source if not already present
            if (!entry.Sources.Any(s => s.File == _pptxFileName && s.Slide == slideIndex))
            {
                entry.Sources.Add(new ImageSource { File = _pptxFileName, Slide = slideIndex });
            }
        }
        else
        {
            // Get image dimensions from file header
            var (widthPx, heightPx) = GetImageDimensions(bytes);

            _index[relativePath] = new ImageIndexEntry
            {
                Format = ext.TrimStart('.'),
                WidthPx = widthPx,
                HeightPx = heightPx,
                SizeKb = (int)(bytes.Length / 1024),
                Context = string.IsNullOrWhiteSpace(context) ? null : context,
                Sources = [new ImageSource { File = _pptxFileName, Slide = slideIndex }],
            };
        }

        return relativePath;
    }

    public void SaveIndex()
    {
        if (_index.Count == 0) return;
        Directory.CreateDirectory(_assetsDir);

        var wrapper = new ImageIndex { Images = _index };
        var json = JsonSerializer.Serialize(wrapper, JsonOptions);
        File.WriteAllText(_indexPath, json);
    }

    private Dictionary<string, ImageIndexEntry> LoadExistingIndex()
    {
        if (!File.Exists(_indexPath))
            return [];

        try
        {
            var json = File.ReadAllText(_indexPath);
            var index = JsonSerializer.Deserialize<ImageIndex>(json, JsonOptions);
            return index?.Images ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string GetExtension(string contentType)
    {
        return contentType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpeg",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "image/x-emf" or "image/emf" => ".emf",
            "image/x-wmf" or "image/wmf" => ".wmf",
            "image/tiff" => ".tiff",
            "image/svg+xml" => ".svg",
            _ => ".png",
        };
    }

    private static (int width, int height) GetImageDimensions(byte[] bytes)
    {
        try
        {
            // PNG: width at offset 16 (4 bytes BE), height at offset 20
            if (bytes.Length > 24 && bytes[0] == 0x89 && bytes[1] == 0x50)
            {
                int w = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                int h = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                return (w, h);
            }

            // JPEG: scan for SOF0/SOF2 marker (0xFF 0xC0 or 0xFF 0xC2)
            if (bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8)
            {
                int i = 2;
                while (i < bytes.Length - 9)
                {
                    if (bytes[i] != 0xFF) { i++; continue; }
                    byte marker = bytes[i + 1];
                    if (marker is 0xC0 or 0xC2)
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
}

internal class ImageIndex
{
    public Dictionary<string, ImageIndexEntry> Images { get; set; } = [];
}

internal class ImageIndexEntry
{
    public string Format { get; set; } = "";
    public int WidthPx { get; set; }
    public int HeightPx { get; set; }
    public int SizeKb { get; set; }
    public string? Context { get; set; }
    public List<ImageSource> Sources { get; set; } = [];
    public List<string> Tags { get; set; } = [];
}

internal class ImageSource
{
    public string File { get; set; } = "";
    public int Slide { get; set; }
}
