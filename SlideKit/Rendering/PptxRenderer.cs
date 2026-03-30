using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using SlideKit.Models;
using P = DocumentFormat.OpenXml.Presentation;
using D = DocumentFormat.OpenXml.Drawing;

namespace SlideKit.Rendering;

public class PptxRenderer
{
    private const long SlideWidth = 12192000;
    private const long SlideHeight = 6858000;

    public void Render(Deck deck, string outputPath, string? templatePath = null, string? basePath = null)
    {
        basePath ??= Path.GetDirectoryName(outputPath) ?? ".";
        templatePath ??= Path.Combine(AppContext.BaseDirectory, "blank-template.pptx");

        if (File.Exists(templatePath))
        {
            File.Copy(templatePath, outputPath, overwrite: true);
            using var pres = PresentationDocument.Open(outputPath, true);
            var now = DateTime.UtcNow;
            pres.PackageProperties.Created = now;
            pres.PackageProperties.Modified = now;
            ClearExistingSlides(pres);
            RenderSlides(deck, pres, basePath);
        }
        else
        {
            using var pres = PresentationDocument.Create(outputPath, PresentationDocumentType.Presentation);
            var now = DateTime.UtcNow;
            pres.PackageProperties.Created = now;
            pres.PackageProperties.Modified = now;
            InitializePresentation(pres, deck.Theme);
            RenderSlides(deck, pres, basePath);
        }
    }

    private static void ClearExistingSlides(PresentationDocument pres)
    {
        var presentationPart = pres.PresentationPart!;
        var slideIdList = presentationPart.Presentation!.SlideIdList;
        if (slideIdList is null)
        {
            presentationPart.Presentation!.SlideIdList = new P.SlideIdList();
            return;
        }

        foreach (var sid in slideIdList.ChildElements.OfType<P.SlideId>().ToList())
        {
            presentationPart.DeletePart((SlidePart)presentationPart.GetPartById(sid.RelationshipId!));
            slideIdList.RemoveChild(sid);
        }
    }

    private static void RenderSlides(Deck deck, PresentationDocument pres, string basePath)
    {
        var presentationPart = pres.PresentationPart!;
        var slideIdList = presentationPart.Presentation!.SlideIdList!;
        uint slideId = 256;
        string font = deck.Theme.Font;

        foreach (var yamlSlide in deck.Slides)
        {
            var slidePart = presentationPart.AddNewPart<SlidePart>($"rId{slideId}");
            slideIdList.Append(new P.SlideId { Id = slideId, RelationshipId = $"rId{slideId}" });

            var layoutPart = presentationPart.SlideMasterParts.First().SlideLayoutParts.First();
            slidePart.AddPart(layoutPart, "rId1");

            var slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.GroupShapeProperties(new D.TransformGroup()))));

            var tree = slide.CommonSlideData!.ShapeTree!;
            uint shapeId = 2;

            foreach (var shape in yamlSlide.Shapes)
            {
                switch (shape.Type.ToLowerInvariant())
                {
                    case "rectangle":
                        AddRectangle(tree, ref shapeId, shape);
                        break;
                    case "textbox":
                        AddTextBox(tree, ref shapeId, shape, font);
                        break;
                    case "table":
                        AddTable(tree, ref shapeId, shape, font);
                        break;
                    case "image":
                        AddImage(slidePart, tree, ref shapeId, shape, basePath);
                        break;
                }
            }

            slidePart.Slide = slide;
            if (yamlSlide.Notes is not null)
                AddSpeakerNotes(slidePart, yamlSlide.Notes);
            slidePart.Slide.Save();
            slideId++;
        }

        presentationPart.Presentation.Save();
    }

    // ---- Shape renderers ----

    private static void AddRectangle(P.ShapeTree tree, ref uint id, Shape shape)
    {
        var sp = new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id++, Name = $"Rect{id}" },
                new P.NonVisualShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(
                new D.Transform2D(
                    new D.Offset { X = shape.X, Y = shape.Y },
                    new D.Extents { Cx = shape.Width, Cy = shape.Height }),
                new D.PresetGeometry(new D.AdjustValueList()) { Preset = D.ShapeTypeValues.Rectangle },
                new D.SolidFill(new D.RgbColorModelHex { Val = Hex(shape.Fill) }),
                new D.Outline(new D.NoFill())));
        tree.Append(sp);
    }

    private static void AddTextBox(P.ShapeTree tree, ref uint id, Shape shape, string font)
    {
        var alignment = ParseAlignment(shape.Alignment);
        int fontSize = shape.FontSize ?? 1800;
        string color = Hex(shape.Color, "333333");

        D.TextBody body;

        if (shape.Bullets is { Count: > 0 })
        {
            body = new D.TextBody(
                new D.BodyProperties { Wrap = D.TextWrappingValues.Square, Anchor = D.TextAnchoringTypeValues.Top },
                new D.ListStyle());

            string bulletChar = shape.BulletChar ?? "\u2022";
            foreach (var item in shape.Bullets)
            {
                var para = new D.Paragraph(
                    new D.ParagraphProperties(
                        new D.SpaceAfter(new D.SpacingPoints { Val = 600 }),
                        new D.BulletFont { Typeface = "Arial" },
                        new D.CharacterBullet { Char = bulletChar }
                    ) { LeftMargin = 342900, Indent = -342900, Alignment = alignment });

                foreach (var run in CreateRuns(item, fontSize, color, font, shape.Bold))
                    para.Append(run);

                body.Append(para);
            }
        }
        else
        {
            var para = new D.Paragraph(
                new D.ParagraphProperties { Alignment = alignment });
            foreach (var run in CreateRuns(shape.Text ?? "", fontSize, color, font, shape.Bold))
                para.Append(run);

            body = new D.TextBody(
                new D.BodyProperties { Wrap = D.TextWrappingValues.Square, Anchor = D.TextAnchoringTypeValues.Top },
                new D.ListStyle(),
                para);
        }

        var sp = new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id++, Name = $"Text{id}" },
                new P.NonVisualShapeDrawingProperties(new D.ShapeLocks { NoGrouping = true }),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(
                new D.Transform2D(
                    new D.Offset { X = shape.X, Y = shape.Y },
                    new D.Extents { Cx = shape.Width, Cy = shape.Height }),
                new D.PresetGeometry(new D.AdjustValueList()) { Preset = D.ShapeTypeValues.Rectangle }),
            new P.TextBody(body.ChildElements.Select(e => (OpenXmlElement)e.CloneNode(true)).ToArray()));

        tree.Append(sp);
    }

    private static void AddTable(P.ShapeTree tree, ref uint id, Shape shape, string font)
    {
        var headers = shape.Headers ?? [];
        var rows = shape.Rows ?? [];
        int colCount = headers.Count;
        if (colCount == 0) return;

        int rowCount = rows.Count + 1;
        long colW = shape.Width / colCount;
        long rowH = shape.Height / rowCount;
        int fontSize = shape.FontSize ?? 1800;

        string headerFill = Hex(shape.HeaderFill, "1F4E79");
        string headerColor = Hex(shape.HeaderColor, "FFFFFF");
        string? altRowFill = shape.AltRowFill is not null ? Hex(shape.AltRowFill) : null;
        string borderColor = Hex(shape.BorderColor, "D6E4F0");

        var table = new D.Table();
        table.Append(new D.TableProperties { FirstRow = true, BandRow = true });

        var grid = new D.TableGrid();
        for (int c = 0; c < colCount; c++)
            grid.Append(new D.GridColumn { Width = colW });
        table.Append(grid);

        // Header row
        var headerRow = new D.TableRow { Height = rowH };
        foreach (var header in headers)
            headerRow.Append(CreateTableCell(header, font, fontSize, bold: true, bgColor: headerFill, textColor: headerColor, borderColor: borderColor));
        table.Append(headerRow);

        // Data rows
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var tableRow = new D.TableRow { Height = rowH };
            string? bg = (altRowFill is not null && r % 2 == 1) ? altRowFill : null;
            for (int c = 0; c < colCount; c++)
                tableRow.Append(CreateTableCell(c < row.Count ? row[c] : "", font, fontSize, bgColor: bg, borderColor: borderColor));
            table.Append(tableRow);
        }

        var graphicFrame = new P.GraphicFrame(
            new P.NonVisualGraphicFrameProperties(
                new P.NonVisualDrawingProperties { Id = id++, Name = $"Table{id}" },
                new P.NonVisualGraphicFrameDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.Transform(
                new D.Offset { X = shape.X, Y = shape.Y },
                new D.Extents { Cx = shape.Width, Cy = rowH * rowCount }),
            new D.Graphic(
                new D.GraphicData(table)
                { Uri = "http://schemas.openxmlformats.org/drawingml/2006/table" }));
        tree.Append(graphicFrame);
    }

    private static D.TableCell CreateTableCell(string text, string font, int fontSize,
        bool bold = false, string? bgColor = null, string? textColor = null, string? borderColor = null)
    {
        string color = Hex(textColor, "333333");
        string border = Hex(borderColor, "D6E4F0");

        var runProps = new D.RunProperties
        {
            Language = "ja-JP",
            FontSize = fontSize,
            Bold = bold,
        };
        runProps.Append(new D.SolidFill(new D.RgbColorModelHex { Val = color }));
        runProps.Append(new D.LatinFont { Typeface = font });
        runProps.Append(new D.EastAsianFont { Typeface = font });

        var cellProps = new D.TableCellProperties(
            new D.LeftBorderLineProperties(new D.SolidFill(new D.RgbColorModelHex { Val = border })) { Width = 12700 },
            new D.RightBorderLineProperties(new D.SolidFill(new D.RgbColorModelHex { Val = border })) { Width = 12700 },
            new D.TopBorderLineProperties(new D.SolidFill(new D.RgbColorModelHex { Val = border })) { Width = 12700 },
            new D.BottomBorderLineProperties(new D.SolidFill(new D.RgbColorModelHex { Val = border })) { Width = 12700 }
        ) { LeftMargin = 91440, RightMargin = 91440, TopMargin = 45720, BottomMargin = 45720 };

        if (bgColor is not null)
            cellProps.Append(new D.SolidFill(new D.RgbColorModelHex { Val = bgColor }));
        else
            cellProps.Append(new D.NoFill());

        return new D.TableCell(
            new D.TextBody(
                new D.BodyProperties(),
                new D.ListStyle(),
                new D.Paragraph(new D.Run(runProps, new D.Text { Text = text }))),
            cellProps);
    }

    private static void AddImage(SlidePart slidePart, P.ShapeTree tree, ref uint id,
        Shape shape, string basePath)
    {
        if (shape.Source is null) return;

        var imagePath = Path.Combine(basePath, shape.Source);
        if (!File.Exists(imagePath)) return;

        var ext = Path.GetExtension(imagePath).ToLowerInvariant();
        var contentType = ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".emf" => "image/x-emf",
            ".wmf" => "image/x-wmf",
            ".tiff" or ".tif" => "image/tiff",
            ".svg" => "image/svg+xml",
            _ => "image/png",
        };

        var imagePart = slidePart.AddImagePart(contentType);
        using (var stream = File.OpenRead(imagePath))
            imagePart.FeedData(stream);

        var relId = slidePart.GetIdOfPart(imagePart);

        // Fit image within the shape bounds while preserving aspect ratio
        long imgX = shape.X, imgY = shape.Y;
        long imgW = shape.Width, imgH = shape.Height;
        var (pixW, pixH) = ReadImageDimensions(imagePath);
        if (pixW > 0 && pixH > 0)
        {
            double imgAspect = (double)pixW / pixH;
            double boxAspect = (double)shape.Width / shape.Height;
            if (imgAspect > boxAspect)
            {
                imgW = shape.Width;
                imgH = (long)(shape.Width / imgAspect);
                imgY = shape.Y + (shape.Height - imgH) / 2;
            }
            else
            {
                imgH = shape.Height;
                imgW = (long)(shape.Height * imgAspect);
                imgX = shape.X + (shape.Width - imgW) / 2;
            }
        }

        var pic = new P.Picture(
            new P.NonVisualPictureProperties(
                new P.NonVisualDrawingProperties { Id = id++, Name = $"Image{id}" },
                new P.NonVisualPictureDrawingProperties(new D.PictureLocks { NoChangeAspect = true }),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.BlipFill(
                new D.Blip { Embed = relId },
                new D.Stretch(new D.FillRectangle())),
            new P.ShapeProperties(
                new D.Transform2D(
                    new D.Offset { X = imgX, Y = imgY },
                    new D.Extents { Cx = imgW, Cy = imgH }),
                new D.PresetGeometry(new D.AdjustValueList()) { Preset = D.ShapeTypeValues.Rectangle }));

        tree.Append(pic);
    }

    /// <summary>
    /// Reads image pixel dimensions from PNG/JPEG/GIF/BMP file headers without loading the full image.
    /// </summary>
    private static (int Width, int Height) ReadImageDimensions(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 32) return (0, 0);
            var header = new byte[32];
            fs.ReadExactly(header);

            // PNG: bytes 0-7 = signature, IHDR chunk at offset 8, width at 16, height at 20 (big-endian)
            if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
            {
                int w = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                int h = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
                return (w, h);
            }

            // JPEG: search for SOF0/SOF2 marker
            if (header[0] == 0xFF && header[1] == 0xD8)
            {
                fs.Position = 2;
                var buf = new byte[8];
                while (fs.Position < fs.Length - 8)
                {
                    if (fs.ReadByte() != 0xFF) continue;
                    int marker = fs.ReadByte();
                    if (marker == 0xC0 || marker == 0xC2) // SOF0 or SOF2
                    {
                        fs.ReadExactly(buf, 0, 5); // length(2) + precision(1) + height(2)
                        int h = (buf[3] << 8) | buf[4];
                        fs.ReadExactly(buf, 0, 2);
                        int w = (buf[0] << 8) | buf[1];
                        return (w, h);
                    }
                    // Skip segment
                    if (fs.Read(buf, 0, 2) < 2) break;
                    int len = (buf[0] << 8) | buf[1];
                    if (len < 2) break;
                    fs.Position += len - 2;
                }
            }

            // GIF: width at 6, height at 8 (little-endian)
            if (header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46)
            {
                int w = header[6] | (header[7] << 8);
                int h = header[8] | (header[9] << 8);
                return (w, h);
            }

            // BMP: width at 18, height at 22 (little-endian, signed for height)
            if (header[0] == 0x42 && header[1] == 0x4D && header.Length >= 26)
            {
                int w = header[18] | (header[19] << 8) | (header[20] << 16) | (header[21] << 24);
                int h = header[22] | (header[23] << 8) | (header[24] << 16) | (header[25] << 24);
                return (w, Math.Abs(h));
            }
        }
        catch { }
        return (0, 0);
    }

    private static void AddSpeakerNotes(SlidePart slidePart, string notes)
    {
        var notesSlidePart = slidePart.AddNewPart<NotesSlidePart>("rId2");
        notesSlidePart.NotesSlide = new P.NotesSlide(
            new P.CommonSlideData(new P.ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.GroupShapeProperties(new D.TransformGroup()),
                new P.Shape(
                    new P.NonVisualShapeProperties(
                        new P.NonVisualDrawingProperties { Id = 2, Name = "Notes" },
                        new P.NonVisualShapeDrawingProperties(),
                        new P.ApplicationNonVisualDrawingProperties(
                            new P.PlaceholderShape { Type = P.PlaceholderValues.Body, Index = 1 })),
                    new P.ShapeProperties(),
                    new P.TextBody(
                        new D.BodyProperties(),
                        new D.ListStyle(),
                        new D.Paragraph(
                            new D.Run(
                                new D.RunProperties { Language = "ja-JP" },
                                new D.Text { Text = notes })))))));
        notesSlidePart.NotesSlide.Save();
    }

    private static D.TextAlignmentTypeValues? ParseAlignment(string? alignment)
    {
        return alignment?.ToLowerInvariant() switch
        {
            "left" => D.TextAlignmentTypeValues.Left,
            "center" => D.TextAlignmentTypeValues.Center,
            "right" => D.TextAlignmentTypeValues.Right,
            _ => null,
        };
    }

    private static string Hex(string? color, string fallback = "FFFFFF")
        => (color ?? fallback).TrimStart('#');

    // Inline markdown: **bold**, *italic*, __bold__, _italic_ (and nesting like **_both_**)
    private static readonly Regex InlinePattern = new(
        @"(\*{1,3}|_{1,3})(.+?)\1",
        RegexOptions.Compiled);

    /// <summary>
    /// Creates D.Run elements from text with inline markdown, supporting nesting.
    /// </summary>
    private static IEnumerable<D.Run> CreateRuns(string text, int fontSize, string color, string font,
        bool shapeBold, bool shapeItalic = false)
    {
        int pos = 0;
        foreach (Match m in InlinePattern.Matches(text))
        {
            if (m.Index > pos)
                yield return MakeRun(text[pos..m.Index], fontSize, color, font, shapeBold, shapeItalic);

            bool isBold = m.Groups[1].Value.Length >= 2;
            bool isItalic = m.Groups[1].Value.Length == 1 || m.Groups[1].Value.Length == 3;

            // Recurse into inner text to handle nesting (e.g. **_both_**)
            foreach (var run in CreateRuns(m.Groups[2].Value, fontSize, color, font,
                shapeBold || isBold, shapeItalic || isItalic))
                yield return run;

            pos = m.Index + m.Length;
        }
        if (pos < text.Length)
            yield return MakeRun(text[pos..], fontSize, color, font, shapeBold, shapeItalic);
    }

    private static D.Run MakeRun(string text, int fontSize, string color, string font, bool bold, bool italic)
    {
        var props = new D.RunProperties { Language = "ja-JP", FontSize = fontSize, Bold = bold, Italic = italic };
        props.Append(new D.SolidFill(new D.RgbColorModelHex { Val = color }));
        props.Append(new D.LatinFont { Typeface = font });
        props.Append(new D.EastAsianFont { Typeface = font });
        return new D.Run(props, new D.Text { Text = text });
    }

    // ---- Presentation initialization (fallback when no template) ----

    private static void InitializePresentation(PresentationDocument pres, SlideTheme theme)
    {
        var presentationPart = pres.AddPresentationPart();
        presentationPart.Presentation = new P.Presentation(
            new P.SlideIdList(),
            new P.SlideSize { Cx = (int)SlideWidth, Cy = (int)SlideHeight, Type = P.SlideSizeValues.Custom },
            new P.NotesSize { Cx = (int)SlideHeight, Cy = (int)SlideWidth }
        );

        string font = theme.Font;
        string colorPrimary = theme.Colors.GetValueOrDefault("primary", "1F4E79");
        string colorAccent = theme.Colors.GetValueOrDefault("accent", "2E75B6");
        string colorLight = theme.Colors.GetValueOrDefault("light", "D6E4F0");

        var slideMasterPart = presentationPart.AddNewPart<SlideMasterPart>("rId1");
        slideMasterPart.SlideMaster = new P.SlideMaster(
            new P.CommonSlideData(new P.ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.GroupShapeProperties(new D.TransformGroup()))),
            new P.ColorMap
            {
                Background1 = D.ColorSchemeIndexValues.Light1,
                Text1 = D.ColorSchemeIndexValues.Dark1,
                Background2 = D.ColorSchemeIndexValues.Light2,
                Text2 = D.ColorSchemeIndexValues.Dark2,
                Accent1 = D.ColorSchemeIndexValues.Accent1,
                Accent2 = D.ColorSchemeIndexValues.Accent2,
                Accent3 = D.ColorSchemeIndexValues.Accent3,
                Accent4 = D.ColorSchemeIndexValues.Accent4,
                Accent5 = D.ColorSchemeIndexValues.Accent5,
                Accent6 = D.ColorSchemeIndexValues.Accent6,
                Hyperlink = D.ColorSchemeIndexValues.Hyperlink,
                FollowedHyperlink = D.ColorSchemeIndexValues.FollowedHyperlink,
            },
            new P.SlideLayoutIdList(
                new P.SlideLayoutId { Id = 2147483649u, RelationshipId = "rId1" }));

        var themePart = slideMasterPart.AddNewPart<ThemePart>("rId2");
        themePart.Theme = new D.Theme(
            new D.ThemeElements(
                new D.ColorScheme(
                    new D.Dark1Color(new D.SystemColor { Val = D.SystemColorValues.WindowText, LastColor = "000000" }),
                    new D.Light1Color(new D.SystemColor { Val = D.SystemColorValues.Window, LastColor = "FFFFFF" }),
                    new D.Dark2Color(new D.RgbColorModelHex { Val = colorPrimary }),
                    new D.Light2Color(new D.RgbColorModelHex { Val = colorLight }),
                    new D.Accent1Color(new D.RgbColorModelHex { Val = colorAccent }),
                    new D.Accent2Color(new D.RgbColorModelHex { Val = "C0504D" }),
                    new D.Accent3Color(new D.RgbColorModelHex { Val = "9BBB59" }),
                    new D.Accent4Color(new D.RgbColorModelHex { Val = "8064A2" }),
                    new D.Accent5Color(new D.RgbColorModelHex { Val = "4BACC6" }),
                    new D.Accent6Color(new D.RgbColorModelHex { Val = "F79646" }),
                    new D.Hyperlink(new D.RgbColorModelHex { Val = "0000FF" }),
                    new D.FollowedHyperlinkColor(new D.RgbColorModelHex { Val = "800080" })
                ) { Name = "PinPoint" },
                new D.FontScheme(
                    new D.MajorFont(new D.LatinFont { Typeface = font }, new D.EastAsianFont { Typeface = font }, new D.ComplexScriptFont { Typeface = font }),
                    new D.MinorFont(new D.LatinFont { Typeface = font }, new D.EastAsianFont { Typeface = font }, new D.ComplexScriptFont { Typeface = font })
                ) { Name = "PinPoint" },
                new D.FormatScheme(
                    new D.FillStyleList(
                        new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor }),
                        new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor }),
                        new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor })),
                    new D.LineStyleList(
                        new D.Outline(new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor })) { Width = 9525 },
                        new D.Outline(new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor })) { Width = 9525 },
                        new D.Outline(new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor })) { Width = 9525 }),
                    new D.EffectStyleList(
                        new D.EffectStyle(new D.EffectList()),
                        new D.EffectStyle(new D.EffectList()),
                        new D.EffectStyle(new D.EffectList())),
                    new D.BackgroundFillStyleList(
                        new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor }),
                        new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor }),
                        new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor }))
                ) { Name = "PinPoint" })
        ) { Name = "PinPoint" };
        themePart.Theme.Save();

        var slideLayoutPart = slideMasterPart.AddNewPart<SlideLayoutPart>("rId1");
        slideLayoutPart.SlideLayout = new P.SlideLayout(
            new P.CommonSlideData(new P.ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.GroupShapeProperties(new D.TransformGroup())))
        ) { Type = P.SlideLayoutValues.Blank };
        slideLayoutPart.SlideLayout.Save();
        slideMasterPart.SlideMaster.Save();

        presentationPart.Presentation.SlideMasterIdList = new P.SlideMasterIdList(
            new P.SlideMasterId { Id = 2147483648u, RelationshipId = "rId1" });
        presentationPart.Presentation.Save();
    }
}
