using SlideKit.Parsing;

namespace SlideKit.Tests;

public class MarkdownToDeckConverterTests
{
    private readonly MarkdownToDeckConverter _converter = new();

    private const long Emu = 12700;

    [Fact]
    public void SplitSlides_SeparatedByHr()
    {
        var md = "## Slide 1\n\n---\n\n## Slide 2\n\n---\n\n## Slide 3";
        var deck = _converter.Convert(md);
        Assert.Equal(3, deck.Slides.Count);
    }

    [Fact]
    public void TitleSlide_AutoDetected_FirstSlideWithSubtitle()
    {
        var md = "## My Title\n\nSubtitle text\n\n---\n\n## Content\n\n- Item";
        var deck = _converter.Convert(md);

        // First slide should have dark background rectangle
        var bg = deck.Slides[0].Shapes[0];
        Assert.Equal("rectangle", bg.Type);
        Assert.Equal(960 * Emu, bg.Width);
        Assert.Equal(540 * Emu, bg.Height);
    }

    [Fact]
    public void TitleSlide_ExplicitAnnotation()
    {
        var md = "<!-- slide: title -->\n## Explicit Title\n\nSub";
        var deck = _converter.Convert(md);

        var shapes = deck.Slides[0].Shapes;
        // Should have: bg rect, accent line, title textbox, subtitle textbox
        Assert.True(shapes.Count >= 3);
        var titleShape = shapes.First(s => s.Type == "textbox" && s.Text == "Explicit Title");
        Assert.Equal(4800, titleShape.FontSize); // 48pt in hundredths
        Assert.True(titleShape.Bold);
    }

    [Fact]
    public void ContentSlide_Bullets()
    {
        var md = "## Heading\n\n- Bullet 1\n- Bullet 2\n- Bullet 3";
        var deck = _converter.Convert(md);

        var bulletShape = deck.Slides[0].Shapes.FirstOrDefault(s => s.Bullets != null);
        Assert.NotNull(bulletShape);
        Assert.Equal(3, bulletShape.Bullets!.Count);
        Assert.Equal("Bullet 1", bulletShape.Bullets[0]);
    }

    [Fact]
    public void ContentSlide_Table()
    {
        var md = "## Data\n\n| A | B |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |";
        var deck = _converter.Convert(md);

        var tableShape = deck.Slides[0].Shapes.FirstOrDefault(s => s.Type == "table");
        Assert.NotNull(tableShape);
        Assert.Equal(2, tableShape.Headers!.Count);
        Assert.Equal("A", tableShape.Headers[0]);
        Assert.Equal(2, tableShape.Rows!.Count);
        Assert.Equal("1", tableShape.Rows[0][0]);
    }

    [Fact]
    public void ContentSlide_CodeBlock()
    {
        var md = "## Intro\n\n- Item\n\n---\n\n## Code\n\n```python\nprint('hello')\n```";
        var deck = _converter.Convert(md);
        Assert.Equal(2, deck.Slides.Count);

        // Second slide should have a code background rectangle and textbox
        var codeText = deck.Slides[1].Shapes.FirstOrDefault(s =>
            s.Type == "textbox" && s.Text != null && s.Text.Contains("print"));
        Assert.NotNull(codeText);
        Assert.Contains("print('hello')", codeText.Text);

        var codeBg = deck.Slides[1].Shapes.FirstOrDefault(s =>
            s.Type == "rectangle" && s.Fill == "F5F5F5");
        Assert.NotNull(codeBg);
    }

    [Fact]
    public void ContentSlide_PlainText()
    {
        var md = "## Title\n\nSome plain text paragraph";
        var deck = _converter.Convert(md);

        var textShape = deck.Slides[0].Shapes.FirstOrDefault(s =>
            s.Type == "textbox" && s.Text == "Some plain text paragraph");
        Assert.NotNull(textShape);
    }

    [Fact]
    public void SectionSlide_HeadingOnly()
    {
        var md = "## First\n\n- Item\n\n---\n\n## Section Break\n\n---\n\n## Next\n\n- Item";
        var deck = _converter.Convert(md);

        // Slide 2 (index 1) should be detected as section
        var sectionSlide = deck.Slides[1];
        var bg = sectionSlide.Shapes[0];
        Assert.Equal("rectangle", bg.Type);
        Assert.Equal(960 * Emu, bg.Width); // full-size dark bg
    }

    [Fact]
    public void EndSlide_LastSlideHeadingOnly()
    {
        var md = "## Content\n\n- Item\n\n---\n\n## Thank You";
        var deck = _converter.Convert(md);

        // Last slide should be end (same layout as section)
        var endSlide = deck.Slides[1];
        var textShape = endSlide.Shapes.First(s => s.Type == "textbox");
        Assert.Equal("center", textShape.Alignment);
    }

    [Fact]
    public void FrontMatter_ThemeApplied()
    {
        var md = "---\ntheme: green\n---\n\n## Title\n\nSubtitle";
        var deck = _converter.Convert(md);

        Assert.Equal("2E8B57", deck.Theme.Colors["accent"]);
        Assert.Equal("1B3A2D", deck.Theme.Colors["primary"]);
    }

    [Fact]
    public void FrontMatter_FontApplied()
    {
        var md = "---\nfont: Yu Gothic UI\n---\n\n## Title\n\nSubtitle";
        var deck = _converter.Convert(md);

        Assert.Equal("Yu Gothic UI", deck.Theme.Font);
    }

    [Fact]
    public void BackgroundOverride()
    {
        var md = "<!-- bg: #FF0000 -->\n## Red Slide\n\nContent";
        var deck = _converter.Convert(md);

        var bgRect = deck.Slides[0].Shapes.FirstOrDefault(s =>
            s.Type == "rectangle" && s.Fill == "#FF0000");
        Assert.NotNull(bgRect);
    }

    [Fact]
    public void ExplicitSectionAnnotation()
    {
        var md = "## Content\n\n- Item\n\n---\n\n<!-- slide: section -->\n## Break\n\nSome text that should be ignored for section";
        var deck = _converter.Convert(md);

        var sectionSlide = deck.Slides[1];
        var bg = sectionSlide.Shapes[0];
        Assert.Equal("rectangle", bg.Type);
        // Section should have dark bg regardless of body content
    }

    [Fact]
    public void EmptySlides_Skipped()
    {
        var md = "## Slide 1\n\n---\n\n\n\n---\n\n## Slide 2";
        var deck = _converter.Convert(md);

        Assert.Equal(2, deck.Slides.Count);
    }

    [Fact]
    public void CoordinatesAreInEmu()
    {
        var md = "## Test\n\n- Item";
        var deck = _converter.Convert(md);

        var titleShape = deck.Slides[0].Shapes.First(s => s.Type == "textbox");
        // X should be MarginLeft (36pt) in EMU
        Assert.Equal(36 * Emu, titleShape.X);
    }

    [Fact]
    public void FontSizesInHundredthsOfPt()
    {
        var md = "## Test\n\n- Item";
        var deck = _converter.Convert(md);

        var titleShape = deck.Slides[0].Shapes.First(s => s.Type == "textbox" && s.Bold);
        Assert.Equal(3600, titleShape.FontSize); // 36pt = 3600 hundredths

        var bulletShape = deck.Slides[0].Shapes.First(s => s.Bullets != null);
        Assert.Equal(2600, bulletShape.FontSize); // 26pt = 2600 hundredths
    }
}
