using System;
using System.IO;
using Markdig;

var pipeline = new MarkdownPipelineBuilder()
    .UseMathematics()
    .Build();

var markdown = @"
インライン: $E = mc^2$

ブロック:
$$
\sum_{i=1}^{n} i = \frac{n(n+1)}{2}
$$
";

var html = Markdig.Markdown.ToHtml(markdown, pipeline);
Console.WriteLine(html);
