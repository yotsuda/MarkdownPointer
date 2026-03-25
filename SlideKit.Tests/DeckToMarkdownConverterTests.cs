using SlideKit.Models;
using SlideKit.Parsing;

namespace SlideKit.Tests;

public class DeckToMarkdownConverterTests
{
    private readonly DeckToMarkdownConverter _converter = new();

    private const long Emu = 12700;

    private static Deck CreateDeck(params Slide[] slides)
    {
        return new Deck { Slides = slides.ToList() };
    }

    [Fact]
    public void TitleSlide_HasAnnotation()
    {
        var slide = new Slide
        {
            Shapes =
            [
                new Shape { Type = "rectangle", X = 0, Y = 0, Width = 960 * Emu, Height = 540 * Emu, Fill = "0E2841" },
                new Shape { Type = "textbox", Text = "Title", FontSize = 4800, Bold = true, Color = "FFFFFF" },
                new Shape { Type = "textbox", Text = "Subtitle", FontSize = 2200, Color = "9AAFBF" },
            ]
        };

        var md = _converter.Convert(CreateDeck(slide));
        Assert.Contains("<!-- slide: title -->", md);
        Assert.Contains("## Title", md);
        Assert.Contains("Subtitle", md);
    }

    [Fact]
    public void SectionSlide_HasAnnotation()
    {
        var slide = new Slide
        {
            Shapes =
            [
                new Shape { Type = "rectangle", X = 0, Y = 0, Width = 960 * Emu, Height = 540 * Emu, Fill = "0E2841" },
                new Shape { Type = "textbox", Text = "Section", FontSize = 4400, Bold = true, Color = "FFFFFF" },
            ]
        };

        var md = _converter.Convert(CreateDeck(slide));
        Assert.Contains("<!-- slide: section -->", md);
        Assert.Contains("## Section", md);
    }

    [Fact]
    public void ContentSlide_NoAnnotation()
    {
        var slide = new Slide
        {
            Shapes =
            [
                new Shape { Type = "textbox", Text = "Heading", FontSize = 3600, Bold = true },
                new Shape { Type = "textbox", Bullets = ["A", "B", "C"], FontSize = 2600 },
            ]
        };

        var md = _converter.Convert(CreateDeck(slide));
        Assert.DoesNotContain("<!-- slide:", md);
        Assert.Contains("## Heading", md);
        Assert.Contains("- A", md);
        Assert.Contains("- B", md);
        Assert.Contains("- C", md);
    }

    [Fact]
    public void Table_RenderedAsMarkdown()
    {
        var slide = new Slide
        {
            Shapes =
            [
                new Shape { Type = "textbox", Text = "Data", FontSize = 3600, Bold = true },
                new Shape
                {
                    Type = "table",
                    Headers = ["Col1", "Col2"],
                    Rows = [["A", "B"], ["C", "D"]],
                },
            ]
        };

        var md = _converter.Convert(CreateDeck(slide));
        Assert.Contains("| Col1 | Col2 |", md);
        Assert.Contains("| --- | --- |", md);
        Assert.Contains("| A | B |", md);
        Assert.Contains("| C | D |", md);
    }

    [Fact]
    public void Image_RenderedAsMarkdown()
    {
        var slide = new Slide
        {
            Shapes =
            [
                new Shape { Type = "textbox", Text = "Photos", FontSize = 3600, Bold = true },
                new Shape { Type = "image", Source = "assets/abc123.png" },
            ]
        };

        var md = _converter.Convert(CreateDeck(slide));
        Assert.Contains("![image](assets/abc123.png)", md);
    }

    [Fact]
    public void MultipleSlides_SeparatedByHr()
    {
        var deck = CreateDeck(
            new Slide { Shapes = [new Shape { Type = "textbox", Text = "Slide1", FontSize = 3600, Bold = true }] },
            new Slide { Shapes = [new Shape { Type = "textbox", Text = "Slide2", FontSize = 3600, Bold = true }] },
            new Slide { Shapes = [new Shape { Type = "textbox", Text = "Slide3", FontSize = 3600, Bold = true }] }
        );

        var md = _converter.Convert(deck);
        var hrCount = md.Split("---").Length - 1;
        Assert.Equal(2, hrCount);
    }

    [Fact]
    public void Rectangles_SkippedInOutput()
    {
        var slide = new Slide
        {
            Shapes =
            [
                new Shape { Type = "rectangle", Fill = "FFFFFF" },
                new Shape { Type = "rectangle", Fill = "2E75B6" },
                new Shape { Type = "textbox", Text = "Content", FontSize = 3600, Bold = true },
            ]
        };

        var md = _converter.Convert(CreateDeck(slide));
        Assert.DoesNotContain("rectangle", md.ToLowerInvariant());
        Assert.Contains("## Content", md);
    }

    [Fact]
    public void HeadingDetection_BoldAndLargeFontSize()
    {
        var slide = new Slide
        {
            Shapes =
            [
                new Shape { Type = "textbox", Text = "IsHeading", FontSize = 3600, Bold = true },
                new Shape { Type = "textbox", Text = "NotHeading", FontSize = 2000, Bold = false },
            ]
        };

        var md = _converter.Convert(CreateDeck(slide));
        Assert.Contains("## IsHeading", md);
        Assert.DoesNotContain("## NotHeading", md);
        Assert.Contains("NotHeading", md);
    }

    [Fact]
    public void DarkBackground_DetectedCorrectly()
    {
        // Dark color (low RGB values)
        var darkSlide = new Slide
        {
            Shapes =
            [
                new Shape { Type = "rectangle", X = 0, Y = 0, Width = 960 * Emu, Height = 540 * Emu, Fill = "0E2841" },
                new Shape { Type = "textbox", Text = "Dark", FontSize = 4400, Bold = true },
            ]
        };

        // Light color (high RGB values)
        var lightSlide = new Slide
        {
            Shapes =
            [
                new Shape { Type = "rectangle", X = 0, Y = 0, Width = 960 * Emu, Height = 540 * Emu, Fill = "FFFFFF" },
                new Shape { Type = "textbox", Text = "Light", FontSize = 4400, Bold = true },
            ]
        };

        var darkMd = _converter.Convert(CreateDeck(darkSlide));
        var lightMd = _converter.Convert(CreateDeck(lightSlide));

        Assert.Contains("<!-- slide:", darkMd);    // Dark bg → section annotation
        Assert.DoesNotContain("<!-- slide:", lightMd); // Light bg → no annotation
    }
}
