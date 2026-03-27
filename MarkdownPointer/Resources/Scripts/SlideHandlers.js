// Override: treat <section> as boundary, not pointable
var _origGetPointableElement = getPointableElement;
getPointableElement = function(element) {
    var result = _origGetPointableElement(element);
    if (result && result.tagName && result.tagName.toLowerCase() === 'section') {
        return null;
    }
    return result;
};

// Mermaid rendering
// Pandoc wraps mermaid blocks in pre > code with HTML-escaped arrows.
// We unwrap and decode so mermaid.run() can parse the raw source.
// Mermaid detectors are case-sensitive; normalize common case variations.
var _mermaidTypeMap = {
    'flowchart':'flowchart','graph':'graph','sequencediagram':'sequenceDiagram',
    'classdiagram':'classDiagram','statediagram':'stateDiagram',
    'erdiagram':'erDiagram','gitgraph':'gitGraph','gantt':'gantt',
    'pie':'pie','mindmap':'mindmap','timeline':'timeline',
    'journey':'journey','quadrantchart':'quadrantChart',
    'architecture':'architecture','kanban':'kanban','treemap':'treemap','info':'info'
};
function _normalizeMermaidType(text) {
    return text.replace(/^(\s*(?:%%\{[^%]*%%\s*)*)(\S+)/, function(_, prefix, word) {
        var c = _mermaidTypeMap[word.toLowerCase()];
        return c ? prefix + c : prefix + word;
    });
}
document.addEventListener('DOMContentLoaded', async function() {
    if (typeof mermaid === 'undefined') return;
    var pres = document.querySelectorAll('pre.mermaid');
    if (pres.length === 0) return;

    // Make all slides visible so Mermaid can calculate SVG dimensions
    var sections = document.querySelectorAll('section');
    var saved = [];
    sections.forEach(function(s) {
        saved.push({ display: s.style.display, visibility: s.style.visibility });
        s.style.display = 'block';
        s.style.visibility = 'visible';
    });

    for (var pre of pres) {
        var code = pre.querySelector('code');
        if (code) {
            pre.textContent = code.textContent;
        }
        pre.textContent = _normalizeMermaidType(pre.textContent);
        // Save source for export re-rendering
        pre.setAttribute('data-mermaid-source', pre.textContent);
        try {
            await mermaid.run({ nodes: [pre] });
        } catch (e) {
            console.error('[Mermaid]', e);
        }
    }

    // Restore original visibility
    sections.forEach(function(s, i) {
        s.style.display = saved[i].display;
        s.style.visibility = saved[i].visibility;
    });

    // Fit Mermaid SVGs within their slide's available height
    fitMermaidToSlides();
    // Reuse document view's Mermaid node line tracking
    processMermaidNodes();
});

function fitMermaidToSlides() {
    if (typeof Reveal === 'undefined') return;
    var slideW = Reveal.getConfig().width || 960;
    var slideH = Reveal.getConfig().height || 700;
    document.querySelectorAll('.mermaid').forEach(function(elem) {
        var svg = elem.querySelector('svg');
        if (!svg) return;
        var section = elem.closest('section');
        if (!section) return;

        // Get SVG's intrinsic size from viewBox or attributes
        var svgW, svgH;
        var vb = svg.getAttribute('viewBox');
        if (vb) {
            var parts = vb.split(/[\s,]+/);
            svgW = parseFloat(parts[2]);
            svgH = parseFloat(parts[3]);
        }
        if (!svgW) svgW = parseFloat(svg.getAttribute('width')) || 0;
        if (!svgH) svgH = parseFloat(svg.getAttribute('height')) || 0;
        if (svgW <= 0 || svgH <= 0) return;

        // Ensure viewBox is set for proper scaling
        if (!vb) svg.setAttribute('viewBox', '0 0 ' + svgW + ' ' + svgH);

        // Available space in slide coordinates
        var availW = slideW - 80;
        var availH = slideH - 160; // title + padding
        if (availH < 200) availH = 200;

        // Scale to fit
        var scale = Math.min(availW / svgW, availH / svgH, 1);
        svg.setAttribute('width', svgW * scale);
        svg.setAttribute('height', svgH * scale);
    });
}

// Make Pandoc code block lines pointable
document.addEventListener('DOMContentLoaded', function() {
    document.querySelectorAll('pre[data-line] code.sourceCode').forEach(function(code) {
        var baseLine = parseInt(code.closest('pre').getAttribute('data-line')) || 0;
        var lineIndex = 0;
        code.querySelectorAll(':scope > span[id]').forEach(function(span) {
            span.classList.add('code-line');
            span.setAttribute('data-line', String(baseLine + 1 + lineIndex));
            lineIndex++;
        });
    });
});

// Auto-fit: scale down slides whose content overflows
function autoFitSlides() {
    if (typeof Reveal === 'undefined' || !Reveal.isReady()) return;
    var slides = Reveal.getSlides();
    slides.forEach(function(slide) {
        // Reset any previous scaling
        slide.style.transform = '';
        slide.style.transformOrigin = '';
        // Compare content height to slide viewport height
        var viewportH = Reveal.getConfig().height || slide.parentElement.clientHeight || 700;
        var contentH = slide.scrollHeight;
        if (contentH > viewportH) {
            var scale = viewportH / contentH;
            // Don't scale below 50% — content would be unreadable
            scale = Math.max(scale, 0.5);
            slide.style.transform = 'scale(' + scale + ')';
            slide.style.transformOrigin = 'top left';
            slide.style.width = (100 / scale) + '%';
        }
    });
}

// Slide state query for MCP control
// Returns a plain object (ExecuteScriptAsync will JSON-serialize it)
function getSlideState() {
    if (typeof Reveal === 'undefined' || !Reveal.isReady()) return null;
    var total = Reveal.getTotalSlides();
    var current = Reveal.getCurrentSlide();
    var currentText = current ? current.textContent.trim().substring(0, 500) : '';
    var overflowed = current ? (current.scrollHeight > (Reveal.getConfig().height || 700)) : false;

    var nextText = null;
    var slides = Reveal.getSlides();
    var currentIdx = slides.indexOf(current);
    if (currentIdx >= 0 && currentIdx + 1 < slides.length) {
        nextText = slides[currentIdx + 1].textContent.trim().substring(0, 500);
    }

    return {
        currentIndex: currentIdx,
        totalSlides: total,
        currentContent: currentText,
        nextContent: nextText,
        overflowed: overflowed
    };
}

// Signal render completion and auto-fit
window.addEventListener('load', function() {
    function signalComplete() {
        requestAnimationFrame(function() { requestAnimationFrame(function() {
            window.chrome.webview.postMessage('render-complete:[]');
        }); });
    }
    function onRevealReady() {
        autoFitSlides();
        signalComplete();
    }
    if (typeof Reveal !== 'undefined' && Reveal.isReady()) {
        onRevealReady();
    } else if (typeof Reveal !== 'undefined') {
        Reveal.on('ready', onRevealReady);
    } else {
        signalComplete();
    }
});
