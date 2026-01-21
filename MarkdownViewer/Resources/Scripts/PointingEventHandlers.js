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