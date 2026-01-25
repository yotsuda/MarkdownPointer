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