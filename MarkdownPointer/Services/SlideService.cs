using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using MarkdownPointer.Resources;

namespace MarkdownPointer.Services
{
    /// <summary>
    /// Service for rendering slide previews (Pandoc + reveal.js) and exporting to PPTX.
    /// </summary>
    public static class SlideService
    {
        // Pre-compiled regex patterns
        private static readonly Regex SectionTagPattern = new(@"<section\b([^>]*?)>", RegexOptions.Compiled);
        private static readonly Regex SlideBreakPattern = new(@"^(-{3,}|\*{3,}|_{3,})$", RegexOptions.Compiled);
        private static readonly Regex HeadingSlidePattern = new(@"^#{1,2}\s", RegexOptions.Compiled);
        private static readonly Regex HeadingPattern = new(@"^(#{1,6})\s", RegexOptions.Compiled);
        private static readonly Regex UnorderedListPattern = new(@"^[-*+]\s", RegexOptions.Compiled);
        private static readonly Regex OrderedListPattern = new(@"^\d+\.\s", RegexOptions.Compiled);
        private static readonly Regex TableSeparatorPattern = new(@"^\|[\s\-:]+\|", RegexOptions.Compiled);
        private static readonly Regex CspMetaPattern = new(
            @"<meta\s+http-equiv=['""]Content-Security-Policy['""][^>]*/?>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Renders markdown as reveal.js slides for preview in WebView2.
        /// </summary>
        public static async Task<string?> RenderSlidesAsync(string markdownPath, string markdown, string theme = "black")
        {
            var html = await PandocToRevealJsAsync(markdownPath, theme);
            if (html == null) return null;

            // Pandoc uses data-src for lazy loading in reveal.js; convert to src for WebView2
            html = html.Replace("data-src=\"", "src=\"");
            // Remove figcaption elements (Pandoc wraps images in <figure> with alt text as caption)
            html = Regex.Replace(html, @"<figcaption[^>]*>.*?</figcaption>", "", RegexOptions.Singleline);

            // Inline local images as base64 (NavigateToString blocks file:// access)
            var baseDir = Path.GetDirectoryName(markdownPath)!;
            html = HtmlGenerator.InlineLocalImages(html, baseDir);

            html = AddLineTracking(html, markdown);
            html = InjectPointingScripts(html, theme);
            return html;
        }

        /// <summary>
        /// Exports markdown to .pptx using SlideKit.
        /// </summary>
        public static async Task<(bool Success, string? Error)> ExportPptxAsync(
            string markdownPath, string outputPath, string? templatePath = null, string? resourceDir = null)
        {
            try
            {
                return await Task.Run(() =>
                {
                    var converter = new SlideKit.Parsing.MarkdownToDeckConverter();
                    var deck = converter.ConvertFile(markdownPath);
                    var renderer = new SlideKit.Rendering.PptxRenderer();
                    renderer.Render(deck, outputPath, templatePath);
                    return ((bool Success, string? Error))(true, null);
                });
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// Converts markdown to reveal.js HTML using Pandoc.
        /// </summary>
        private static async Task<string?> PandocToRevealJsAsync(string markdownPath, string theme = "black")
        {
            var outputPath = Path.Combine(Path.GetTempPath(), $"slides_{Guid.NewGuid():N}.html");
            try
            {
                var result = await Task.Run(async () =>
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "pandoc",
                        Arguments = $"-f markdown -t revealjs -s " +
                                    $"--variable revealjs-url=https://cdn.jsdelivr.net/npm/reveal.js@5.2.1 " +
                                    $"--variable theme={theme} " +
                                    $"-o \"{outputPath}\" \"{markdownPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    using var process = Process.Start(psi);
                    if (process == null) return (string?)null;

                    await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();

                    if (process.ExitCode != 0 || !File.Exists(outputPath))
                        return null;

                    return await File.ReadAllTextAsync(outputPath, Encoding.UTF8);
                });

                return result;
            }
            finally
            {
                try { File.Delete(outputPath); } catch { }
            }
        }

        /// <summary>
        /// Adds data-line attributes to section elements for pointing mode.
        /// </summary>
        private static string AddLineTracking(string html, string markdown)
        {
            var lines = markdown.Split('\n');
            var slideStartLines = FindSlideStartLines(lines);

            int slideIndex = 0;
            html = SectionTagPattern.Replace(html, match =>
            {
                var attrs = match.Groups[1].Value;
                // Skip wrapper sections (no attributes — used by Pandoc to group vertical slides)
                if (string.IsNullOrWhiteSpace(attrs)) return match.Value;
                // Skip auto-generated title slide from frontmatter
                if (attrs.Contains("id=\"title-slide\"")) return match.Value;
                // Skip sections that already have data-line
                if (attrs.Contains("data-line")) return match.Value;

                if (slideIndex < slideStartLines.Count)
                {
                    var line = slideStartLines[slideIndex++];
                    return $"<section data-line=\"{line}\"{attrs}>";
                }
                return match.Value;
            });

            // Add line tracking to block elements within sections
            html = AddElementLineTracking(html, lines, slideStartLines);

            return html;
        }

        /// <summary>
        /// Finds the 1-based line numbers where each slide starts.
        /// Pandoc splits slides on headings and horizontal rules (---).
        /// </summary>
        private static List<int> FindSlideStartLines(string[] lines)
        {
            var result = new List<int>();

            // Skip YAML frontmatter
            int i = 0;
            if (lines.Length > 0 && lines[0].TrimEnd() == "---")
            {
                for (i = 1; i < lines.Length; i++)
                {
                    if (lines[i].TrimEnd() == "---")
                    {
                        i++;
                        break;
                    }
                }
            }

            // Find first non-empty content line
            while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i])) i++;
            if (i < lines.Length) result.Add(i + 1); // 1-based

            bool lastWasSlideStart = true;
            for (; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimEnd();

                // Horizontal rule (slide separator): ---, ***, ___
                if (SlideBreakPattern.IsMatch(trimmed))
                {
                    // Next non-empty line starts a new slide
                    int j = i + 1;
                    while (j < lines.Length && string.IsNullOrWhiteSpace(lines[j])) j++;
                    if (j < lines.Length)
                    {
                        result.Add(j + 1);
                        lastWasSlideStart = true;
                    }
                    i = j - 1;
                    continue;
                }

                // Headings start new slides (Pandoc default behavior)
                if (!lastWasSlideStart && HeadingSlidePattern.IsMatch(trimmed))
                {
                    result.Add(i + 1);
                    lastWasSlideStart = true;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    lastWasSlideStart = false;
                }
            }

            return result;
        }

        // Cached tag patterns for InjectBlockLineAttributes
        private static readonly Dictionary<string, Regex> TagPatternCache = new();
        private static readonly Regex SectionOpenPattern = new(@"<section\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static Regex GetTagPattern(string tag)
        {
            if (!TagPatternCache.TryGetValue(tag, out var pattern))
            {
                pattern = new Regex($@"<{Regex.Escape(tag)}\b([^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
                TagPatternCache[tag] = pattern;
            }
            return pattern;
        }

        /// <summary>
        /// Adds data-line to block elements within each section.
        /// </summary>
        private static string AddElementLineTracking(string html, string[] lines, List<int> slideStartLines)
        {
            // Pre-compute section positions once for all slides
            // Skip wrapper sections (just "<section>") and title slides
            var sectionMatches = SectionOpenPattern.Matches(html)
                .Cast<Match>()
                .Where(m => m.Value != "<section>" && !m.Value.Contains("id=\"title-slide\""))
                .ToList();

            // Collect all blocks per section
            var allSectionBlocks = new List<(int SectionIndex, List<(int Line, string Tag)> Blocks)>();

            for (int s = 0; s < slideStartLines.Count; s++)
            {
                int startIdx = slideStartLines[s] - 1; // 0-based
                int endIdx = s + 1 < slideStartLines.Count
                    ? slideStartLines[s + 1] - 2
                    : lines.Length;
                endIdx = Math.Min(endIdx, lines.Length);

                var blocks = new List<(int Line, string Tag)>();
                bool inCodeBlock = false;
                int codeBlockStart = 0;

                for (int i = startIdx; i < endIdx; i++)
                {
                    var trimmed = lines[i].TrimStart();

                    if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                    {
                        if (!inCodeBlock) { inCodeBlock = true; codeBlockStart = i; }
                        else { inCodeBlock = false; blocks.Add((codeBlockStart + 1, "pre")); }
                        continue;
                    }
                    if (inCodeBlock) continue;
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;

                    var headingMatch = HeadingPattern.Match(trimmed);
                    if (headingMatch.Success)
                    {
                        blocks.Add((i + 1, $"h{headingMatch.Groups[1].Value.Length}"));
                        continue;
                    }
                    if (UnorderedListPattern.IsMatch(trimmed) || OrderedListPattern.IsMatch(trimmed))
                    {
                        blocks.Add((i + 1, "li"));
                        continue;
                    }
                    if (trimmed.StartsWith(">")) { blocks.Add((i + 1, "blockquote")); continue; }
                    if (trimmed.StartsWith("|") && !TableSeparatorPattern.IsMatch(trimmed))
                    {
                        blocks.Add((i + 1, "tr"));
                        continue;
                    }
                    blocks.Add((i + 1, "p"));
                }
                if (inCodeBlock) blocks.Add((codeBlockStart + 1, "pre"));

                if (blocks.Count > 0)
                    allSectionBlocks.Add((s, blocks));
            }

            // Single StringBuilder pass for all sections
            var sb = new StringBuilder(html);
            int offset = 0;

            foreach (var (sectionIndex, blocks) in allSectionBlocks)
            {
                if (sectionIndex >= sectionMatches.Count) continue;

                var sectionStart = sectionMatches[sectionIndex].Index + sectionMatches[sectionIndex].Length;
                var sectionEnd = html.Length;
                if (sectionIndex + 1 < sectionMatches.Count)
                {
                    var closingIdx = html.LastIndexOf("</section>",
                        sectionMatches[sectionIndex + 1].Index, StringComparison.OrdinalIgnoreCase);
                    if (closingIdx > sectionStart) sectionEnd = closingIdx;
                }

                var searchPos = sectionStart;

                foreach (var (line, tag) in blocks)
                {
                    var tagPattern = GetTagPattern(tag);
                    var currentHtml = sb.ToString();
                    var tagMatch = tagPattern.Match(currentHtml, searchPos + offset);
                    if (!tagMatch.Success || tagMatch.Index >= sectionEnd + offset) continue;
                    if (tagMatch.Groups[1].Value.Contains("data-line"))
                    {
                        searchPos = tagMatch.Index + tagMatch.Length - offset;
                        continue;
                    }

                    var insertPos = tagMatch.Index + $"<{tag}".Length;
                    var attr = $" data-line=\"{line}\"";
                    sb.Insert(insertPos, attr);
                    offset += attr.Length;
                    searchPos = tagMatch.Index + tagMatch.Length - offset + attr.Length;
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Injects pointing-mode CSS and JavaScript into the reveal.js HTML.
        /// </summary>
        private static string InjectPointingScripts(string html, string theme = "black")
        {
            // Remove CSP if present
            html = CspMetaPattern.Replace(html, "");

            // Map reveal.js theme to Mermaid theme
            var mermaidTheme = theme switch
            {
                "black" or "night" or "moon" or "league" or "blood" or "dracula" => "dark",
                "beige" or "serif" => "neutral",
                "solarized" or "sky" => "forest",
                _ => "default"  // white, simple, etc.
            };

            var headContent =
                $"<script src='https://cdn.jsdelivr.net/npm/mermaid@11.12.3/dist/mermaid.min.js'></script>\n" +
                $"<script>mermaid.initialize({{ startOnLoad: false, theme: '{mermaidTheme}' }});</script>\n" +
                $"<style>{JsResources.SlideStyles}</style>";
            html = html.Replace("</head>", headContent + "\n</head>");

            var scripts =
                "<script>\n" +
                JsResources.CoreEventHandlers + "\n" +
                JsResources.ScrollAndPointingMode + "\n" +
                JsResources.PointingHelpers + "\n" +
                JsResources.GetElementContent + "\n" +
                JsResources.PointingEventHandlers + "\n" +
                JsResources.MermaidNodeProcessing + "\n" +
                JsResources.SlideHandlers + "\n" +
                "</script>";

            html = html.Replace("</body>", scripts + "\n</body>");
            return html;
        }
    }
}
