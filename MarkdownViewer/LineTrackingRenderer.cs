using System.IO;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MarkdownViewer
{
    /// <summary>
    /// Custom HTML renderer that adds data-line attributes to block elements
    /// </summary>
    public class LineTrackingHtmlRenderer : HtmlRenderer
    {
        public LineTrackingHtmlRenderer(TextWriter writer) : base(writer)
        {
            // Replace default object renderers with line-tracking versions
            ObjectRenderers.Clear();
            
            // Add our custom block renderers
            ObjectRenderers.Add(new LineTrackingParagraphRenderer());
            ObjectRenderers.Add(new LineTrackingHeadingRenderer());
            ObjectRenderers.Add(new LineTrackingCodeBlockRenderer());
            ObjectRenderers.Add(new LineTrackingListRenderer());
            ObjectRenderers.Add(new LineTrackingQuoteBlockRenderer());
            ObjectRenderers.Add(new LineTrackingThematicBreakRenderer());
            ObjectRenderers.Add(new LineTrackingHtmlBlockRenderer());
            
            // Add default inline renderers
            ObjectRenderers.Add(new CodeInlineRenderer());
            ObjectRenderers.Add(new EmphasisInlineRenderer());
            ObjectRenderers.Add(new LineBreakInlineRenderer());
            ObjectRenderers.Add(new LinkInlineRenderer());
            ObjectRenderers.Add(new LiteralInlineRenderer());
            ObjectRenderers.Add(new HtmlEntityInlineRenderer());
            ObjectRenderers.Add(new HtmlInlineRenderer());
            ObjectRenderers.Add(new AutolinkInlineRenderer());
            ObjectRenderers.Add(new DelimiterInlineRenderer());
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
            renderer.Write($"<pre data-line=\"{obj.Line + 1}\"><code");
            
            if (obj is FencedCodeBlock fenced && !string.IsNullOrEmpty(fenced.Info))
            {
                renderer.Write($" class=\"language-{fenced.Info}\"");
            }
            
            renderer.Write(">");
            renderer.WriteLeafRawLines(obj, true, true, true);
            renderer.WriteLine("</code></pre>");
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
}