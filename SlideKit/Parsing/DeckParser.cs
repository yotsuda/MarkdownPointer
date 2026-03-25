using System.Text.RegularExpressions;
using SlideKit.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SlideKit.Parsing;

public partial class DeckParser
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    // 1 pt = 12700 EMU (OOXML standard)
    private const long PtToEmu = 12700;

    public Deck Parse(string yaml)
    {
        var deck = Deserializer.Deserialize<Deck>(yaml) ?? new Deck();
        ExpandVariables(deck);
        NormalizeUnits(deck);
        return deck;
    }

    private static void NormalizeUnits(Deck deck)
    {
        foreach (var slide in deck.Slides)
        {
            foreach (var shape in slide.Shapes)
            {
                shape.X      = shape.X      * PtToEmu;
                shape.Y      = shape.Y      * PtToEmu;
                shape.Width  = shape.Width  * PtToEmu;
                shape.Height = shape.Height * PtToEmu;
                if (shape.FontSize.HasValue)
                    shape.FontSize = shape.FontSize.Value * 100;
            }
        }
    }

    public Deck ParseFile(string path)
    {
        var yaml = File.ReadAllText(path);
        return Parse(yaml);
    }

    private static void ExpandVariables(Deck deck)
    {
        if (deck.Variables.Count == 0) return;

        foreach (var slide in deck.Slides)
        {
            slide.Notes = ReplaceVars(slide.Notes, deck.Variables);
            foreach (var shape in slide.Shapes)
            {
                shape.Text = ReplaceVars(shape.Text, deck.Variables);
                if (shape.Bullets is not null)
                {
                    for (int i = 0; i < shape.Bullets.Count; i++)
                        shape.Bullets[i] = ReplaceVars(shape.Bullets[i], deck.Variables)!;
                }
                if (shape.Headers is not null)
                {
                    for (int i = 0; i < shape.Headers.Count; i++)
                        shape.Headers[i] = ReplaceVars(shape.Headers[i], deck.Variables)!;
                }
                if (shape.Rows is not null)
                {
                    foreach (var row in shape.Rows)
                    {
                        for (int i = 0; i < row.Count; i++)
                            row[i] = ReplaceVars(row[i], deck.Variables)!;
                    }
                }
            }
        }
    }

    private static string? ReplaceVars(string? text, Dictionary<string, string> vars)
    {
        if (text is null) return null;
        return VarPattern().Replace(text, m =>
            vars.TryGetValue(m.Groups[1].Value, out var val) ? val : m.Value);
    }

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex VarPattern();
}
