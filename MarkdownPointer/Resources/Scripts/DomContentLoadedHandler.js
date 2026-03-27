document.addEventListener('DOMContentLoaded', async function() {
    var renderErrors = [];

    // KaTeX rendering with error collection
    var hasMathContent = document.querySelector('.math') !== null;
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
    } else if (hasMathContent) {
        renderErrors.push('[KaTeX] Failed to load library (check your network connection)');
    }

    // Mermaid rendering
    // Mermaid detectors are case-sensitive; normalize common case variations
    var mermaidTypeMap = {
        'flowchart': 'flowchart', 'graph': 'graph', 'sequencediagram': 'sequenceDiagram',
        'classdiagram': 'classDiagram', 'statediagram': 'stateDiagram',
        'erdiagram': 'erDiagram', 'gitgraph': 'gitGraph', 'gantt': 'gantt',
        'pie': 'pie', 'mindmap': 'mindmap', 'timeline': 'timeline',
        'journey': 'journey', 'quadrantchart': 'quadrantChart',
        'architecture': 'architecture', 'kanban': 'kanban', 'treemap': 'treemap',
        'info': 'info'
    };
    function normalizeMermaidType(text) {
        // Skip %%{init:...}%% directives, then match the first word (diagram type)
        return text.replace(/^(\s*(?:%%\{[^%]*%%\s*)*)(\S+)/, function(_, prefix, word) {
            var canonical = mermaidTypeMap[word.toLowerCase()];
            return canonical ? prefix + canonical : prefix + word;
        });
    }

    var hasMermaidContent = document.querySelectorAll('.mermaid').length > 0;
    if (typeof mermaid !== 'undefined') {
        var mermaidElements = document.querySelectorAll('.mermaid');
        for (var elem of mermaidElements) {
            try {
                elem.textContent = normalizeMermaidType(elem.textContent);
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

        // Wrap Mermaid diagrams in scrollable containers
        document.querySelectorAll('.mermaid').forEach(function(elem) {
            var svg = elem.querySelector('svg');
            if (!svg) return;
            // Wrap in scrollable container
            var wrapper = document.createElement('div');
            wrapper.className = 'mermaid-scroll-container';
            elem.parentNode.insertBefore(wrapper, elem);
            wrapper.appendChild(elem);
        });

        // Process Mermaid nodes for click handling
        processMermaidNodes();
    } else if (hasMermaidContent) {
        renderErrors.push('[Mermaid] Failed to load library (check your network connection)');
    }

    // Wait for actual paint before signaling completion
    requestAnimationFrame(function() { requestAnimationFrame(function() {
        window.chrome.webview.postMessage('render-complete:' + JSON.stringify(renderErrors));
    }); });
});