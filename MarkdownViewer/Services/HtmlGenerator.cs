using System.IO;
using System.Text;
using Markdig;
using MarkdownViewer.Resources;

namespace MarkdownViewer.Services
{
    /// <summary>
    /// Generates HTML content from Markdown source.
    /// </summary>
    public class HtmlGenerator
    {
        private readonly MarkdownPipeline _pipeline;

        public HtmlGenerator(MarkdownPipeline pipeline)
        {
            _pipeline = pipeline;
        }

        /// <summary>
        /// Converts Markdown content to HTML with line tracking, KaTeX, and Mermaid support.
        /// </summary>
        /// <param name="markdown">Markdown source text</param>
        /// <param name="baseDir">Base directory for resolving relative paths</param>
        /// <returns>Complete HTML document</returns>
        public string ConvertToHtml(string markdown, string baseDir)
        {
            // Parse markdown to AST
            var document = Markdown.Parse(markdown, _pipeline);
            
            // Render with line tracking
            using var writer = new StringWriter();
            var renderer = new LineTrackingHtmlRenderer(writer);
            _pipeline.Setup(renderer);
            renderer.ReplaceExtensionRenderers();
            renderer.Render(document);
            var htmlContent = writer.ToString();

            // Convert path for file:// URL
            var baseUrl = new Uri(baseDir + Path.DirectorySeparatorChar).AbsoluteUri;

            // Generate nonce for CSP
            var nonce = Convert.ToBase64String(Guid.NewGuid().ToByteArray());

            var html = new StringBuilder();
            html.AppendLine("<!DOCTYPE html>");
            html.AppendLine("<html><head>");
            html.AppendLine("<meta charset='utf-8'/>");
            html.AppendLine($"<meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; style-src 'unsafe-inline' https://cdn.jsdelivr.net; img-src file: data: blob:; script-src 'nonce-{nonce}' 'unsafe-eval' https://cdn.jsdelivr.net; font-src https://cdn.jsdelivr.net;\"/>");
            html.AppendLine($"<base href='{baseUrl}'/>");
            
            // CSS
            html.AppendLine("<style>");
            html.AppendLine(CssResources.MainStyles);
            html.AppendLine("</style>");
            
            // External libraries
            html.AppendLine("<link rel='stylesheet' href='https://cdn.jsdelivr.net/npm/katex@0.16.9/dist/katex.min.css'/>");
            html.AppendLine("<script defer src='https://cdn.jsdelivr.net/npm/katex@0.16.9/dist/katex.min.js'></script>");
            html.AppendLine("<script defer src='https://cdn.jsdelivr.net/npm/katex@0.16.9/dist/contrib/auto-render.min.js'></script>");
            html.AppendLine("<script src='https://cdn.jsdelivr.net/npm/html2canvas@1.4.1/dist/html2canvas.min.js'></script>");
            
            // Core event handlers
            html.AppendLine($"<script nonce='{nonce}'>");
            html.AppendLine(JsResources.CoreEventHandlers);
            html.AppendLine("</script>");
            
            // Scroll and pointing mode
            html.AppendLine($"<script nonce='{nonce}'>");
            html.AppendLine(JsResources.ScrollAndPointingMode);
            html.AppendLine(JsResources.PointingHelpers);
            html.AppendLine(JsResources.GetElementContent);
            html.AppendLine(JsResources.PointingEventHandlers);
            html.AppendLine("</script>");
            
            // Mermaid
            html.AppendLine("<script src='https://cdn.jsdelivr.net/npm/mermaid/dist/mermaid.min.js'></script>");
            html.AppendLine($"<script nonce='{nonce}'>mermaid.initialize({{ startOnLoad: false, theme: 'default' }});</script>");
            
            html.AppendLine("</head><body>");
            html.AppendLine(htmlContent);
            
            // DOMContentLoaded: KaTeX + Mermaid rendering
            html.AppendLine($"<script nonce='{nonce}'>");
            html.AppendLine(GetDomContentLoadedScript());
            html.AppendLine("</script>");
            
            html.AppendLine("</body></html>");

            return html.ToString();
        }

        /// <summary>
        /// Gets the DOMContentLoaded script for KaTeX and Mermaid rendering.
        /// This includes the complex Mermaid node processing logic.
        /// </summary>
        private static string GetDomContentLoadedScript()
        {
            // This is kept inline due to its complexity and tight coupling with Mermaid rendering
            return DomContentLoadedScript;
        }

        // The DOMContentLoaded script is defined as a constant for clarity
        private const string DomContentLoadedScript = """
document.addEventListener('DOMContentLoaded', async function() {
    var renderErrors = [];
    
    // KaTeX rendering with error collection
    if (typeof renderMathInElement !== 'undefined') {
        renderMathInElement(document.body, {
            delimiters: [
                {left: '\\[', right: '\\]', display: true},
                {left: '\\(', right: '\\)', display: false},
                {left: '$$', right: '$$', display: true},
                {left: '$', right: '$', display: false}
            ],
            throwOnError: false,
            errorCallback: function(msg, err) {
                renderErrors.push('[KaTeX] ' + msg);
            }
        });
        
        document.querySelectorAll('.katex-error').forEach(function(errElem) {
            var line = '?';
            var parent = errElem.parentElement;
            while (parent && parent !== document.body) {
                if (parent.hasAttribute('data-line')) {
                    line = parent.getAttribute('data-line');
                    break;
                }
                parent = parent.parentElement;
            }
            var formula = errElem.getAttribute('title') || errElem.textContent;
            if (formula && !renderErrors.some(e => e.includes(formula.substring(0, 20)))) {
                renderErrors.push('[KaTeX Line ' + line + '] ' + formula);
            }
        });
        
        document.querySelectorAll('.katex').forEach(function(katex) {
            var parent = katex.parentElement;
            while (parent && parent !== document.body) {
                if (parent.hasAttribute('data-line')) {
                    katex.setAttribute('data-line', parent.getAttribute('data-line'));
                    break;
                }
                parent = parent.parentElement;
            }
        });
    }
    
    // Mermaid rendering
    if (typeof mermaid !== 'undefined') {
        var mermaidElements = document.querySelectorAll('.mermaid');
        for (var elem of mermaidElements) {
            try {
                await mermaid.run({ nodes: [elem] });
            } catch (e) {
                var line = elem.getAttribute('data-line') || '?';
                var msg = e.message || String(e);
                renderErrors.push('[Mermaid Line ' + line + '] ' + msg);
            }
        }
        
        // Process Mermaid nodes for click handling
        processMermaidNodes();
    }
    
    window.chrome.webview.postMessage('render-complete:' + JSON.stringify(renderErrors));
});

function processMermaidNodes() {
    document.querySelectorAll('.mermaid').forEach(function(container) {
        var source = container.getAttribute('data-mermaid-source') || '';
        var baseLine = parseInt(container.getAttribute('data-line') || '0', 10);
        var sourceLines = source.split('\n');
        var svg = container.querySelector('svg');
        if (!svg) return;
        
        var nodeLineMap = {};
        var arrowLineMap = {};
        var messageLineNums = [];
        var edgeLabelLineMap = {};
        
        // Parse source for line mappings
        parseSourceLines(sourceLines, baseLine, nodeLineMap, arrowLineMap, messageLineNums, edgeLabelLineMap);
        
        // Apply mappings to SVG elements
        applyMappingsToSvg(svg, nodeLineMap, arrowLineMap, messageLineNums, edgeLabelLineMap);
    });
}

function parseSourceLines(sourceLines, baseLine, nodeLineMap, arrowLineMap, messageLineNums, edgeLabelLineMap) {
    for (var i = 0; i < sourceLines.length; i++) {
        var line = sourceLines[i];
        var lineNum = baseLine + i + 1;
        
        // Flowchart node
        var nodeMatch = line.match(/^\s*([A-Za-z0-9_]+)\s*[\[\{\(]/);
        if (nodeMatch && !nodeLineMap[nodeMatch[1]]) {
            nodeLineMap[nodeMatch[1]] = lineNum;
        }
        
        // Flowchart arrow
        var arrowMatch = line.match(/^\s*([A-Za-z0-9_]+)[^\-]*--[->](\|[^|]*\|)?\s*([A-Za-z0-9_]+)/);
        if (arrowMatch) {
            var key = arrowMatch[1] + '-' + arrowMatch[3];
            arrowLineMap[key] = lineNum;
            if (arrowMatch[2]) {
                var label = arrowMatch[2].replace(/\|/g, '');
                edgeLabelLineMap[label] = lineNum;
            }
        }
        
        // Sequence diagram message
        if (/->>|-->|->/.test(line) && line.indexOf(':') !== -1) {
            messageLineNums.push(lineNum);
        }
        
        // Sequence participant/actor
        var actorMatch = line.match(/^\s*(participant|actor)\s+([A-Za-z0-9_]+)/);
        if (actorMatch && !nodeLineMap[actorMatch[2]]) {
            nodeLineMap[actorMatch[2]] = lineNum;
        }
        
        // Class diagram, state diagram, ER diagram, Gantt, Pie, Git graph patterns
        // (Additional pattern matching for various Mermaid diagram types)
        parseAdditionalPatterns(line, lineNum, nodeLineMap, arrowLineMap, edgeLabelLineMap);
    }
}

function parseAdditionalPatterns(line, lineNum, nodeLineMap, arrowLineMap, edgeLabelLineMap) {
    // Class diagram relationships
    var classRelPatterns = [
        { regex: /^\s*([A-Za-z0-9_]+)\s*(<\|--|&lt;\|--)\s*([A-Za-z0-9_]+)/, type: 'extends', swap: true, g1: 1, g2: 3 },
        { regex: /^\s*([A-Za-z0-9_]+)\s*(--\|>|--\|&gt;)\s*([A-Za-z0-9_]+)/, type: 'extends', swap: false, g1: 1, g2: 3 },
        { regex: /^\s*([A-Za-z0-9_]+)\s*\*--\s*([A-Za-z0-9_]+)/, type: 'composition', swap: false, g1: 1, g2: 2 },
        { regex: /^\s*([A-Za-z0-9_]+)\s*--\*\s*([A-Za-z0-9_]+)/, type: 'composition', swap: true, g1: 1, g2: 2 },
        { regex: /^\s*([A-Za-z0-9_]+)\s*o--\s*([A-Za-z0-9_]+)/, type: 'aggregation', swap: false, g1: 1, g2: 2 },
        { regex: /^\s*([A-Za-z0-9_]+)\s*--o\s*([A-Za-z0-9_]+)/, type: 'aggregation', swap: true, g1: 1, g2: 2 },
        { regex: /^\s*([A-Za-z0-9_]+)\s*-->\s*([A-Za-z0-9_]+)/, type: 'association', swap: false, g1: 1, g2: 2 },
        { regex: /^\s*([A-Za-z0-9_]+)\s*<--\s*([A-Za-z0-9_]+)/, type: 'association', swap: true, g1: 1, g2: 2 }
    ];
    
    for (var pi = 0; pi < classRelPatterns.length; pi++) {
        var p = classRelPatterns[pi];
        var m = line.match(p.regex);
        if (m) {
            var class1 = m[p.g1];
            var class2 = m[p.g2];
            if (!nodeLineMap['class:' + class1]) nodeLineMap['class:' + class1] = lineNum;
            if (!nodeLineMap['class:' + class2]) nodeLineMap['class:' + class2] = lineNum;
            var from = p.swap ? class2 : class1;
            var to = p.swap ? class1 : class2;
            arrowLineMap['class-rel:' + class1 + '_' + class2] = lineNum + ':' + p.type + ':' + from + ':' + to;
            break;
        }
    }
    
    // State diagram transition
    var stateTransMatch = line.match(/^\s*(\[\*\]|[A-Za-z0-9_]+)\s*-->\s*(\[\*\]|[A-Za-z0-9_]+)/);
    if (stateTransMatch) {
        arrowLineMap[stateTransMatch[1] + '->' + stateTransMatch[2]] = lineNum;
    }
    
    // ER diagram
    var erRelMatch = line.match(/^\s*([A-Za-z0-9_-]+)\s*(\||\}|o).*(\||\{|o)\s*([A-Za-z0-9_-]+)\s*:\s*(\w+)/);
    if (erRelMatch) {
        arrowLineMap[erRelMatch[1] + '-' + erRelMatch[4]] = lineNum;
        nodeLineMap[erRelMatch[5]] = lineNum;
    }
    
    // Gantt
    var ganttTaskMatch = line.match(/^\s*(.+?)\s*:([a-zA-Z0-9]+),/);
    if (ganttTaskMatch) {
        nodeLineMap['gantt:' + ganttTaskMatch[2]] = lineNum;
        nodeLineMap['gantt-name:' + ganttTaskMatch[1].trim()] = lineNum;
    }
    
    // Pie chart
    var pieSliceMatch = line.match(/^\s*"([^"]+)"\s*:\s*(\d+)/);
    if (pieSliceMatch) {
        nodeLineMap['pie:' + pieSliceMatch[1]] = lineNum;
    }
    
    // Git graph
    var gitCommitMatch = line.match(/^\s*commit(\s+id:\s*"([^"]+)")?/);
    if (gitCommitMatch) {
        if (!nodeLineMap['git-commit-count']) nodeLineMap['git-commit-count'] = 0;
        nodeLineMap['git-commit:' + nodeLineMap['git-commit-count']] = lineNum;
        if (gitCommitMatch[2]) nodeLineMap['git-label:' + gitCommitMatch[2]] = lineNum;
        nodeLineMap['git-commit-count']++;
    }
}

function applyMappingsToSvg(svg, nodeLineMap, arrowLineMap, messageLineNums, edgeLabelLineMap) {
    // Mark flowchart nodes, sequence actors, class diagram nodes, etc.
    svg.querySelectorAll('g.node, g.cluster, g.edgeLabel').forEach(function(node) {
        node.style.cursor = 'pointer';
        node.setAttribute('data-mermaid-node', 'true');
        
        var nodeId = node.id || '';
        
        // Flowchart node: flowchart-NodeName-0
        var flowMatch = nodeId.match(/^flowchart-([^-]+)-/);
        if (flowMatch && nodeLineMap[flowMatch[1]]) {
            node.setAttribute('data-source-line', String(nodeLineMap[flowMatch[1]]));
            return;
        }
        
        // Class diagram node: classId-ClassName-0
        var classMatch = nodeId.match(/^classId-([^-]+)-/);
        if (classMatch) {
            var className = classMatch[1];
            if (nodeLineMap['class:' + className]) {
                node.setAttribute('data-source-line', String(nodeLineMap['class:' + className]));
            }
            return;
        }
    });
    
    // Sequence messages
    var msgIdx = 0;
    svg.querySelectorAll('text.messageText').forEach(function(msg) {
        msg.style.cursor = 'pointer';
        msg.setAttribute('data-mermaid-node', 'true');
        if (msgIdx < messageLineNums.length) {
            msg.setAttribute('data-source-line', String(messageLineNums[msgIdx]));
        }
        msgIdx++;
    });
    
    // Flowchart arrows with hit areas
    svg.querySelectorAll('path.flowchart-link').forEach(function(path) {
        var pathId = path.id || '';
        var linkMatch = pathId.match(/^L[-_]([^-_]+)[-_]([^-_]+)[-_]/);
        var sourceLine = null;
        if (linkMatch && arrowLineMap[linkMatch[1] + '-' + linkMatch[2]]) {
            sourceLine = String(arrowLineMap[linkMatch[1] + '-' + linkMatch[2]]);
        }
        
        createHitArea(path, sourceLine, 'data-hit-area-for', pathId);
        path.setAttribute('data-mermaid-node', 'true');
        if (sourceLine) path.setAttribute('data-source-line', sourceLine);
    });
    
    // Class diagram class names
    svg.querySelectorAll('g.label-group g.label').forEach(function(label) {
        var className = label.textContent.trim();
        if (nodeLineMap['class:' + className]) {
            label.style.cursor = 'pointer';
            label.setAttribute('data-mermaid-node', 'true');
            label.setAttribute('data-class-name', className);
            label.setAttribute('data-source-line', String(nodeLineMap['class:' + className]));
        }
    });
    
    // Class diagram members and methods
    svg.querySelectorAll('g.members-group g.label, g.methods-group g.label').forEach(function(label) {
        label.style.cursor = 'pointer';
        label.setAttribute('data-mermaid-node', 'true');
        label.setAttribute('data-class-member', 'true');
        var memberText = label.textContent.trim();
        if (nodeLineMap[memberText]) {
            label.setAttribute('data-source-line', String(nodeLineMap[memberText]));
        }
    });
    
    // Class diagram relations (inheritance, composition, etc.)
    svg.querySelectorAll('path.relation').forEach(function(path) {
        var pathId = path.id || '';
        var relMatch = pathId.match(/^id_([^_]+)_([^_]+)_\d+$/);
        var relKey = relMatch ? 'class-rel:' + relMatch[1] + '_' + relMatch[2] : '';
        var relInfo = arrowLineMap[relKey];
        var sourceLine = null;
        var relText = '';
        
        if (relInfo) {
            var parts = relInfo.split(':');
            sourceLine = parts[0];
            var relType = parts[1];
            var fromClass = parts[2];
            var toClass = parts[3];
            var typeLabels = {
                'extends': ' extends ',
                'composition': ' *-- ',
                'aggregation': ' o-- ',
                'association': ' --> ',
                'dependency': ' ..> ',
                'realization': ' implements '
            };
            relText = fromClass + (typeLabels[relType] || ' -- ') + toClass;
        }
        
        // Create hit area including marker
        var totalLen = path.getTotalLength();
        var startPoint = path.getPointAtLength(0);
        var endPoint = path.getPointAtLength(totalLen);
        var markerStart = path.getAttribute('marker-start') || '';
        var markerEnd = path.getAttribute('marker-end') || '';
        var markerLen = 18;
        var tipX, tipY;
        
        if (markerStart && markerStart !== 'none') {
            var nearStart = path.getPointAtLength(Math.min(1, totalLen));
            var dx = startPoint.x - nearStart.x;
            var dy = startPoint.y - nearStart.y;
            var len = Math.sqrt(dx * dx + dy * dy);
            if (len > 0) { dx /= len; dy /= len; }
            tipX = startPoint.x + dx * markerLen;
            tipY = startPoint.y + dy * markerLen;
        } else if (markerEnd && markerEnd !== 'none') {
            var nearEnd = path.getPointAtLength(Math.max(0, totalLen - 1));
            var dx = endPoint.x - nearEnd.x;
            var dy = endPoint.y - nearEnd.y;
            var len = Math.sqrt(dx * dx + dy * dy);
            if (len > 0) { dx /= len; dy /= len; }
            tipX = endPoint.x + dx * markerLen;
            tipY = endPoint.y + dy * markerLen;
        } else {
            tipX = startPoint.x;
            tipY = startPoint.y;
        }
        
        var minX = Math.min(startPoint.x, endPoint.x, tipX);
        var maxX = Math.max(startPoint.x, endPoint.x, tipX);
        var minY = Math.min(startPoint.y, endPoint.y, tipY);
        var maxY = Math.max(startPoint.y, endPoint.y, tipY);
        var rectWidth = maxX - minX;
        var rectX = minX;
        if (rectWidth < 16) {
            rectX = minX - (16 - rectWidth) / 2;
            rectWidth = 16;
        }
        
        var hitRect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
        hitRect.setAttribute('x', rectX);
        hitRect.setAttribute('y', minY);
        hitRect.setAttribute('width', rectWidth);
        hitRect.setAttribute('height', maxY - minY);
        hitRect.setAttribute('fill', 'transparent');
        hitRect.style.cursor = 'pointer';
        hitRect.setAttribute('data-mermaid-node', 'true');
        hitRect.setAttribute('data-class-relation', relText);
        if (sourceLine) hitRect.setAttribute('data-source-line', String(sourceLine));
        path.parentNode.insertBefore(hitRect, path.nextSibling);
        
        path.style.cursor = 'pointer';
        path.setAttribute('data-mermaid-node', 'true');
        path.setAttribute('data-class-relation', relText);
        if (sourceLine) path.setAttribute('data-source-line', String(sourceLine));
    });
}

function createHitArea(element, sourceLine, dataAttr, dataValue) {
    var bbox = element.getBBox();
    var minSize = 16;
    var rectW = Math.max(bbox.width, minSize);
    var rectH = Math.max(bbox.height, minSize);
    var rectX = bbox.x - (rectW - bbox.width) / 2;
    var rectY = bbox.y - (rectH - bbox.height) / 2;
    
    var hitRect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
    hitRect.setAttribute('x', rectX);
    hitRect.setAttribute('y', rectY);
    hitRect.setAttribute('width', rectW);
    hitRect.setAttribute('height', rectH);
    hitRect.setAttribute('fill', 'transparent');
    hitRect.style.cursor = 'pointer';
    hitRect.setAttribute('data-mermaid-node', 'true');
    if (dataAttr && dataValue) hitRect.setAttribute(dataAttr, dataValue);
    if (sourceLine) hitRect.setAttribute('data-source-line', sourceLine);
    
    element.parentNode.insertBefore(hitRect, element.nextSibling);
}
""";
    }
}
