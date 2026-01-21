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