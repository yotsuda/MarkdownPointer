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

// Zoom Mermaid diagram content within its scroll container
function zoomMermaidDiagram(container, direction) {
    var mermaidEl = container.querySelector('.mermaid');
    var svg = mermaidEl ? mermaidEl.querySelector('svg') : null;
    if (!svg) return;
    // Store original SVG dimensions on first zoom
    if (!container.dataset.origW) {
        container.dataset.origW = svg.getBoundingClientRect().width;
        container.dataset.origH = svg.getBoundingClientRect().height;
    }
    var current = parseFloat(mermaidEl.dataset.zoom || '1');
    var factor = direction === 'in' ? 1.1 : 1 / 1.1;
    var newZoom = Math.max(0.25, Math.min(5, current * factor));
    mermaidEl.dataset.zoom = newZoom;
    var origW = parseFloat(container.dataset.origW);
    var origH = parseFloat(container.dataset.origH);
    var newW = origW * newZoom;
    var newH = origH * newZoom;
    svg.style.width = newW + 'px';
    svg.style.height = newH + 'px';
    svg.style.minWidth = newW + 'px';
    svg.style.maxWidth = 'none';
}

// Handle Ctrl+wheel for zoom
document.addEventListener('wheel', function(e) {
    if (e.ctrlKey) {
        e.preventDefault();
        var el = e.target;
        while (el && !el.classList.contains('mermaid-scroll-container')) el = el.parentElement;
        if (el) {
            zoomMermaidDiagram(el, e.deltaY < 0 ? 'in' : 'out');
        } else {
            window.chrome.webview.postMessage('zoom:' + (e.deltaY < 0 ? 'in' : 'out'));
        }
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