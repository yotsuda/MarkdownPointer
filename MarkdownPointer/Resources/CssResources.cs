namespace MarkdownPointer.Resources
{
    /// <summary>
    /// CSS styles for rendered Markdown content.
    /// </summary>
    public static class CssResources
    {
        public const string MainStyles = @"
body {
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Helvetica, Arial, sans-serif;
    line-height: 1.6;
    padding: 40px;
    max-width: 980px;
    margin: 0 auto;
    background-color: #ffffff;
    color: #24292e;
}
h1, h2, h3, h4, h5, h6 {
    margin-top: 24px;
    margin-bottom: 16px;
    font-weight: 600;
    line-height: 1.25;
}
h1 { font-size: 2em; border-bottom: 1px solid #eaecef; padding-bottom: 0.3em; }
h2 { font-size: 1.5em; border-bottom: 1px solid #eaecef; padding-bottom: 0.3em; }
h3 { font-size: 1.25em; }
code {
    background-color: rgba(27,31,35,0.05);
    padding: 0.2em 0.4em;
    margin: 0;
    border-radius: 3px;
    font-family: 'Consolas', 'Monaco', 'Courier New', monospace;
    font-size: 85%;
}
pre {
    background-color: #f6f8fa;
    padding: 16px;
    border-radius: 6px;
    overflow: auto;
    line-height: 1.55;
}
pre code {
    background-color: transparent;
    padding: 0;
    font-size: 100%;
}
blockquote {
    padding: 0 1em;
    color: #6a737d;
    border-left: 0.25em solid #dfe2e5;
    margin: 0 0 16px 0;
}
ul, ol {
    padding-left: 2em;
    margin-bottom: 16px;
}
li { margin-bottom: 4px; }
table {
    border-collapse: collapse;
    width: 100%;
    margin-bottom: 16px;
}
table th, table td {
    padding: 6px 13px;
    border: 1px solid #dfe2e5;
}
table th {
    font-weight: 600;
    background-color: #f6f8fa;
}
table tr:nth-child(2n) {
    background-color: #f6f8fa;
}
table th p, table td p {
    margin: 0;
    display: inline;
}
a {
    color: #0366d6;
    text-decoration: none;
}
a:hover {
    text-decoration: underline;
}
img {
    max-width: 100%;
    box-sizing: border-box;
}
hr {
    height: 0.25em;
    padding: 0;
    margin: 24px 0;
    background-color: #e1e4e8;
    border: 0;
}
p {
    margin-bottom: 16px;
}
.pointing-highlight {
    outline: 2px solid #0078d4 !important;
    outline-offset: 2px;
    background-color: rgba(0, 120, 212, 0.1) !important;
    cursor: pointer !important;
}
/* Pie chart slices - use filter instead of outline */
.pieCircle.pointing-highlight {
    outline: none !important;
    background-color: transparent !important;
    filter: drop-shadow(0 0 4px #0078d4) drop-shadow(0 0 2px #0078d4);
}
/* Diamond/rhombus nodes (g element containing polygon) - use filter */
g.pointing-highlight:has(polygon) {
    outline: none !important;
    background-color: transparent !important;
    filter: drop-shadow(0 0 4px #0078d4) drop-shadow(0 0 2px #0078d4);
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
";
    }
}