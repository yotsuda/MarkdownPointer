using YamlDotNet.Serialization;

namespace SlideKit.Models;

public class Deck
{
    public Dictionary<string, string> Variables { get; set; } = [];
    public SlideTheme Theme { get; set; } = new();
    public List<Slide> Slides { get; set; } = [];
}

public class SlideTheme
{
    public Dictionary<string, string> Colors { get; set; } = [];
    public string Font { get; set; } = "Meiryo UI";
}

public class Slide
{
    public string? Notes { get; set; }
    public List<Shape> Shapes { get; set; } = [];
}

public class Shape
{
    // rectangle, textbox, table, image
    public string Type { get; set; } = "rectangle";

    // Position & size (pt; slide = 960 x 540 pt)
    public long X { get; set; }
    public long Y { get; set; }
    public long Width { get; set; }
    public long Height { get; set; }

    // Rectangle
    public string? Fill { get; set; }

    // TextBox
    public string? Text { get; set; }

    [YamlMember(Alias = "font_size")]
    public int? FontSize { get; set; }          // pt (e.g., 24)

    public bool Bold { get; set; }
    public string? Color { get; set; }          // RGB hex
    public string? Alignment { get; set; }      // left, center, right

    // Bullet list
    public List<string>? Bullets { get; set; }

    [YamlMember(Alias = "bullet_char")]
    public string? BulletChar { get; set; }

    // Table
    public List<string>? Headers { get; set; }
    public List<List<string>>? Rows { get; set; }

    [YamlMember(Alias = "header_fill")]
    public string? HeaderFill { get; set; }

    [YamlMember(Alias = "header_color")]
    public string? HeaderColor { get; set; }

    [YamlMember(Alias = "alt_row_fill")]
    public string? AltRowFill { get; set; }

    [YamlMember(Alias = "border_color")]
    public string? BorderColor { get; set; }

    // Image
    public string? Source { get; set; }             // relative path to image file (e.g., assets/a3f8c2.png)
}
