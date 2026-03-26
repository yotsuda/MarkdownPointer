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
            var sectionMatches = SectionOpenPattern.Matches(html)
                .Cast<Match>()
                .Where(m => !m.Value.Contains("id=\"title-slide\""))
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
/* Prevent reveal.js from affecting Mermaid diagram internals */
.mermaid svg foreignObject {
    overflow: visible !important;
}
.mermaid svg foreignObject div {
    text-align: center !important;
}
/* Fit Mermaid diagrams within the slide */
.mermaid {
    display: flex !important;
    justify-content: center;
}
</style>";
            // Map reveal.js theme to Mermaid theme
            var mermaidTheme = theme switch
            {
                "black" or "night" or "moon" or "league" or "blood" or "dracula" => "dark",
                "beige" or "serif" => "neutral",
                "solarized" or "sky" => "forest",
                _ => "default"  // white, simple, etc.
            };
            var mermaidSetup = $@"
<script src='https://cdn.jsdelivr.net/npm/mermaid@11.12.3/dist/mermaid.min.js'></script>
<script>mermaid.initialize({{ startOnLoad: false, theme: '{mermaidTheme}' }});</script>";
            html = html.Replace("</head>", mermaidSetup + "\n" + pointingCss + "\n</head>");

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

// Mermaid rendering
// Pandoc wraps mermaid blocks in pre > code with HTML-escaped arrows.
// We unwrap and decode so mermaid.run() can parse the raw source.
// Mermaid detectors are case-sensitive; normalize common case variations.
var _mermaidTypeMap = {{
    'flowchart':'flowchart','graph':'graph','sequencediagram':'sequenceDiagram',
    'classdiagram':'classDiagram','statediagram':'stateDiagram',
    'erdiagram':'erDiagram','gitgraph':'gitGraph','gantt':'gantt',
    'pie':'pie','mindmap':'mindmap','timeline':'timeline',
    'journey':'journey','quadrantchart':'quadrantChart',
    'architecture':'architecture','kanban':'kanban','treemap':'treemap','info':'info'
}};
function _normalizeMermaidType(text) {{
    return text.replace(/^(\s*(?:%%\{{[^%]*%%\s*)*)(\S+)/, function(_, prefix, word) {{
        var c = _mermaidTypeMap[word.toLowerCase()];
        return c ? prefix + c : prefix + word;
    }});
}}
document.addEventListener('DOMContentLoaded', async function() {{
    if (typeof mermaid === 'undefined') return;
    var pres = document.querySelectorAll('pre.mermaid');
    if (pres.length === 0) return;

    // Make all slides visible so Mermaid can calculate SVG dimensions
    var sections = document.querySelectorAll('section');
    var saved = [];
    sections.forEach(function(s) {{
        saved.push({{ display: s.style.display, visibility: s.style.visibility }});
        s.style.display = 'block';
        s.style.visibility = 'visible';
    }});

    for (var pre of pres) {{
        var code = pre.querySelector('code');
        if (code) {{
            pre.textContent = code.textContent;
        }}
        pre.textContent = _normalizeMermaidType(pre.textContent);
        try {{
            await mermaid.run({{ nodes: [pre] }});
        }} catch (e) {{
            console.error('[Mermaid]', e);
        }}
    }}

    // Restore original visibility
    sections.forEach(function(s, i) {{
        s.style.display = saved[i].display;
        s.style.visibility = saved[i].visibility;
    }});

    // Fit Mermaid SVGs within their slide's available height
    fitMermaidToSlides();
}});

function fitMermaidToSlides() {{
    if (typeof Reveal === 'undefined') return;
    var slideW = Reveal.getConfig().width || 960;
    var slideH = Reveal.getConfig().height || 700;
    document.querySelectorAll('.mermaid').forEach(function(elem) {{
        var svg = elem.querySelector('svg');
        if (!svg) return;
        var section = elem.closest('section');
        if (!section) return;

        // Get SVG's intrinsic size from viewBox or attributes
        var svgW, svgH;
        var vb = svg.getAttribute('viewBox');
        if (vb) {{
            var parts = vb.split(/[\s,]+/);
            svgW = parseFloat(parts[2]);
            svgH = parseFloat(parts[3]);
        }}
        if (!svgW) svgW = parseFloat(svg.getAttribute('width')) || 0;
        if (!svgH) svgH = parseFloat(svg.getAttribute('height')) || 0;
        if (svgW <= 0 || svgH <= 0) return;

        // Ensure viewBox is set for proper scaling
        if (!vb) svg.setAttribute('viewBox', '0 0 ' + svgW + ' ' + svgH);

        // Available space in slide coordinates
        var availW = slideW - 80;
        var availH = slideH - 160; // title + padding
        if (availH < 200) availH = 200;

        // Scale to fit
        var scale = Math.min(availW / svgW, availH / svgH, 1);
        svg.setAttribute('width', svgW * scale);
        svg.setAttribute('height', svgH * scale);
    }});
}}

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
