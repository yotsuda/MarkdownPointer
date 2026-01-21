namespace MarkdownViewer.Resources
{
    /// <summary>
    /// JavaScript resources for rendered Markdown content.
    /// Split into multiple properties for maintainability.
    /// </summary>
    public static class JsResources
    {
        /// <summary>
        /// Core event handlers: link clicks, hovers, zoom, mouse events.
        /// </summary>
        public const string CoreEventHandlers = @"
// Handle link clicks
document.addEventListener('click', function(e) {
    var target = e.target;
    while (target && target.tagName !== 'A') {
        target = target.parentElement;
    }
    if (target && target.href) {
        var href = target.getAttribute('href');
        // Handle anchor links (e.g. #footnote-1) within the page
        if (href && href.startsWith('#')) {
            e.preventDefault();
            var targetId = href.substring(1);
            var targetEl = document.getElementById(targetId);
            if (targetEl) {
                targetEl.scrollIntoView({ behavior: 'smooth' });
            }
        } else {
            e.preventDefault();
            window.chrome.webview.postMessage('click:' + target.href);
        }
    }
});

// Handle link hover
document.addEventListener('mouseover', function(e) {
    var target = e.target;
    while (target && target.tagName !== 'A') {
        target = target.parentElement;
    }
    if (target && target.href) {
        window.chrome.webview.postMessage('hover:' + target.href);
    }
});

// Handle Ctrl+wheel for zoom
document.addEventListener('wheel', function(e) {
    if (e.ctrlKey) {
        e.preventDefault();
        window.chrome.webview.postMessage('zoom:' + (e.deltaY < 0 ? 'in' : 'out'));
    }
}, { passive: false });

// Handle mouse leave from link
document.addEventListener('mouseout', function(e) {
    var target = e.target;
    while (target && target.tagName !== 'A') {
        target = target.parentElement;
    }
    if (target && target.tagName === 'A') {
        window.chrome.webview.postMessage('leave:');
    }
});
";

        /// <summary>
        /// Scroll to line function and pointing mode variables.
        /// </summary>
        public const string ScrollAndPointingMode = @"
// Scroll to line function (called from C#)
function scrollToLine(line) {
    var elements = document.querySelectorAll('[data-line]');
    var closest = null;
    var closestLine = -1;
    
    for (var i = 0; i < elements.length; i++) {
        var elemLine = parseInt(elements[i].getAttribute('data-line'));
        if (elemLine <= line && elemLine > closestLine) {
            closest = elements[i];
            closestLine = elemLine;
        }
    }
    
    if (closest) {
        closest.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }
}

// Pointing mode
var pointingModeEnabled = true;
var currentHighlight = null;

function setPointingMode(enabled) {
    pointingModeEnabled = enabled;
    if (!enabled && currentHighlight) {
        currentHighlight.classList.remove('pointing-highlight');
        currentHighlight = null;
    }
    document.body.style.cursor = enabled ? 'crosshair' : '';
    document.body.style.userSelect = enabled ? 'none' : '';
}
";
        /// <summary>
        /// Pointing mode helper functions: getPointableElement, getElementLine, etc.
        /// </summary>
        public const string PointingHelpers = @"
function getPointableElement(element) {
    while (element && element !== document.body) {
        var tagName = element.tagName ? element.tagName.toLowerCase() : '';
        
        // Table cells
        if (tagName === 'td' || tagName === 'th') return element;
        
        // Code block lines
        if (element.classList && element.classList.contains('code-line')) return element;
        
        // Mermaid nodes (g.node, g.cluster, g.edgeLabel)
        if (element.hasAttribute && element.hasAttribute('data-mermaid-node')) {
            return element;
        }
        
        // Elements with data-line
        if (element.hasAttribute && element.hasAttribute('data-line')) return element;
        
        // Mermaid container
        if (element.classList && element.classList.contains('mermaid')) {
            var parent = element;
            while (parent && parent !== document.body) {
                if (parent.hasAttribute && parent.hasAttribute('data-line')) return parent;
                parent = parent.parentElement || parent.parentNode;
            }
            return element;
        }
        // KaTeX - try to find parent with data-line, otherwise return the katex element
        if (element.classList && (element.classList.contains('katex') || element.classList.contains('math'))) {
            // First check if this element itself has data-line
            if (element.hasAttribute && element.hasAttribute('data-line')) return element;
            // Then search parents
            var parent = element.parentElement;
            while (parent && parent !== document.body) {
                if (parent.hasAttribute && parent.hasAttribute('data-line')) return parent;
                parent = parent.parentElement;
            }
            // No parent with data-line found, return the katex element itself
            return element;
        }
        element = element.parentElement || element.parentNode;
    }
    return null;
}

function getElementLine(element) {
    // First check for exact source line (set on Mermaid nodes)
    if (element && element.hasAttribute && element.hasAttribute('data-source-line')) {
        return element.getAttribute('data-source-line');
    }
    while (element && element !== document.body) {
        if (element.hasAttribute && element.hasAttribute('data-line')) {
            return element.getAttribute('data-line');
        }
        // Use parentNode for SVG elements, parentElement for HTML
        element = element.parentElement || element.parentNode;
    }
    return '?';
}

function getTableRowMarkdown(tr) {
    var cells = tr.querySelectorAll('td, th');
    var parts = [];
    cells.forEach(function(cell) { parts.push(cell.textContent.trim()); });
    return '| ' + parts.join(' | ') + ' |';
}
";

        /// <summary>
        /// getElementContent function for extracting element content for clipboard.
        /// </summary>
        public const string GetElementContent = @"
function getElementContent(element) {
    // Check for render error first (e.g., failed Mermaid diagrams)
    var errorElem = element.closest('[data-render-error]');
    if (errorElem) {
        return errorElem.getAttribute('data-render-error');
    }
    
    var tagName = element.tagName.toLowerCase();
    if (tagName === 'td' || tagName === 'th') {
        var tr = element.parentElement;
        var table = tr ? tr.closest('table') : null;
        var cellIndex = Array.from(tr.children).indexOf(element);
        var rowIndex = table ? Array.from(table.querySelectorAll('tr')).indexOf(tr) : 0;
        var cellContent = element.textContent.trim();
        return 'table[row ' + rowIndex + ', col ' + cellIndex + '] cell: ' + cellContent + ' | row: ' + getTableRowMarkdown(tr);
    }
    if (tagName === 'tr') {
        var table = element.closest('table');
        var rowIndex = table ? Array.from(table.querySelectorAll('tr')).indexOf(element) : 0;
        return 'table[row ' + rowIndex + '] ' + getTableRowMarkdown(element);
    }
    if (tagName === 'table') {
        var headerRow = element.querySelector('tr');
        return headerRow ? 'table: ' + getTableRowMarkdown(headerRow) : '(table)';
    }
    // Code block line
    if (element.classList && element.classList.contains('code-line')) {
        var lineNum = element.getAttribute('data-line');
        var pre = element.closest('pre');
        var code = pre ? pre.querySelector('code') : null;
        var lang = '';
        if (code && code.className) {
            var match = code.className.match(/language-(\w+)/);
            if (match) lang = match[1];
        }
        var lineText = element.textContent;
        return 'code[' + (lang || 'text') + ' L' + lineNum + ']: ' + lineText;
    }
    if (tagName === 'ul' || tagName === 'ol') {
        var prefix = tagName === 'ol' ? '1.' : '-';
        var items = [];
        var directLis = element.querySelectorAll(':scope > li');
        directLis.forEach(function(li, idx) {
            var text = '';
            for (var i = 0; i < li.childNodes.length; i++) {
                var node = li.childNodes[i];
                if (node.nodeType === 3) text += node.textContent;
                else if (node.tagName && node.tagName.toLowerCase() === 'p') text += node.textContent;
            }
            text = text.trim();
            if (text.length > 20) text = text.substring(0, 20) + '...';
            var hasNested = li.querySelector('ul, ol');
            var itemPrefix = tagName === 'ol' ? (idx + 1) + '.' : '-';
            items.push(itemPrefix + ' ' + text + (hasNested ? ' [+]' : ''));
        });
        return items.join(', ');
    }
    // Mermaid node (inside SVG)
    if (element.hasAttribute && element.hasAttribute('data-mermaid-node')) {
        var nodeText = element.textContent.trim().replace(/\s+/g, ' ');
        if (nodeText.length > 40) nodeText = nodeText.substring(0, 40) + '...';
        var nodeType = 'node';
        var className = element.getAttribute('class') || '';
        if (element.classList && element.classList.contains('cluster')) nodeType = 'subgraph';
        else if (element.classList && element.classList.contains('edgeLabel')) nodeType = 'edge';
        else if (element.hasAttribute && element.hasAttribute('data-class-name')) {
            nodeType = 'class';
            nodeText = element.getAttribute('data-class-name');
        }
        else if (element.hasAttribute && element.hasAttribute('data-class-relation')) {
            nodeType = 'relation';
            nodeText = element.getAttribute('data-class-relation');
        }
        else if (element.hasAttribute && element.hasAttribute('data-class-member')) {
            var parent = element.parentElement;
            if (parent && parent.classList && parent.classList.contains('methods-group')) {
                nodeType = 'method';
            } else {
                nodeType = 'member';
            }
        }
        else if (className.indexOf('messageText') !== -1) nodeType = 'message';
        else if (element.hasAttribute && element.hasAttribute('data-hit-area-for')) {
            nodeType = 'arrow';
            var hitFor = element.getAttribute('data-hit-area-for');
            var linkMatch = hitFor.match(/^L[-_]([^-_]+)[-_]([^-_]+)[-_]/);
            if (linkMatch) {
                nodeText = linkMatch[1] + ' -> ' + linkMatch[2];
            }
        }
        else if (element.hasAttribute && element.hasAttribute('data-seq-arrow-text')) {
            nodeType = 'arrow';
            nodeText = element.getAttribute('data-seq-arrow-text');
        }
        else if (element.hasAttribute && element.hasAttribute('data-state-node')) {
            nodeType = 'state';
            nodeText = element.getAttribute('data-state-node');
        }
        else if (element.hasAttribute && element.hasAttribute('data-state-transition')) {
            nodeType = 'transition';
            var trans = element.getAttribute('data-state-transition');
            nodeText = trans.replace('->', ' -> ');
        }
        else if (element.hasAttribute && element.hasAttribute('data-er-attr')) {
            nodeType = 'attribute';
            nodeText = element.getAttribute('data-er-attr');
        }
        else if (element.hasAttribute && element.hasAttribute('data-er-entity')) {
            nodeType = 'entity';
            nodeText = element.getAttribute('data-er-entity');
        }
        else if (element.hasAttribute && element.hasAttribute('data-er-relation')) {
            nodeType = 'relationship';
            nodeText = element.getAttribute('data-er-relation');
        }
        else if (element.hasAttribute && element.hasAttribute('data-gantt-task')) {
            nodeType = 'task';
            nodeText = element.getAttribute('data-gantt-task');
        }
        else if (element.hasAttribute && element.hasAttribute('data-gantt-task-name')) {
            nodeType = 'task';
            nodeText = element.getAttribute('data-gantt-task-name');
        }
        else if (element.hasAttribute && element.hasAttribute('data-gantt-section')) {
            nodeType = 'section';
            nodeText = element.getAttribute('data-gantt-section');
        }
        else if (element.hasAttribute && element.hasAttribute('data-gantt-title')) {
            nodeType = 'title';
            nodeText = element.getAttribute('data-gantt-title');
        }
        else if (element.hasAttribute && element.hasAttribute('data-pie-slice')) {
            nodeType = 'slice';
            nodeText = element.getAttribute('data-pie-slice');
        }
        else if (element.hasAttribute && element.hasAttribute('data-pie-legend')) {
            nodeType = 'legend';
            nodeText = element.getAttribute('data-pie-legend');
        }
        else if (element.hasAttribute && element.hasAttribute('data-pie-title')) {
            nodeType = 'title';
            nodeText = element.getAttribute('data-pie-title');
        }
        else if (element.hasAttribute && element.hasAttribute('data-git-commit')) {
            nodeType = 'commit';
            nodeText = 'commit ' + element.getAttribute('data-git-commit');
        }
        else if (element.hasAttribute && element.hasAttribute('data-git-label')) {
            nodeType = 'commit';
            nodeText = element.getAttribute('data-git-label');
        }
        else if (element.hasAttribute && element.hasAttribute('data-git-branch')) {
            nodeType = 'branch';
            nodeText = element.getAttribute('data-git-branch');
        }
        else if (element.hasAttribute && element.hasAttribute('data-mindmap-node')) {
            nodeType = 'node';
            nodeText = element.getAttribute('data-mindmap-node');
        }
        else if (className.indexOf('flowchart-link') !== -1) {
            nodeType = 'arrow';
            var elemId = element.id || '';
            var linkMatch = elemId.match(/^L[-_]([^-_]+)[-_]([^-_]+)[-_]/);
            if (linkMatch) {
                nodeText = linkMatch[1] + ' -> ' + linkMatch[2];
            }
        }
        else if (className.indexOf('messageLine') !== -1) {
            nodeType = 'arrow';
            var prev = element.previousElementSibling;
            if (prev && prev.classList && prev.classList.contains('messageText')) {
                nodeText = prev.textContent.trim();
            }
        }
        else if (className.indexOf('transition') !== -1) {
            nodeType = 'transition';
        }
        return 'mermaid ' + nodeType + ': ' + nodeText;
    }
    // Mermaid container
    if (element.classList && element.classList.contains('mermaid')) {
        var src = element.getAttribute('data-mermaid-source') || '';
        if (src) {
            var firstLine = src.split('\n')[0].trim();
            return 'mermaid diagram: ' + firstLine;
        }
        return 'mermaid diagram';
    }
    if (element.classList.contains('katex') || element.classList.contains('math') || element.querySelector('.katex')) {
        var mathSrc = element.getAttribute('data-math') || element.textContent.trim();
        mathSrc = mathSrc.replace(/\s+/g, ' ');
        if (mathSrc.length > 60) mathSrc = mathSrc.substring(0, 60) + '...';
        return '$$ ' + mathSrc + ' $$';
    }
    if (tagName === 'pre') {
        var code = element.querySelector('code');
        var lang = '';
        if (code && code.className) {
            var match = code.className.match(/language-(\w+)/);
            if (match) lang = match[1];
        }
        var codeText = element.textContent.trim();
        var lines = codeText.split('\n');
        var preview = lines.slice(0, 2).join(' ').substring(0, 50);
        if (lines.length > 2 || codeText.length > 50) preview += '...';
        return '```' + lang + ' ' + preview + ' ```';
    }
    if (/^h[1-6]$/.test(tagName)) {
        var level = tagName.charAt(1);
        return '#'.repeat(parseInt(level)) + ' ' + element.textContent.trim();
    }
    if (tagName === 'li') {
        var text = '';
        var hasNested = false;
        for (var i = 0; i < element.childNodes.length; i++) {
            var node = element.childNodes[i];
            if (node.nodeType === 3) {
                text += node.textContent;
            } else if (node.tagName) {
                var childTag = node.tagName.toLowerCase();
                if (childTag === 'ul' || childTag === 'ol') {
                    hasNested = true;
                } else if (childTag === 'p') {
                    text += node.textContent;
                }
            }
        }
        text = text.trim();
        if (text.length > 60) text = text.substring(0, 60) + '...';
        var parent = element.parentElement;
        var prefix = (parent && parent.tagName.toLowerCase() === 'ol')
            ? (Array.from(parent.children).indexOf(element) + 1) + '. '
            : '- ';
        return prefix + text + (hasNested ? ' (has nested items)' : '');
    }
    if (tagName === 'blockquote') {
        var text = element.textContent.trim();
        if (text.length > 60) text = text.substring(0, 60) + '...';
        return '> ' + text;
    }
    if (tagName === 'hr') return '---';
    var text = element.textContent.trim();
    if (text.length > 80) text = text.substring(0, 80) + '...';
    return text;
}
";

        /// <summary>
        /// Pointing mode mouse event handlers.
        /// </summary>
        public const string PointingEventHandlers = @"
document.addEventListener('mouseover', function(e) {
    if (!pointingModeEnabled) return;
    var pointable = getPointableElement(e.target);
    if (pointable && pointable !== currentHighlight) {
        if (currentHighlight) currentHighlight.classList.remove('pointing-highlight');
        pointable.classList.add('pointing-highlight');
        currentHighlight = pointable;
    }
});

document.addEventListener('mouseout', function(e) {
    if (!pointingModeEnabled) return;
    if (!currentHighlight) return;
    var related = e.relatedTarget;
    var relatedPointable = related ? getPointableElement(related) : null;
    if (relatedPointable !== currentHighlight) {
        currentHighlight.classList.remove('pointing-highlight');
        currentHighlight = null;
    }
});

document.addEventListener('click', function(e) {
    if (!pointingModeEnabled) return;
    var pointable = getPointableElement(e.target);
    if (pointable) {
        e.preventDefault();
        e.stopPropagation();
        
        // Flash effect (SVG uses drop-shadow filter, HTML uses CSS class)
        var flashTarget = pointable;
        if (pointable.hasAttribute && pointable.hasAttribute('data-hit-area-for')) {
            var origId = pointable.getAttribute('data-hit-area-for');
            var origElem = pointable.ownerSVGElement.getElementById(origId);
            if (origElem) flashTarget = origElem;
        } else if (pointable.hasAttribute && pointable.hasAttribute('data-seq-arrow-text')) {
            var prevElem = pointable.previousElementSibling;
            if (prevElem) flashTarget = prevElem;
        } else if (pointable.hasAttribute && pointable.hasAttribute('data-state-transition')) {
            var prevElem = pointable.previousElementSibling;
            if (prevElem) flashTarget = prevElem;
        } else if (pointable.hasAttribute && pointable.hasAttribute('data-er-relation')) {
            var prevElem = pointable.previousElementSibling;
            if (prevElem) flashTarget = prevElem;
        } else if (pointable.hasAttribute && pointable.hasAttribute('data-class-relation')) {
            var prevElem = pointable.previousElementSibling;
            if (prevElem) flashTarget = prevElem;
        }
        var isSvg = flashTarget instanceof SVGElement;
        if (isSvg) {
            flashTarget.style.transition = 'none';
            flashTarget.style.filter = 'drop-shadow(0 0 8px rgba(0, 120, 212, 1)) drop-shadow(0 0 4px rgba(0, 120, 212, 0.8))';
            setTimeout(function() {
                flashTarget.style.transition = 'filter 0.7s ease-out';
                flashTarget.style.filter = '';
            }, 10);
            setTimeout(function() { flashTarget.style.transition = ''; }, 720);
        } else {
            flashTarget.classList.remove('pointing-flash');
            void flashTarget.offsetWidth;
            flashTarget.classList.add('pointing-flash');
            setTimeout(function() { flashTarget.classList.remove('pointing-flash'); }, 500);
        }
        
        var line = getElementLine(pointable);
        var content = getElementContent(pointable);
        window.chrome.webview.postMessage('point:' + line + '|' + content);
    }
}, true);
";

        /// <summary>
        /// DOMContentLoaded handler for KaTeX and Mermaid rendering.
        /// </summary>
        public const string DomContentLoadedHandler = @"
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
                var errorMsg = '[KaTeX Line ' + line + '] ' + formula;
                renderErrors.push(errorMsg);
                // Store error message on the element for click handling
                errElem.setAttribute('data-render-error', errorMsg);
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
                var errorMsg = '[Mermaid Line ' + line + '] ' + msg;
                renderErrors.push(errorMsg);
                // Store error message on the element for click handling
                elem.setAttribute('data-render-error', errorMsg);
            }
        }

        // Process Mermaid nodes for click handling
        processMermaidNodes();
    }

    window.chrome.webview.postMessage('render-complete:' + JSON.stringify(renderErrors));
});
";

        /// <summary>
        /// Mermaid node processing functions for click handling and line mapping.
        /// Includes: processMermaidNodes, parseSourceLines, parseAdditionalPatterns, 
        /// applyMappingsToSvg, createHitArea
        /// </summary>
        public const string MermaidNodeProcessing = @"
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
    // Detect diagram type from first line
    var firstLine = sourceLines[0] ? sourceLines[0].trim().toLowerCase() : '';
    var diagramType = 'unknown';
    if (firstLine.indexOf('graph') === 0 || firstLine.indexOf('flowchart') === 0) diagramType = 'flowchart';
    else if (firstLine.indexOf('sequencediagram') === 0) diagramType = 'sequence';
    else if (firstLine.indexOf('classdiagram') === 0) diagramType = 'class';
    else if (firstLine.indexOf('statediagram') === 0) diagramType = 'state';
    else if (firstLine.indexOf('erdiagram') === 0) diagramType = 'er';
    else if (firstLine.indexOf('gantt') === 0) diagramType = 'gantt';
    else if (firstLine.indexOf('pie') === 0) diagramType = 'pie';
    else if (firstLine.indexOf('gitgraph') === 0) diagramType = 'git';
    else if (firstLine.indexOf('mindmap') === 0) diagramType = 'mindmap';

    for (var i = 0; i < sourceLines.length; i++) {
        var line = sourceLines[i];
        var lineNum = baseLine + i + 1;

        // Flowchart node
        var nodeMatch = line.match(/^\s*([^\s\[\{\(]+)\s*[\[\{\(]/);
        if (nodeMatch && !nodeLineMap[nodeMatch[1]]) {
            nodeLineMap[nodeMatch[1]] = lineNum;
        }

        // Flowchart subgraph
        var subgraphMatch = line.match(/^\s*subgraph\s+(\S+)/);
        if (subgraphMatch) {
            nodeLineMap['subgraph:' + subgraphMatch[1]] = lineNum;
        }

        // Flowchart arrow
        var arrowMatch = line.match(/^\s*([^\s\[\{\(-]+)[^\-]*--[->](\|[^|]*\|)?\s*([^\s\[\{\(]+)/);
        if (arrowMatch) {
            var key = arrowMatch[1] + '-' + arrowMatch[3];
            arrowLineMap[key] = lineNum;
            if (arrowMatch[2]) {
                var label = arrowMatch[2].replace(/\|/g, '');
                edgeLabelLineMap[label] = lineNum;
            }
            // Also register the target node
            if (!nodeLineMap[arrowMatch[3]]) {
                nodeLineMap[arrowMatch[3]] = lineNum;
            }
        }

        // Sequence diagram message
        if (/->>|-->|->/.test(line) && line.indexOf(':') !== -1) {
            messageLineNums.push(lineNum);
        }

        // Sequence participant/actor
        var actorMatch = line.match(/^\s*(participant|actor)\s+(\S+)/);
        if (actorMatch && !nodeLineMap[actorMatch[2]]) {
            nodeLineMap[actorMatch[2]] = lineNum;
        }

        // Sequence implicit actor (from message lines like Alice->>Bob: Hello)
        var seqMatch = line.match(/^\s*([^\s-]+)\s*-/);
        if (seqMatch && !nodeLineMap[seqMatch[1]]) {
            nodeLineMap[seqMatch[1]] = lineNum;
        }

        // Class diagram, state diagram, ER diagram, Gantt, Pie, Git graph patterns
        // (Additional pattern matching for various Mermaid diagram types)
        parseAdditionalPatterns(line, lineNum, nodeLineMap, arrowLineMap, edgeLabelLineMap, diagramType);
    }
}

function parseAdditionalPatterns(line, lineNum, nodeLineMap, arrowLineMap, edgeLabelLineMap, diagramType) {
    // Class diagram relationships
    var classRelPatterns = [
        { regex: /^\s*(\S+)\s*(<\|--|&lt;\|--)\s*(\S+)/, type: 'extends', swap: true, g1: 1, g2: 3 },
        { regex: /^\s*(\S+)\s*(--\|>|--\|&gt;)\s*(\S+)/, type: 'extends', swap: false, g1: 1, g2: 3 },
        { regex: /^\s*(\S+)\s*\*--\s*(\S+)/, type: 'composition', swap: false, g1: 1, g2: 2 },
        { regex: /^\s*(\S+)\s*--\*\s*(\S+)/, type: 'composition', swap: true, g1: 1, g2: 2 },
        { regex: /^\s*(\S+)\s*o--\s*(\S+)/, type: 'aggregation', swap: false, g1: 1, g2: 2 },
        { regex: /^\s*(\S+)\s*--o\s*(\S+)/, type: 'aggregation', swap: true, g1: 1, g2: 2 },
        { regex: /^\s*(\S+)\s*-->\s*(\S+)/, type: 'association', swap: false, g1: 1, g2: 2 },
        { regex: /^\s*(\S+)\s*<--\s*(\S+)/, type: 'association', swap: true, g1: 1, g2: 2 }
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

    // Class diagram class name from member definition: ClassName : member
    var classNameMatch = line.match(/^\s*(\S+)\s*:\s*.+$/);
    if (classNameMatch) {
        if (!nodeLineMap['class:' + classNameMatch[1]]) nodeLineMap['class:' + classNameMatch[1]] = lineNum;
    }

    // Class diagram member/method: ClassName : +memberName or ClassName: +methodName()
    if (diagramType === 'class') {
        var classMemberMatch = line.match(/^\s*(\S+)\s*:\s*(.+)$/);
        if (classMemberMatch) {
            if (!nodeLineMap['class-member-lines']) nodeLineMap['class-member-lines'] = [];
            nodeLineMap['class-member-lines'].push(lineNum);
        }
    }

    // State diagram transition
    var stateTransMatch = line.match(/^\s*(\[\*\]|[^\s-]+)\s*-->\s*(\[\*\]|[^\s-]+)/);
    if (stateTransMatch) {
        arrowLineMap[stateTransMatch[1] + '->' + stateTransMatch[2]] = lineNum;
        // Also register state names
        if (stateTransMatch[1] !== '[*]' && !nodeLineMap['state:' + stateTransMatch[1]]) {
            nodeLineMap['state:' + stateTransMatch[1]] = lineNum;
        }
        if (stateTransMatch[2] !== '[*]' && !nodeLineMap['state:' + stateTransMatch[2]]) {
            nodeLineMap['state:' + stateTransMatch[2]] = lineNum;
        }
    }
    // ER diagram relationship: ENTITY1 ||--o{ ENTITY2 : label
    var erRelMatch = line.match(/^\s*([^\s\|\}o]+)\s*(\||\}|o).*(\||\{|o)\s*([^\s\|\{o:]+)\s*:\s*(\S+)/);
    if (erRelMatch) {
        var erKey = erRelMatch[1] + '-' + erRelMatch[4];
        arrowLineMap[erKey] = lineNum;
        // Map the label
        nodeLineMap[erRelMatch[5]] = lineNum;
        // Map entity names from relationship (for entities without explicit definition)
        if (!nodeLineMap['errel:' + erRelMatch[1]]) nodeLineMap['errel:' + erRelMatch[1]] = lineNum;
        if (!nodeLineMap['errel:' + erRelMatch[4]]) nodeLineMap['errel:' + erRelMatch[4]] = lineNum;
    }

    // ER diagram entity definition: ENTITY {
    var erEntityMatch = line.match(/^\s*([^\s\{]+)\s*\{/);
    if (erEntityMatch && !nodeLineMap['entity:' + erEntityMatch[1]]) {
        nodeLineMap['entity:' + erEntityMatch[1]] = lineNum;
    }

    // Gantt task: TaskName :taskId, ... (index-based for duplicate task names)
    if (diagramType === 'gantt') {
        var ganttTaskMatch = line.match(/^\s*(.+?)\s*:([a-zA-Z0-9]+),/);
        if (ganttTaskMatch) {
            if (!nodeLineMap['gantt-task-lines']) nodeLineMap['gantt-task-lines'] = [];
            nodeLineMap['gantt-task-lines'].push(lineNum);
        }
    }

    // Gantt section: section SectionName
    var ganttSectionMatch = line.match(/^\s*section\s+(.+)$/);
    if (ganttSectionMatch) {
        nodeLineMap['gantt-section:' + ganttSectionMatch[1].trim()] = lineNum;
    }

    // Gantt title: title TitleText
    var ganttTitleMatch = line.match(/^\s*title\s+(.+)$/);
    if (ganttTitleMatch) {
        nodeLineMap['gantt-title:' + ganttTitleMatch[1].trim()] = lineNum;
    }

    // Pie chart slice: ""Label"" : value (index-based for duplicate labels)
    if (diagramType === 'pie') {
        var pieSliceMatch = line.match(/^\s*""([^""]+)""\s*:\s*(\d+)/);
        if (pieSliceMatch) {
            if (!nodeLineMap['pie-slice-lines']) nodeLineMap['pie-slice-lines'] = [];
            nodeLineMap['pie-slice-lines'].push(lineNum);
        }
    }

    // Pie chart title: pie title TitleText
    var pieTitleMatch = line.match(/^\s*pie\s+title\s+(.+)$/);
    if (pieTitleMatch) {
        nodeLineMap['pie-title:' + pieTitleMatch[1].trim()] = lineNum;
    }

    // Git graph commit: commit or commit id: ""label""
    var gitCommitMatch = line.match(/^\s*commit(\s+id:\s*""([^""]+)"")?/);
    if (gitCommitMatch) {
        if (!nodeLineMap['git-commit-count']) nodeLineMap['git-commit-count'] = 0;
        nodeLineMap['git-commit:' + nodeLineMap['git-commit-count']] = lineNum;
        if (gitCommitMatch[2]) nodeLineMap['git-label:' + gitCommitMatch[2]] = lineNum;
        nodeLineMap['git-commit-count']++;
    }

    // Git graph branch: branch BranchName
    var gitBranchMatch = line.match(/^\s*branch\s+(\S+)/);
    if (gitBranchMatch) {
        nodeLineMap['git-branch:' + gitBranchMatch[1]] = lineNum;
    }

    // Git graph merge: merge BranchName (also creates a commit circle)
    var gitMergeMatch = line.match(/^\s*merge\s+(\S+)/);
    if (gitMergeMatch) {
        if (!nodeLineMap['git-commit-count']) nodeLineMap['git-commit-count'] = 0;
        nodeLineMap['git-commit:' + nodeLineMap['git-commit-count']] = lineNum;
        nodeLineMap['git-commit-count']++;
    }

    // Mindmap: collect line numbers in order (index-based for duplicate labels)
    if (diagramType === 'mindmap') {
        if (!nodeLineMap['mindmap-lines']) nodeLineMap['mindmap-lines'] = [];
        // root node
        var mindmapRootMatch = line.match(/^\s*root\s*\(\((.+)\)\)/);
        if (mindmapRootMatch) {
            nodeLineMap['mindmap-lines'].push(lineNum);
        }
        // leaf nodes (indented text)
        var mindmapNodeMatch = line.match(/^\s{2,}(\S+)\s*$/);
        if (mindmapNodeMatch && !line.includes('root')) {
            nodeLineMap['mindmap-lines'].push(lineNum);
        }
    }
}

function applyMappingsToSvg(svg, nodeLineMap, arrowLineMap, messageLineNums, edgeLabelLineMap) {
    // Mark flowchart nodes, sequence actors, class diagram nodes, etc.
    svg.querySelectorAll('g.node, g.cluster, g.edgeLabel, g[id^=""state-""], g[id^=""root-""], g.note, g.activation').forEach(function(node) {
        node.style.cursor = 'pointer';
        node.setAttribute('data-mermaid-node', 'true');

        var nodeId = node.id || '';

        // Flowchart node: flowchart-NodeName-0
        var flowMatch = nodeId.match(/^flowchart-([^-]+)-/);
        if (flowMatch && nodeLineMap[flowMatch[1]]) {
            node.setAttribute('data-source-line', String(nodeLineMap[flowMatch[1]]));
            return;
        }

        // Flowchart subgraph (cluster)
        if (node.classList && node.classList.contains('cluster')) {
            var clusterLabel = node.querySelector('.nodeLabel, text');
            if (clusterLabel) {
                var subgraphName = clusterLabel.textContent.trim();
                if (nodeLineMap['subgraph:' + subgraphName]) {
                    node.setAttribute('data-source-line', String(nodeLineMap['subgraph:' + subgraphName]));
                    return;
                }
            }
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
        // State diagram node: state-StateName-N
        var stateMatch = nodeId.match(/^state-([^-]+)-/);
        if (stateMatch) {
            var stateName = stateMatch[1];
            if (stateName === 'root_start') {
                node.setAttribute('data-state-node', '[*] (start)');
            } else if (stateName === 'root_end') {
                node.setAttribute('data-state-node', '[*] (end)');
            } else {
                node.setAttribute('data-state-node', stateName);
                if (nodeLineMap['state:' + stateName]) {
                    node.setAttribute('data-source-line', String(nodeLineMap['state:' + stateName]));
                }
            }
            return;
        }

        // Flowchart edge label
        if (node.classList.contains('edgeLabel')) {
            var labelText = node.textContent.trim();
            if (edgeLabelLineMap[labelText]) {
                node.setAttribute('data-source-line', String(edgeLabelLineMap[labelText]));
            }
            return;
        }

        // Sequence actor container: root-ActorName
        if (nodeId.indexOf('root-') === 0) {
            var actorText = node.querySelector('text.actor');
            if (actorText) {
                var actorName = actorText.textContent.trim();
                if (nodeLineMap[actorName]) {
                    node.setAttribute('data-source-line', String(nodeLineMap[actorName]));
                }
            }
            return;
        }
    });

    // Sequence diagram: mark bottom actors
    svg.querySelectorAll('rect.actor-bottom').forEach(function(rect) {
        var parent = rect.parentElement;
        if (parent && parent.tagName.toLowerCase() === 'g') {
            parent.style.cursor = 'pointer';
            parent.setAttribute('data-mermaid-node', 'true');
            var textEl = parent.querySelector('text.actor');
            if (textEl) {
                var actorName = textEl.textContent.trim();
                if (nodeLineMap[actorName]) {
                    parent.setAttribute('data-source-line', String(nodeLineMap[actorName]));
                }
            }
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

    // Sequence diagram arrows (message lines) with hit areas
    var seqMsgIdx = 0;
    svg.querySelectorAll('line.messageLine0, line.messageLine1').forEach(function(line) {
        var sourceLine = null;
        if (seqMsgIdx < messageLineNums.length) {
            sourceLine = String(messageLineNums[seqMsgIdx]);
        }

        // Get message text from previous sibling
        var msgText = '';
        var prev = line.previousElementSibling;
        if (prev && prev.classList && prev.classList.contains('messageText')) {
            msgText = prev.textContent.trim();
        }

        // Create hit area for easier clicking
        var bbox = line.getBBox();
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
        hitRect.setAttribute('data-seq-arrow-text', msgText);
        if (sourceLine) {
            hitRect.setAttribute('data-source-line', sourceLine);
        }
        line.parentNode.insertBefore(hitRect, line.nextSibling);

        line.style.cursor = 'pointer';
        line.setAttribute('data-mermaid-node', 'true');
        if (sourceLine) {
            line.setAttribute('data-source-line', sourceLine);
        }
        // Only increment for messageLine0
        if (line.classList.contains('messageLine0')) seqMsgIdx++;
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

    // Class diagram members and methods (index-based for duplicate member names)
    var classMemberLines = nodeLineMap['class-member-lines'] || [];
    svg.querySelectorAll('g.members-group g.label, g.methods-group g.label').forEach(function(label, idx) {
        label.style.cursor = 'pointer';
        label.setAttribute('data-mermaid-node', 'true');
        label.setAttribute('data-class-member', 'true');
        var memberText = label.textContent.trim();
        if (classMemberLines[idx]) {
            label.setAttribute('data-source-line', String(classMemberLines[idx]));
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

    // State diagram transitions
    var stateTransKeys = Object.keys(arrowLineMap).filter(function(k) { return k.indexOf('->') !== -1; });
    var transIdx = 0;
    svg.querySelectorAll('path.transition').forEach(function(path) {
        var sourceLine = null;
        var transKey = stateTransKeys[transIdx] || '';
        if (transKey && arrowLineMap[transKey]) {
            sourceLine = String(arrowLineMap[transKey]);
        }
        createHitArea(path, sourceLine, 'data-state-transition', transKey);
        path.style.cursor = 'pointer';
        path.setAttribute('data-mermaid-node', 'true');
        path.setAttribute('data-state-transition', transKey);
        if (sourceLine) path.setAttribute('data-source-line', sourceLine);
        transIdx++;
    });

    // ER diagram: entity names
    svg.querySelectorAll('g.entityLabel, g.label.name').forEach(function(label) {
        var entityName = label.textContent.trim();
        label.style.cursor = 'pointer';
        label.setAttribute('data-mermaid-node', 'true');
        label.setAttribute('data-er-entity', entityName);
        if (nodeLineMap['entity:' + entityName]) {
            label.setAttribute('data-source-line', String(nodeLineMap['entity:' + entityName]));
        } else if (nodeLineMap['errel:' + entityName]) {
            // Use relationship line for entities without explicit definition
            label.setAttribute('data-source-line', String(nodeLineMap['errel:' + entityName]));
        }
    });

    // ER diagram: relationship lines
    svg.querySelectorAll('path.relationshipLine').forEach(function(path) {
        var pathId = path.id || '';
        // Extract entity names from id like 'id_entity-CUSTOMER-0_entity-ORDER-1_0'
        var relMatch = pathId.match(/entity-([A-Za-z0-9_-]+)-\d+_entity-([A-Za-z0-9_-]+)-/);
        var sourceLine = null;
        var relText = '';

        if (relMatch) {
            var entity1 = relMatch[1];
            var entity2 = relMatch[2];
            relText = entity1 + ' -- ' + entity2;
            var key = entity1 + '-' + entity2;
            if (arrowLineMap[key]) {
                sourceLine = String(arrowLineMap[key]);
            } else {
                // Try reverse order
                key = entity2 + '-' + entity1;
                if (arrowLineMap[key]) {
                    sourceLine = String(arrowLineMap[key]);
                }
            }
        }

        // Create hit area
        var bbox = path.getBBox();
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
        hitRect.setAttribute('data-er-relation', relText);
        if (sourceLine) {
            hitRect.setAttribute('data-source-line', sourceLine);
        }
        path.parentNode.insertBefore(hitRect, path.nextSibling);

        path.style.cursor = 'pointer';
        path.setAttribute('data-mermaid-node', 'true');
        if (sourceLine) {
            path.setAttribute('data-source-line', sourceLine);
        }
    });

    // ER diagram: attributes
    svg.querySelectorAll('g.label.attribute-name, g.label.attribute-type').forEach(function(label) {
        var attrText = label.textContent.trim();
        var isType = label.classList.contains('attribute-type');
        label.style.cursor = 'pointer';
        label.setAttribute('data-mermaid-node', 'true');
        label.setAttribute('data-er-attr', attrText);

        // For attribute-type, get line from next sibling (attribute-name)
        if (isType) {
            var nextSib = label.nextElementSibling;
            if (nextSib && nextSib.classList.contains('attribute-name')) {
                var nameText = nextSib.textContent.trim();
                if (nodeLineMap[nameText]) {
                    label.setAttribute('data-source-line', String(nodeLineMap[nameText]));
                }
            }
        } else {
            // attribute-name
            if (nodeLineMap[attrText]) {
                label.setAttribute('data-source-line', String(nodeLineMap[attrText]));
            }
        }
    });

    // Gantt: tasks (index-based for duplicate task names)
    var ganttTaskLines = nodeLineMap['gantt-task-lines'] || [];
    svg.querySelectorAll('rect.task').forEach(function(task, idx) {
        var taskId = task.id || '';
        task.style.cursor = 'pointer';
        task.setAttribute('data-mermaid-node', 'true');
        task.setAttribute('data-gantt-task', taskId);
        if (ganttTaskLines[idx]) {
            task.setAttribute('data-source-line', String(ganttTaskLines[idx]));
        }
    });

    // Gantt: task text (index-based for duplicate task names)
    svg.querySelectorAll('text.taskText').forEach(function(text, idx) {
        var taskName = text.textContent.trim();
        text.style.cursor = 'pointer';
        text.setAttribute('data-mermaid-node', 'true');
        text.setAttribute('data-gantt-task-name', taskName);
        if (ganttTaskLines[idx]) {
            text.setAttribute('data-source-line', String(ganttTaskLines[idx]));
        }
    });

    // Gantt: section titles
    svg.querySelectorAll('text.sectionTitle').forEach(function(text) {
        var sectionName = text.textContent.trim();
        text.style.cursor = 'pointer';
        text.setAttribute('data-mermaid-node', 'true');
        text.setAttribute('data-gantt-section', sectionName);
        if (nodeLineMap['gantt-section:' + sectionName]) {
            text.setAttribute('data-source-line', String(nodeLineMap['gantt-section:' + sectionName]));
        }
    });

    // Gantt: title
    svg.querySelectorAll('text.titleText').forEach(function(text) {
        var titleText = text.textContent.trim();
        text.style.cursor = 'pointer';
        text.setAttribute('data-mermaid-node', 'true');
        text.setAttribute('data-gantt-title', titleText);
        if (nodeLineMap['gantt-title:' + titleText]) {
            text.setAttribute('data-source-line', String(nodeLineMap['gantt-title:' + titleText]));
        }
    });

    // Pie chart: slices (index-based for duplicate labels)
    var pieSliceLines = nodeLineMap['pie-slice-lines'] || [];
    var pieLegends = svg.querySelectorAll('g.legend text');
    svg.querySelectorAll('path.pieCircle, .pieCircle').forEach(function(slice, idx) {
        slice.style.cursor = 'pointer';
        slice.setAttribute('data-mermaid-node', 'true');
        var legendText = pieLegends[idx] ? pieLegends[idx].textContent.trim() : '';
        slice.setAttribute('data-pie-slice', legendText);
        if (pieSliceLines[idx]) {
            slice.setAttribute('data-source-line', String(pieSliceLines[idx]));
        }
    });

    // Pie chart: legend (index-based for duplicate labels)
    svg.querySelectorAll('g.legend').forEach(function(legend, idx) {
        var legendText = legend.textContent.trim();
        legend.style.cursor = 'pointer';
        legend.setAttribute('data-mermaid-node', 'true');
        legend.setAttribute('data-pie-legend', legendText);
        if (pieSliceLines[idx]) {
            legend.setAttribute('data-source-line', String(pieSliceLines[idx]));
        }
    });

    // Pie chart: title
    svg.querySelectorAll('text.pieTitleText').forEach(function(text) {
        var titleText = text.textContent.trim();
        text.style.cursor = 'pointer';
        text.setAttribute('data-mermaid-node', 'true');
        text.setAttribute('data-pie-title', titleText);
        if (nodeLineMap['pie-title:' + titleText]) {
            text.setAttribute('data-source-line', String(nodeLineMap['pie-title:' + titleText]));
        }
    });

    // Git graph: commits (excluding merge decorations)
    var gitCommitIdx = 0;
    svg.querySelectorAll('circle.commit:not(.commit-merge)').forEach(function(commit) {
        commit.style.cursor = 'pointer';
        commit.setAttribute('data-mermaid-node', 'true');
        commit.setAttribute('data-git-commit', String(gitCommitIdx));
        if (nodeLineMap['git-commit:' + gitCommitIdx]) {
            commit.setAttribute('data-source-line', String(nodeLineMap['git-commit:' + gitCommitIdx]));
        }
        gitCommitIdx++;
    });

    // Git graph: merge decorations (same line as previous commit)
    svg.querySelectorAll('circle.commit-merge').forEach(function(merge) {
        merge.style.cursor = 'pointer';
        merge.setAttribute('data-mermaid-node', 'true');
        var prevSibling = merge.previousElementSibling;
        if (prevSibling && prevSibling.hasAttribute('data-source-line')) {
            merge.setAttribute('data-source-line', prevSibling.getAttribute('data-source-line'));
            merge.setAttribute('data-git-commit', prevSibling.getAttribute('data-git-commit'));
        }
    });

    // Git graph: commit labels
    svg.querySelectorAll('text.commit-label').forEach(function(text) {
        var labelText = text.textContent.trim();
        text.style.cursor = 'pointer';
        text.setAttribute('data-mermaid-node', 'true');
        text.setAttribute('data-git-label', labelText);
        if (nodeLineMap['git-label:' + labelText]) {
            text.setAttribute('data-source-line', String(nodeLineMap['git-label:' + labelText]));
        }
    });

    // Git graph: branch labels
    svg.querySelectorAll('g.branchLabel').forEach(function(branch) {
        var branchName = branch.textContent.trim();
        branch.style.cursor = 'pointer';
        branch.setAttribute('data-mermaid-node', 'true');
        branch.setAttribute('data-git-branch', branchName);
        if (nodeLineMap['git-branch:' + branchName]) {
            branch.setAttribute('data-source-line', String(nodeLineMap['git-branch:' + branchName]));
        }
    });

    // Mindmap: nodes (index-based for duplicate labels)
    var mindmapLines = nodeLineMap['mindmap-lines'] || [];
    svg.querySelectorAll('g.mindmap-node, g.node.mindmap-node').forEach(function(node, idx) {
        var labelEl = node.querySelector('.nodeLabel');
        if (labelEl) {
            var nodeText = labelEl.textContent.trim();
            node.style.cursor = 'pointer';
            node.setAttribute('data-mermaid-node', 'true');
            node.setAttribute('data-mindmap-node', nodeText);
            if (mindmapLines[idx]) {
                node.setAttribute('data-source-line', String(mindmapLines[idx]));
            }
        }
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
";
    }
}
