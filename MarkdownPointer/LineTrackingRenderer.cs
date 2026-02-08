using System.IO;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Tables;
using Markdig.Extensions.Mathematics;

namespace MarkdownPointer
{
    /// <summary>
    /// Custom HTML renderer that adds data-line attributes to block elements
    /// </summary>
    public class LineTrackingHtmlRenderer : HtmlRenderer
    {
        public LineTrackingHtmlRenderer(TextWriter writer) : base(writer)
        {
            // Initial replacement (for built-in renderers)
            ReplaceAllRenderers();
        }
        
        /// <summary>
        /// Call this AFTER pipeline.Setup() to replace extension renderers
        /// </summary>
        public void ReplaceExtensionRenderers()
        {
            ReplaceAllRenderers();
        }
        
        private void ReplaceAllRenderers()
        {
            // Replace specific renderers with line-tracking versions
            ReplaceRenderer<ParagraphBlock, LineTrackingParagraphRenderer>();
            ReplaceRenderer<HeadingBlock, LineTrackingHeadingRenderer>();
            ReplaceRenderer<CodeBlock, LineTrackingCodeBlockRenderer>();
            ReplaceRenderer<ListBlock, LineTrackingListRenderer>();
            ReplaceRenderer<QuoteBlock, LineTrackingQuoteBlockRenderer>();
            ReplaceRenderer<ThematicBreakBlock, LineTrackingThematicBreakRenderer>();
            ReplaceRenderer<HtmlBlock, LineTrackingHtmlBlockRenderer>();
            ReplaceRenderer<Table, LineTrackingTableRenderer>();
            ReplaceRenderer<MathBlock, LineTrackingMathBlockRenderer>();
        }
        
        private void ReplaceRenderer<TBlock, TRenderer>() 
            where TBlock : MarkdownObject
            where TRenderer : IMarkdownObjectRenderer, new()
        {
            // Find and remove existing renderer for this block type
            for (int i = ObjectRenderers.Count - 1; i >= 0; i--)
            {
                if (ObjectRenderers[i] is HtmlObjectRenderer<TBlock>)
                {
                    ObjectRenderers.RemoveAt(i);
                }
            }
            
            // Add our custom renderer
            ObjectRenderers.Insert(0, new TRenderer());
        }
    }
    
    public class LineTrackingParagraphRenderer : HtmlObjectRenderer<ParagraphBlock>
    {
        protected override void Write(HtmlRenderer renderer, ParagraphBlock obj)
        {
            renderer.Write($"<p data-line=\"{obj.Line + 1}\">");
            renderer.WriteLeafInline(obj);
            renderer.WriteLine("</p>");
        }
    }
    
    public class LineTrackingHeadingRenderer : HtmlObjectRenderer<HeadingBlock>
    {
        protected override void Write(HtmlRenderer renderer, HeadingBlock obj)
        {
            var tag = $"h{obj.Level}";
            renderer.Write($"<{tag} data-line=\"{obj.Line + 1}\"");
            renderer.WriteAttributes(obj);
            renderer.Write(">");
            renderer.WriteLeafInline(obj);
            renderer.WriteLine($"</{tag}>");
        }
    }
    
    public class LineTrackingCodeBlockRenderer : HtmlObjectRenderer<CodeBlock>
    {
        protected override void Write(HtmlRenderer renderer, CodeBlock obj)
        {
            var isMermaid = obj is FencedCodeBlock fenced && 
                           string.Equals(fenced.Info, "mermaid", StringComparison.OrdinalIgnoreCase);
            
            if (isMermaid)
            {
                // Get mermaid source for data attribute
                var sourceWriter = new StringWriter();
                var tempRenderer = new HtmlRenderer(sourceWriter);
                tempRenderer.WriteLeafRawLines(obj, true, true, true);
                var mermaidSource = sourceWriter.ToString().Trim();
                var escapedSource = mermaidSource.Replace("&", "&amp;").Replace("\"", "&quot;");
                
                // Mermaid needs: <pre class="mermaid" data-mermaid-source="...">content</pre>
                renderer.Write($"<pre class=\"mermaid\" data-line=\"{obj.Line + 1}\" data-mermaid-source=\"{escapedSource}\">");
                renderer.WriteLeafRawLines(obj, true, true, true);
                renderer.WriteLine("</pre>");
            }
            else
            {
                renderer.Write($"<pre data-line=\"{obj.Line + 1}\"><code");
                
                if (obj is FencedCodeBlock fc && !string.IsNullOrEmpty(fc.Info))
                {
                    renderer.Write($" class=\"language-{fc.Info}\"");
                }
                
                renderer.Write(">");
                
                // Render each line wrapped in a span with data-line attribute
                var lines = obj.Lines;
                for (int i = 0; i < lines.Count; i++)
                {
                    var line = lines.Lines[i];
                    var lineContent = System.Web.HttpUtility.HtmlEncode(line.Slice.ToString());
                    var sourceLine = obj.Line + (obj is FencedCodeBlock ? 2 : 1) + i;
                    renderer.Write($"<span class=\"code-line\" data-line=\"{sourceLine}\">{lineContent}</span>");
                }
                
                renderer.WriteLine("</code></pre>");
            }
        }
    }
    
    public class LineTrackingListRenderer : HtmlObjectRenderer<ListBlock>
    {
        protected override void Write(HtmlRenderer renderer, ListBlock obj)
        {
            var tag = obj.IsOrdered ? "ol" : "ul";
            renderer.Write($"<{tag} data-line=\"{obj.Line + 1}\">");
            renderer.WriteLine();
            
            foreach (var item in obj)
            {
                if (item is ListItemBlock listItem)
                {
                    renderer.Write($"<li data-line=\"{listItem.Line + 1}\">");
                    renderer.WriteChildren(listItem);
                    renderer.WriteLine("</li>");
                }
            }
            
            renderer.WriteLine($"</{tag}>");
        }
    }
    
    public class LineTrackingQuoteBlockRenderer : HtmlObjectRenderer<QuoteBlock>
    {
        protected override void Write(HtmlRenderer renderer, QuoteBlock obj)
        {
            renderer.Write($"<blockquote data-line=\"{obj.Line + 1}\">");
            renderer.WriteLine();
            renderer.WriteChildren(obj);
            renderer.WriteLine("</blockquote>");
        }
    }
    
    public class LineTrackingThematicBreakRenderer : HtmlObjectRenderer<ThematicBreakBlock>
    {
        protected override void Write(HtmlRenderer renderer, ThematicBreakBlock obj)
        {
            renderer.WriteLine($"<hr data-line=\"{obj.Line + 1}\" />");
        }
    }
    
    public class LineTrackingHtmlBlockRenderer : HtmlObjectRenderer<HtmlBlock>
    {
        protected override void Write(HtmlRenderer renderer, HtmlBlock obj)
        {
            renderer.WriteLeafRawLines(obj, true, false, false);
        }
    }

    public class LineTrackingTableRenderer : HtmlObjectRenderer<Table>
    {
        protected override void Write(HtmlRenderer renderer, Table obj)
        {
            renderer.Write($"<table data-line=\"{obj.Line + 1}\">");
            renderer.WriteLine();
            
            foreach (var row in obj)
            {
                if (row is TableRow tableRow)
                {
                    var isHeader = tableRow.IsHeader;
                    renderer.Write($"<tr data-line=\"{tableRow.Line + 1}\">");
                    foreach (var cell in tableRow)
                    {
                        if (cell is TableCell tableCell)
                        {
                            var tag = isHeader ? "th" : "td";
                            renderer.Write($"<{tag}>");
                            renderer.WriteChildren(tableCell);
                            renderer.Write($"</{tag}>");
                        }
                    }
                    renderer.WriteLine("</tr>");
                }
            }
            
            renderer.WriteLine("</table>");
        }
    }

    public class LineTrackingMathBlockRenderer : HtmlObjectRenderer<MathBlock>
    {
        protected override void Write(HtmlRenderer renderer, MathBlock obj)
        {
            renderer.Write($"<div class=\"math\" data-line=\"{obj.Line + 1}\">");
            renderer.WriteLine("\\[");
            renderer.WriteLeafRawLines(obj, true, true, true);
            renderer.Write("\\]</div>");
            renderer.WriteLine();
        }
    }
}