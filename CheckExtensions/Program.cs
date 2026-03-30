using System;
using System.IO;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using MarkdownPointer;

// ── Configuration ──
var fixtureDir = Path.Combine(AppContext.BaseDirectory, "TestFixtures");
if (!Directory.Exists(fixtureDir))
{
    // When running via dotnet run, fixtures are in the project dir
    fixtureDir = Path.Combine(Directory.GetCurrentDirectory(), "TestFixtures");
}

var files = args.Length > 0
    ? args.Select(a => Path.GetFullPath(a)).ToArray()
    : Directory.GetFiles(fixtureDir, "*.md");

int totalPass = 0, totalFail = 0;

foreach (var file in files)
{
    Console.WriteLine($"\n=== {Path.GetFileName(file)} ===");
    var mdLines = File.ReadAllLines(file);

    // Build pipeline matching the App's configuration
    var pipeline = new MarkdownPipelineBuilder()
        .UseAbbreviations()
        .UseAutoIdentifiers(Markdig.Extensions.AutoIdentifiers.AutoIdentifierOptions.GitHub)
        .UseCitations()
        .UseCustomContainers()
        .UseDefinitionLists()
        .UseEmphasisExtras()
        .UseFigures()
        .UseFooters()
        .UseFootnotes()
        .UseGridTables()
        .UseMathematics()
        .UseMediaLinks()
        .UsePipeTables()
        .UseListExtras()
        .UseTaskLists()
        .UseAutoLinks()
        .UseGenericAttributes()
        .Build();

    var markdown = File.ReadAllText(file);

    // Render with LineTrackingHtmlRenderer
    var writer = new StringWriter();
    var renderer = new LineTrackingHtmlRenderer(writer);
    pipeline.Setup(renderer);
    renderer.ReplaceExtensionRenderers();

    var document = Markdig.Markdown.Parse(markdown, pipeline);
    renderer.Render(document);
    writer.Flush();
    var html = writer.ToString();

    // Extract data-line attributes, but exclude code-line spans (verified separately)
    var dataLinePattern = new Regex(
        @"(?<!class=""code-line"" )data-line=""(\d+)""[^>]*>([^<]*)",
        RegexOptions.Singleline);

    var matches = dataLinePattern.Matches(html);
    int pass = 0, fail = 0;

    foreach (Match m in matches)
    {
        var reportedLine = int.Parse(m.Groups[1].Value);
        var htmlContent = m.Groups[2].Value.Trim();

        // Skip empty content (container elements like <ul>, <ol>, <blockquote>, <table>)
        if (string.IsNullOrWhiteSpace(htmlContent))
            continue;

        // Decode HTML entities for comparison
        var decoded = System.Web.HttpUtility.HtmlDecode(htmlContent);

        // For multiline content (e.g. blockquote paragraphs), use first line only
        var firstLine = decoded.Contains('\n') ? decoded.Split('\n')[0].Trim() : decoded;

        // Find which source line(s) contain this text
        var foundLines = new List<int>();
        for (int i = 0; i < mdLines.Length; i++)
        {
            var srcLine = mdLines[i].Trim();
            // Strip markdown syntax for comparison
            var stripped = StripMarkdownSyntax(srcLine);
            if (!string.IsNullOrEmpty(firstLine) && !string.IsNullOrEmpty(stripped)
                && (stripped.Contains(firstLine, StringComparison.Ordinal)
                    || firstLine.Contains(stripped, StringComparison.Ordinal)))
            {
                foundLines.Add(i + 1); // 1-indexed
            }
        }

        if (foundLines.Contains(reportedLine))
        {
            pass++;
        }
        else
        {
            fail++;
            var expected = foundLines.Count > 0
                ? string.Join(",", foundLines)
                : "?";
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  FAIL: data-line={reportedLine} content=\"{Truncate(decoded, 40)}\" expected={expected}");
            Console.ResetColor();
        }
    }

    // Strict check: code-line spans must exactly match source lines
    VerifyCodeLineSpans(html, mdLines, ref pass, ref fail);

    // Also verify block-level elements by walking the Markdig AST
    VerifyAst(document, mdLines, ref pass, ref fail);

    Console.ForegroundColor = fail > 0 ? ConsoleColor.Yellow : ConsoleColor.Green;
    Console.WriteLine($"  {pass} pass, {fail} fail");
    Console.ResetColor();

    totalPass += pass;
    totalFail += fail;
}

// ── Summary ──
Console.WriteLine();
Console.ForegroundColor = totalFail > 0 ? ConsoleColor.Red : ConsoleColor.Green;
Console.WriteLine($"Total: {totalPass} pass, {totalFail} fail");
Console.ResetColor();

return totalFail > 0 ? 1 : 0;

// ── Helpers ──

static string StripMarkdownSyntax(string line)
{
    // Remove heading markers
    line = Regex.Replace(line, @"^#{1,6}\s+", "");
    // Remove list markers
    line = Regex.Replace(line, @"^\s*[-*+]\s+", "");
    line = Regex.Replace(line, @"^\s*\d+\.\s+", "");
    // Remove blockquote markers
    line = Regex.Replace(line, @"^>\s*", "");
    // Remove fenced code markers
    line = Regex.Replace(line, @"^```.*$", "");
    // Remove table pipes (but keep cell content)
    if (line.Contains('|'))
        line = string.Join(" ", line.Split('|').Select(s => s.Trim()).Where(s => s.Length > 0 && !Regex.IsMatch(s, @"^[-:]+$")));
    return line.Trim();
}

static void VerifyCodeLineSpans(string html, string[] mdLines, ref int pass, ref int fail)
{
    // Match <span class="code-line" data-line="N">content</span>
    var pattern = new Regex(@"<span class=""code-line"" data-line=""(\d+)"">([^<]*)</span>");
    foreach (Match m in pattern.Matches(html))
    {
        var reported = int.Parse(m.Groups[1].Value);
        var htmlContent = System.Web.HttpUtility.HtmlDecode(m.Groups[2].Value);

        if (reported < 1 || reported > mdLines.Length)
        {
            fail++;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  FAIL(code-line): data-line={reported} OUT OF RANGE");
            Console.ResetColor();
            continue;
        }

        // The source line (without leading indentation for indented code blocks)
        var srcLine = mdLines[reported - 1];
        // Strip blockquote markers and indented code block prefix
        var normalized = Regex.Replace(srcLine, @"^(\s*>\s*)+", "");
        if (normalized.Length >= 4 && normalized.StartsWith("    "))
            normalized = normalized[4..];

        if (normalized == htmlContent || normalized.Trim() == htmlContent.Trim())
        {
            pass++;
        }
        else
        {
            fail++;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  FAIL(code-line): data-line={reported} content=\"{Truncate(htmlContent, 30)}\" src[{reported}]=\"{Truncate(srcLine, 30)}\"");
            Console.ResetColor();
        }
    }
}

static string Truncate(string s, int max)
    => s.Length <= max ? s : s[..max] + "...";

static void VerifyAst(MarkdownDocument doc, string[] mdLines, ref int pass, ref int fail)
{
    // Walk AST and verify that obj.Line + 1 points to a line containing relevant content
    foreach (var block in doc.Descendants<Block>())
    {
        // Only check leaf blocks that produce data-line
        int reported = block.Line + 1; // our renderers use obj.Line + 1

        if (reported < 1 || reported > mdLines.Length)
        {
            // Out of range
            if (block is LeafBlock)
            {
                fail++;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  FAIL(AST): {block.GetType().Name} Line={block.Line} → reported={reported} OUT OF RANGE");
                Console.ResetColor();
            }
            continue;
        }

        // Verify that the AST line number makes sense
        switch (block)
        {
            case HeadingBlock h:
                var headingLine = mdLines[block.Line];
                if (!headingLine.TrimStart().StartsWith('#'))
                {
                    fail++;
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  FAIL(AST): HeadingBlock.Line={block.Line} → \"{Truncate(headingLine, 30)}\" (no # prefix)");
                    Console.ResetColor();
                }
                else pass++;
                break;

            case FencedCodeBlock fc:
                var fenceLine = mdLines[block.Line];
                // Strip blockquote markers for nested code blocks
                var fenceNorm = Regex.Replace(fenceLine, @"^(\s*>\s*)+", "").TrimStart();
                if (!fenceNorm.StartsWith("```"))
                {
                    fail++;
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  FAIL(AST): FencedCodeBlock.Line={block.Line} → \"{Truncate(fenceLine, 30)}\" (no ``` prefix)");
                    Console.ResetColor();
                }
                else pass++;
                break;
        }
    }
}
