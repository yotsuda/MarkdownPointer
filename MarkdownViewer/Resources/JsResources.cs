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
            return '```mermaid ' + firstLine + '```';
        }
        return '```mermaid```';
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
    }
}
