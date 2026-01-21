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
        var isPieSlice = isSvg && flashTarget.classList && flashTarget.classList.contains('pieCircle');
        var isSvgRoot = isSvg && flashTarget.tagName && flashTarget.tagName.toLowerCase() === 'svg';
        // Check if HTML element contains a mermaid diagram (SVG inside)
        var containsMermaid = !isSvg && flashTarget.querySelector && flashTarget.querySelector('svg.mermaid, .mermaid svg');
        
        if (isPieSlice) {
            // Pie chart: animate fill color blend
            var origFill = flashTarget.getAttribute('fill');
            var computed = window.getComputedStyle(flashTarget).fill;
            var match = computed.match(/rgb\s*\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)/);
            var orig = match ? {r: +match[1], g: +match[2], b: +match[3]} : {r: 200, g: 200, b: 200};
            var flash = {r: 0, g: 120, b: 212};
            var start = performance.now();
            (function anim(now) {
                var p = Math.min((now - start) / 500, 1);
                var e = 1 - Math.pow(1 - p, 2);
                var b = 0.4 * (1 - e);
                var c = {
                    r: Math.round(orig.r * (1 - b) + flash.r * b),
                    g: Math.round(orig.g * (1 - b) + flash.g * b),
                    b: Math.round(orig.b * (1 - b) + flash.b * b)
                };
                var hex = '#' + [c.r, c.g, c.b].map(function(x) { return x.toString(16).padStart(2, '0'); }).join('');
                flashTarget.setAttribute('fill', hex);
                if (p < 1) requestAnimationFrame(anim);
                else if (origFill) flashTarget.setAttribute('fill', origFill);
            })(start);
        } else if (isSvgRoot || containsMermaid) {
            // SVG root or HTML containing mermaid: outer glow effect
            flashTarget.style.transition = 'none';
            flashTarget.style.boxShadow = '0 0 12px 4px rgba(0, 120, 212, 0.6)';
            setTimeout(function() {
                flashTarget.style.transition = 'box-shadow 0.5s ease-out';
                flashTarget.style.boxShadow = '';
            }, 10);
            setTimeout(function() { flashTarget.style.transition = ''; }, 520);
        } else if (isSvg) {
            // SVG child elements: use drop-shadow filter
            flashTarget.style.transition = 'none';
            flashTarget.style.filter = 'drop-shadow(0 0 8px rgba(0, 120, 212, 1)) drop-shadow(0 0 4px rgba(0, 120, 212, 0.8))';
            setTimeout(function() {
                flashTarget.style.transition = 'filter 0.7s ease-out';
                flashTarget.style.filter = '';
            }, 10);
            setTimeout(function() { flashTarget.style.transition = ''; }, 720);
        } else {
            // HTML elements: use CSS class
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