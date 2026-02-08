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
                var arrowType = element.getAttribute('data-arrow-type') || '-->';
                nodeText = linkMatch[1] + ' ' + arrowType + ' ' + linkMatch[2];
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
                var arrowType = element.getAttribute('data-arrow-type') || '-->';
                nodeText = linkMatch[1] + ' ' + arrowType + ' ' + linkMatch[2];
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