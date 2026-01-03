using System.IO;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Tables;

namespace MarkdownViewer
{
    /// <summary>
    /// Custom HTML renderer that adds data-line attributes to block elements
    /// </summary>
    public class LineTrackingHtmlRenderer : HtmlRenderer
    {
        public LineTrackingHtmlRenderer(TextWriter writer) : base(writer)
        {
            // Replace specific renderers with line-tracking versions
            // pipeline.Setup() will be called after to add extension renderers
            ReplaceRenderer<ParagraphBlock, LineTrackingParagraphRenderer>();
            ReplaceRenderer<HeadingBlock, LineTrackingHeadingRenderer>();
            ReplaceRenderer<CodeBlock, LineTrackingCodeBlockRenderer>();
            ReplaceRenderer<ListBlock, LineTrackingListRenderer>();
            ReplaceRenderer<QuoteBlock, LineTrackingQuoteBlockRenderer>();
            ReplaceRenderer<ThematicBreakBlock, LineTrackingThematicBreakRenderer>();
            ReplaceRenderer<HtmlBlock, LineTrackingHtmlBlockRenderer>();
            ReplaceRenderer<Table, LineTrackingTableRenderer>();
            
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
                // Mermaid needs: <pre class="mermaid">content</pre>
                renderer.Write($"<pre class=\"mermaid\" data-line=\"{obj.Line + 1}\">");
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
                renderer.WriteLeafRawLines(obj, true, true, true);
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
}