using System.Text;
using SlideKit.Models;

namespace SlideKit.Parsing;

/// <summary>
/// Converts a Deck model to Markdown with slide separators and annotations.
/// </summary>
public class DeckToMarkdownConverter
{
    private const long EmuPerPt = 12700;

    public string Convert(Deck deck)
    {
        var sb = new StringBuilder();

        for (int i = 0; i < deck.Slides.Count; i++)
        {
            if (i > 0)
                sb.AppendLine("\n---\n");

            var slide = deck.Slides[i];
            var slideType = DetectSlideType(slide);

            if (slideType is "title" or "section" or "end")
                sb.AppendLine($"<!-- slide: {slideType} -->");

            RenderSlide(sb, slide);
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    private static string DetectSlideType(Slide slide)
    {
        // Check if slide has a full-size dark rectangle (background)
        bool hasDarkBg = slide.Shapes.Any(s =>
            s.Type == "rectangle" &&
            s.Width > 10_000_000 && s.Height > 5_000_000 &&
            IsDarkColor(s.Fill));

        var textShapes = slide.Shapes.Where(s => s.Type == "textbox").ToList();
        bool hasSubtitle = textShapes.Count >= 2 &&
            textShapes.Any(s => s.FontSize is not null && s.FontSize < 3000);

        if (hasDarkBg && hasSubtitle) return "title";
        if (hasDarkBg && textShapes.Count <= 1) return "section";

        return "content";
    }

    private static bool IsDarkColor(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length < 6) return false;
        try
        {
            int r = System.Convert.ToInt32(hex[..2], 16);
            int g = System.Convert.ToInt32(hex[2..4], 16);
            int b = System.Convert.ToInt32(hex[4..6], 16);
            return (r + g + b) / 3 < 80;
        }
        catch { return false; }
    }

    private static void RenderSlide(StringBuilder sb, Slide slide)
    {
        // Collect content shapes (skip rectangles used as backgrounds/accents)
        var contentShapes = slide.Shapes
            .Where(s => s.Type != "rectangle")
            .OrderBy(s => s.Y)
            .ToList();

        foreach (var shape in contentShapes)
        {
            switch (shape.Type.ToLowerInvariant())
            {
                case "textbox":
                    RenderTextBox(sb, shape);
                    break;
                case "table":
                    RenderTable(sb, shape);
                    break;
                case "image":
                    RenderImage(sb, shape);
                    break;
            }
        }
    }

    private static void RenderTextBox(StringBuilder sb, Shape shape)
    {
        bool isHeading = shape.Bold && (shape.FontSize is null || shape.FontSize >= 3000);

        if (shape.Bullets is { Count: > 0 })
        {
            foreach (var bullet in shape.Bullets)
                sb.AppendLine($"- {bullet}");
            sb.AppendLine();
        }
        else if (!string.IsNullOrWhiteSpace(shape.Text))
        {
            if (isHeading)
                sb.AppendLine($"## {shape.Text}");
            else
                sb.AppendLine(shape.Text);
            sb.AppendLine();
        }
    }

    private static void RenderTable(StringBuilder sb, Shape shape)
    {
        if (shape.Headers is not { Count: > 0 }) return;

        // Header row
        sb.AppendLine("| " + string.Join(" | ", shape.Headers) + " |");
        sb.AppendLine("| " + string.Join(" | ", shape.Headers.Select(_ => "---")) + " |");

        // Data rows
        if (shape.Rows is not null)
        {
            foreach (var row in shape.Rows)
            {
                // Pad row to match header count
                var cells = new List<string>(row);
                while (cells.Count < shape.Headers.Count) cells.Add("");
                sb.AppendLine("| " + string.Join(" | ", cells) + " |");
            }
        }
        sb.AppendLine();
    }

    private static void RenderImage(StringBuilder sb, Shape shape)
    {
        if (shape.Source is not null)
            sb.AppendLine($"![image]({shape.Source})");
        sb.AppendLine();
    }
}
