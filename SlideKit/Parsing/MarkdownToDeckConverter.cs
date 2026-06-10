using System.Text.RegularExpressions;
using SlideKit.Models;

namespace SlideKit.Parsing;

/// <summary>
/// Converts Markdown (with --- slide separators) to a Deck model.
/// Supports <!-- slide: title/section/end --> and <!-- bg: color --> annotations.
/// </summary>
public partial class MarkdownToDeckConverter
{
    // 1 pt = 12700 EMU; font sizes in hundredths of pt
    private const long Emu = 12700;
    private const long SlideW = 960 * Emu;
    private const long SlideH = 540 * Emu;
    private const long MarginLeft = 36 * Emu;
    private const long ContentLeft = 54 * Emu;
    private const long TitleY = 35 * Emu;
    private const long TitleH = 55 * Emu;
    private const int TitleFontSize = 3600;   // 36pt
    private const long AccentY = 94 * Emu;
    private const long AccentH = 5 * Emu;
    private const long ContentY = 130 * Emu;
    private const int ContentFontSize = 2600; // 26pt
    private const int TableFontSize = 2200;   // 22pt

    private string _accentColor = "2E75B6";
    private string _darkBg = "0E2841";
    private string _font = "Meiryo UI";

    public Deck Convert(string markdown)
    {
        var deck = new Deck();
        deck.Theme.Font = _font;
        deck.Theme.Colors["accent"] = _accentColor;

        // Parse front matter
        var content = ParseFrontMatter(markdown, deck);

        _accentColor = deck.Theme.Colors.GetValueOrDefault("accent", "2E75B6");
        _darkBg = deck.Theme.Colors.GetValueOrDefault("primary", "0E2841");
        _font = deck.Theme.Font;

        // Split into slides by ---
        var slideTexts = SplitSlides(content);

        for (int i = 0; i < slideTexts.Count; i++)
        {
            var slideText = slideTexts[i].Trim();
            if (string.IsNullOrEmpty(slideText)) continue;

            var slideType = DetectSlideType(slideText, i, slideTexts.Count);
            var slide = BuildSlide(slideText, slideType);
            deck.Slides.Add(slide);
        }

        return deck;
    }

    public Deck ConvertFile(string path)
    {
        var markdown = File.ReadAllText(path);
        return Convert(markdown);
    }

    private static string ParseFrontMatter(string markdown, Deck deck)
    {
        if (!markdown.TrimStart().StartsWith("---")) return markdown;

        var lines = markdown.Split('\n');
        int start = -1, end = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                if (start < 0) start = i;
                else { end = i; break; }
            }
        }
        if (start < 0 || end < 0) return markdown;

        // Parse simple key: value pairs from front matter
        for (int i = start + 1; i < end; i++)
        {
            var line = lines[i].Trim();
            var colonIdx = line.IndexOf(':');
            if (colonIdx <= 0) continue;
            var key = line[..colonIdx].Trim().ToLowerInvariant();
            var value = line[(colonIdx + 1)..].Trim();
            switch (key)
            {
                case "font": deck.Theme.Font = value; break;
                case "theme":
                    ApplyTheme(value, deck);
                    break;
            }
        }

        return string.Join('\n', lines[(end + 1)..]);
    }

    private static void ApplyTheme(string themeName, Deck deck)
    {
        switch (themeName.ToLowerInvariant())
        {
            case "blue":
                deck.Theme.Colors["primary"] = "0E2841";
                deck.Theme.Colors["accent"] = "2E75B6";
                break;
            case "green":
                deck.Theme.Colors["primary"] = "1B3A2D";
                deck.Theme.Colors["accent"] = "2E8B57";
                break;
            case "red":
                deck.Theme.Colors["primary"] = "3B0A0A";
                deck.Theme.Colors["accent"] = "C0392B";
                break;
            case "dark":
                deck.Theme.Colors["primary"] = "1A1A2E";
                deck.Theme.Colors["accent"] = "E94560";
                break;
        }
    }

    private static List<string> SplitSlides(string content)
    {
        // Split on --- (horizontal rule) and # / ## headings (like Pandoc/reveal.js)
        var slides = new List<string>();
        var lines = content.Split('\n');
        var current = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimEnd();

            // Horizontal rule: --- or more
            if (HrPattern().IsMatch(trimmed))
            {
                if (current.Count > 0)
                {
                    slides.Add(string.Join('\n', current));
                    current.Clear();
                }
                continue;
            }

            // # or ## heading starts a new slide (but not ### or deeper).
            // A lone slide/bg annotation does NOT count as content: "<!-- slide: title -->"
            // before a heading marks that heading's slide, it must not split it off.
            if (SlideHeadingPattern().IsMatch(trimmed) && current.Any(l =>
                    !string.IsNullOrWhiteSpace(l)
                    && !SlideAnnotationPattern().IsMatch(l)
                    && !BgAnnotationPattern().IsMatch(l)))
            {
                slides.Add(string.Join('\n', current));
                current.Clear();
            }

            current.Add(lines[i]);
        }

        if (current.Count > 0)
            slides.Add(string.Join('\n', current));

        return slides;
    }

    private static string DetectSlideType(string slideText, int index, int total)
    {
        // Check for explicit annotation
        var annotation = SlideAnnotationPattern().Match(slideText);
        if (annotation.Success)
            return annotation.Groups[1].Value.Trim().ToLowerInvariant();

        // Auto-detect
        var lines = GetContentLines(slideText);
        var heading = lines.FirstOrDefault(l => HeadingPattern().IsMatch(l));
        var bodyLines = lines.Where(l => !HeadingPattern().IsMatch(l) && !string.IsNullOrWhiteSpace(l)).ToList();

        // First slide with heading + non-heading text → title
        if (index == 0 && heading != null && bodyLines.Count > 0 && bodyLines.Count <= 3
            && !bodyLines.Any(l => l.TrimStart().StartsWith("-") || l.TrimStart().StartsWith("|")))
            return "title";

        // Heading only, no body → section
        if (heading != null && bodyLines.Count == 0)
        {
            // Last slide → end
            if (index == total - 1) return "end";
            return "section";
        }

        return "content";
    }

    private static List<string> GetContentLines(string slideText)
    {
        return slideText.Split('\n')
            .Where(l => !SlideAnnotationPattern().IsMatch(l) && !BgAnnotationPattern().IsMatch(l))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
    }

    private Slide BuildSlide(string slideText, string slideType)
    {
        // Extract background override
        string? bgOverride = null;
        var bgMatch = BgAnnotationPattern().Match(slideText);
        if (bgMatch.Success)
            bgOverride = bgMatch.Groups[1].Value.Trim();

        // Parse content
        var lines = slideText.Split('\n')
            .Where(l => !SlideAnnotationPattern().IsMatch(l) && !BgAnnotationPattern().IsMatch(l))
            .ToList();

        string? title = null;
        string? subtitle = null;
        var bullets = new List<string>();
        var tableHeaders = new List<string>();
        var tableRows = new List<List<string>>();
        var codeLines = new List<string>();
        string? imagePath = null;
        bool inCode = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // Code block
            if (trimmed.StartsWith("```"))
            {
                inCode = !inCode;
                continue;
            }
            if (inCode) { codeLines.Add(line); continue; }

            // Heading
            if (HeadingPattern().IsMatch(trimmed))
            {
                title = HeadingPattern().Replace(trimmed, "").Trim();
                continue;
            }

            // Image: ![alt](path) or ![alt](path){width=...}
            var imgMatch = ImagePattern().Match(trimmed);
            if (imgMatch.Success)
            {
                imagePath = imgMatch.Groups[1].Value;
                continue;
            }

            // Bullet
            if (BulletPattern().IsMatch(trimmed))
            {
                bullets.Add(BulletPattern().Replace(trimmed, "").Trim());
                continue;
            }

            // Table
            if (trimmed.StartsWith('|') && trimmed.EndsWith('|'))
            {
                if (TableSepPattern().IsMatch(trimmed)) continue; // separator row
                var cells = trimmed.Split('|', StringSplitOptions.TrimEntries)
                    .Where(c => !string.IsNullOrEmpty(c)).ToList();
                if (tableHeaders.Count == 0)
                    tableHeaders = cells;
                else
                    tableRows.Add(cells);
                continue;
            }

            // Plain text
            if (!string.IsNullOrWhiteSpace(trimmed))
                subtitle = (subtitle == null ? "" : subtitle + " ") + trimmed;
        }

        return slideType switch
        {
            "title" => BuildTitleSlide(title, subtitle, bgOverride),
            "section" => BuildSectionSlide(title, bgOverride),
            "end" => BuildEndSlide(title, bgOverride),
            _ => BuildContentSlide(title, subtitle, bullets, tableHeaders, tableRows, codeLines, imagePath, bgOverride),
        };
    }

    private Slide BuildTitleSlide(string? title, string? subtitle, string? bg)
    {
        var slide = new Slide();
        var bgColor = bg ?? _darkBg;

        slide.Shapes.Add(new Shape { Type = "rectangle", X = 0, Y = 0, Width = SlideW, Height = SlideH, Fill = bgColor });
        slide.Shapes.Add(new Shape { Type = "rectangle", X = MarginLeft, Y = 220 * Emu, Width = 7 * Emu, Height = 120 * Emu, Fill = _accentColor });
        slide.Shapes.Add(new Shape
        {
            Type = "textbox", X = ContentLeft, Y = 210 * Emu, Width = 870 * Emu, Height = 70 * Emu,
            Text = title ?? "", FontSize = 4800, Bold = true, Color = "FFFFFF"
        });
        if (!string.IsNullOrEmpty(subtitle))
        {
            slide.Shapes.Add(new Shape
            {
                Type = "textbox", X = ContentLeft, Y = 290 * Emu, Width = 870 * Emu, Height = 40 * Emu,
                Text = subtitle, FontSize = 2200, Color = "9AAFBF"
            });
        }
        return slide;
    }

    private Slide BuildSectionSlide(string? title, string? bg)
    {
        var slide = new Slide();
        var bgColor = bg ?? _darkBg;

        slide.Shapes.Add(new Shape { Type = "rectangle", X = 0, Y = 0, Width = SlideW, Height = SlideH, Fill = bgColor });
        slide.Shapes.Add(new Shape
        {
            Type = "textbox", X = 80 * Emu, Y = 220 * Emu, Width = 800 * Emu, Height = 70 * Emu,
            Text = title ?? "", FontSize = 4400, Bold = true, Color = "FFFFFF", Alignment = "center"
        });
        return slide;
    }

    private Slide BuildEndSlide(string? title, string? bg)
    {
        return BuildSectionSlide(title, bg);
    }

    private Slide BuildContentSlide(string? title, string? subtitle, List<string> bullets,
        List<string> tableHeaders, List<List<string>> tableRows,
        List<string> codeLines, string? imagePath, string? bg)
    {
        var slide = new Slide();

        if (bg != null)
            slide.Shapes.Add(new Shape { Type = "rectangle", X = 0, Y = 0, Width = SlideW, Height = SlideH, Fill = bg });

        // Title + accent line
        if (title != null)
        {
            slide.Shapes.Add(new Shape
            {
                Type = "textbox", X = MarginLeft, Y = TitleY, Width = 888 * Emu, Height = TitleH,
                Text = title, FontSize = TitleFontSize, Bold = true
            });
            slide.Shapes.Add(new Shape
            {
                Type = "rectangle", X = MarginLeft, Y = AccentY, Width = 888 * Emu, Height = AccentH,
                Fill = _accentColor
            });
        }

        long cY = title != null ? ContentY : 40 * Emu;
        long cH = SlideH - cY - 30 * Emu;

        // Determine if we have both image and text content
        bool hasImage = imagePath != null;
        bool hasText = bullets.Count > 0 || !string.IsNullOrWhiteSpace(subtitle);
        bool hasTable = tableHeaders.Count > 0;
        bool hasCode = codeLines.Count > 0;

        // When image + text coexist: left text, right image
        long textW = 864 * Emu;
        long imgX = ContentLeft;
        long imgW = 852 * Emu;
        if (hasImage && (hasText || hasTable || hasCode))
        {
            textW = 420 * Emu;
            imgX = ContentLeft + 440 * Emu;
            imgW = 412 * Emu;
        }

        // Image
        if (hasImage)
        {
            slide.Shapes.Add(new Shape
            {
                Type = "image", X = imgX, Y = cY + 10 * Emu,
                Width = imgW, Height = cH - 10 * Emu,
                Source = imagePath
            });
        }

        // Table
        if (hasTable)
        {
            slide.Shapes.Add(new Shape
            {
                Type = "table", X = ContentLeft, Y = cY + 10 * Emu, Width = textW,
                Height = Math.Min(cH, 50 * Emu * (tableRows.Count + 1)),
                FontSize = TableFontSize,
                Headers = tableHeaders, Rows = tableRows,
                HeaderFill = _darkBg, HeaderColor = "FFFFFF", BorderColor = "D6E4F0",
                AltRowFill = "F2F7FB"
            });
        }
        // Code block
        else if (hasCode)
        {
            long codeH = Math.Min(cH, 30 * Emu * codeLines.Count + 30 * Emu);
            slide.Shapes.Add(new Shape
            {
                Type = "rectangle", X = ContentLeft, Y = cY + 10 * Emu,
                Width = textW, Height = codeH, Fill = "F5F5F5"
            });
            slide.Shapes.Add(new Shape
            {
                Type = "textbox", X = ContentLeft + 24 * Emu, Y = cY + 25 * Emu,
                Width = textW - 48 * Emu, Height = codeH - 30 * Emu,
                Text = string.Join("\n", codeLines), FontSize = 2200, Color = "333333"
            });
        }
        // Bullets (with optional subtitle as leading text)
        else if (bullets.Count > 0)
        {
            var allBullets = new List<string>();
            if (!string.IsNullOrWhiteSpace(subtitle))
                allBullets.Add(subtitle);
            allBullets.AddRange(bullets);
            slide.Shapes.Add(new Shape
            {
                Type = "textbox", X = ContentLeft, Y = cY,
                Width = textW, Height = cH,
                Bullets = allBullets, FontSize = ContentFontSize
            });
        }
        // Plain text
        else if (!string.IsNullOrWhiteSpace(subtitle))
        {
            slide.Shapes.Add(new Shape
            {
                Type = "textbox", X = ContentLeft, Y = cY,
                Width = textW, Height = cH,
                Text = subtitle, FontSize = ContentFontSize
            });
        }

        return slide;
    }

    [GeneratedRegex(@"^-{3,}\s*$")]
    private static partial Regex HrPattern();

    [GeneratedRegex(@"^#{1,3}\s+")]
    private static partial Regex SlideHeadingPattern();

    [GeneratedRegex(@"<!--\s*slide:\s*(.+?)\s*-->")]
    private static partial Regex SlideAnnotationPattern();

    [GeneratedRegex(@"<!--\s*bg:\s*(.+?)\s*-->")]
    private static partial Regex BgAnnotationPattern();

    [GeneratedRegex(@"^#{1,6}\s+")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^[-*+]\s+")]
    private static partial Regex BulletPattern();

    [GeneratedRegex(@"^\|[\s\-:]+\|")]
    private static partial Regex TableSepPattern();

    [GeneratedRegex(@"^!\[[^\]]*\]\(([^)\s]+)[^)]*\)")]
    private static partial Regex ImagePattern();
}
