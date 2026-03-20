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

            html = AddLineTracking(html, markdown);
            html = InjectPointingScripts(html);
            return html;
        }

        /// <summary>
        /// Exports markdown to .pptx using Pandoc.
        /// Supports --reference-doc for template application.
        /// </summary>
        public static async Task<(bool Success, string? Error)> ExportPptxAsync(
            string markdownPath, string outputPath, string? templatePath = null, string? resourceDir = null)
        {
            try
            {
                var refDoc = !string.IsNullOrEmpty(templatePath) && File.Exists(templatePath)
                    ? $"--reference-doc=\"{templatePath}\" " : "";
                var resPath = !string.IsNullOrEmpty(resourceDir)
                    ? $"--resource-path=\"{resourceDir}\" " : "";
                var args = $"-f markdown -t pptx {refDoc}{resPath}-o \"{outputPath}\" \"{markdownPath}\"";

                var psi = new ProcessStartInfo
                {
                    FileName = "pandoc",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(psi);
                if (process == null) return (false, "Failed to start Pandoc");

                var stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                    return (false, string.IsNullOrWhiteSpace(stderr) ? "Pandoc exited with error" : stderr.Trim());

                return (true, null);
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

        /// <summary>
        /// Adds data-line to block elements within each section.
        /// </summary>
        private static string AddElementLineTracking(string html, string[] lines, List<int> slideStartLines)
        {
            // Build block element list per slide and inject data-line
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

                html = InjectBlockLineAttributes(html, s, blocks);
            }
            return html;
        }

        private static string InjectBlockLineAttributes(string html, int sectionIndex,
            List<(int Line, string Tag)> blocks)
        {
            if (blocks.Count == 0) return html;

            var sectionPattern = new Regex(@"<section\b[^>]*>", RegexOptions.IgnoreCase);
            // Filter out the auto-generated title slide from frontmatter
            var allMatches = sectionPattern.Matches(html);
            var sectionMatches = allMatches.Cast<Match>()
                .Where(m => !m.Value.Contains("id=\"title-slide\""))
                .ToList();
            if (sectionIndex >= sectionMatches.Count) return html;

            var sectionStart = sectionMatches[sectionIndex].Index + sectionMatches[sectionIndex].Length;
            var sectionEnd = html.Length;
            if (sectionIndex + 1 < sectionMatches.Count)
            {
                var closingIdx = html.LastIndexOf("</section>",
                    sectionMatches[sectionIndex + 1].Index, StringComparison.OrdinalIgnoreCase);
                if (closingIdx > sectionStart) sectionEnd = closingIdx;
            }

            var sb = new StringBuilder(html);
            int offset = 0;
            var searchPos = sectionStart;

            foreach (var (line, tag) in blocks)
            {
                var tagPattern = new Regex($@"<{Regex.Escape(tag)}\b([^>]*)>", RegexOptions.IgnoreCase);
                var tagMatch = tagPattern.Match(sb.ToString(), searchPos + offset);
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

            return sb.ToString();
        }

        /// <summary>
        /// Injects pointing-mode CSS and JavaScript into the reveal.js HTML.
        /// </summary>
        private static string InjectPointingScripts(string html)
        {
            // Remove CSP if present
            html = CspMetaPattern.Replace(html, "");

            var pointingCss = @"
<style>
.pointing-highlight {
    outline: 2px solid #0078d4 !important;
    outline-offset: 2px;
    background-color: rgba(0, 120, 212, 0.1) !important;
    cursor: pointer !important;
}
/* Slide sections: never visually highlight */
section.pointing-highlight {
    outline: none !important;
    background-color: unset !important;
}
.code-line {
    display: block; min-height: 1.55em;
}
.code-line.pointing-highlight {
    outline: none !important;
    background-color: rgba(0, 120, 212, 0.25) !important;
}
.pointing-flash {
    animation: flash-effect 0.5s ease-out;
}
@keyframes flash-effect {
    0% { box-shadow: inset 0 0 0 100px rgba(0, 120, 212, 0.4); }
    100% { box-shadow: inset 0 0 0 100px transparent; }
}
</style>";
            html = html.Replace("</head>", pointingCss + "\n</head>");

            var scripts = $@"
<script>
{JsResources.CoreEventHandlers}
{JsResources.ScrollAndPointingMode}
{JsResources.PointingHelpers}
{JsResources.GetElementContent}
{JsResources.PointingEventHandlers}

// Override: treat <section> as boundary, not pointable
var _origGetPointableElement = getPointableElement;
getPointableElement = function(element) {{
    var result = _origGetPointableElement(element);
    if (result && result.tagName && result.tagName.toLowerCase() === 'section') {{
        return null;
    }}
    return result;
}};

// Make Pandoc code block lines pointable
document.addEventListener('DOMContentLoaded', function() {{
    document.querySelectorAll('pre[data-line] code.sourceCode').forEach(function(code) {{
        var baseLine = parseInt(code.closest('pre').getAttribute('data-line')) || 0;
        var lineIndex = 0;
        code.querySelectorAll(':scope > span[id]').forEach(function(span) {{
            span.classList.add('code-line');
            span.setAttribute('data-line', String(baseLine + 1 + lineIndex));
            lineIndex++;
        }});
    }});
}});

// Auto-fit: scale down slides whose content overflows
function autoFitSlides() {{
    if (typeof Reveal === 'undefined' || !Reveal.isReady()) return;
    var slides = Reveal.getSlides();
    slides.forEach(function(slide) {{
        // Reset any previous scaling
        slide.style.transform = '';
        slide.style.transformOrigin = '';
        // Compare content height to slide viewport height
        var viewportH = Reveal.getConfig().height || slide.parentElement.clientHeight || 700;
        var contentH = slide.scrollHeight;
        if (contentH > viewportH) {{
            var scale = viewportH / contentH;
            // Don't scale below 50% — content would be unreadable
            scale = Math.max(scale, 0.5);
            slide.style.transform = 'scale(' + scale + ')';
            slide.style.transformOrigin = 'top left';
            slide.style.width = (100 / scale) + '%';
        }}
    }});
}}

// Slide state query for MCP control
// Returns a plain object (ExecuteScriptAsync will JSON-serialize it)
function getSlideState() {{
    if (typeof Reveal === 'undefined' || !Reveal.isReady()) return null;
    var total = Reveal.getTotalSlides();
    var current = Reveal.getCurrentSlide();
    var currentText = current ? current.textContent.trim().substring(0, 500) : '';
    var overflowed = current ? (current.scrollHeight > (Reveal.getConfig().height || 700)) : false;

    var nextText = null;
    var slides = Reveal.getSlides();
    var currentIdx = slides.indexOf(current);
    if (currentIdx >= 0 && currentIdx + 1 < slides.length) {{
        nextText = slides[currentIdx + 1].textContent.trim().substring(0, 500);
    }}

    return {{
        currentIndex: currentIdx,
        totalSlides: total,
        currentContent: currentText,
        nextContent: nextText,
        overflowed: overflowed
    }};
}}

// Signal render completion and auto-fit
window.addEventListener('load', function() {{
    // Delay to ensure Reveal.js has fully laid out slides
    setTimeout(function() {{
        autoFitSlides();
        window.chrome.webview.postMessage('render-complete:[]');
    }}, 300);
}});
</script>";

            html = html.Replace("</body>", scripts + "\n</body>");
            return html;
        }
    }
}
