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