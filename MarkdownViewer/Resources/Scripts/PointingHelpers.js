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