function processMermaidNodes() {
    document.querySelectorAll('.mermaid').forEach(function(container) {
        var source = container.getAttribute('data-mermaid-source') || '';
        var baseLine = parseInt(container.getAttribute('data-line') || '0', 10);
        var sourceLines = source.split('\n');
        var svg = container.querySelector('svg');
        if (!svg) return;

        var nodeLineMap = {};
        var arrowLineMap = {};
        var messageLineNums = [];
        var edgeLabelLineMap = {};

        // Parse source for line mappings
        parseSourceLines(sourceLines, baseLine, nodeLineMap, arrowLineMap, messageLineNums, edgeLabelLineMap);

        // Apply mappings to SVG elements
        applyMappingsToSvg(svg, nodeLineMap, arrowLineMap, messageLineNums, edgeLabelLineMap);
    });
}

function parseSourceLines(sourceLines, baseLine, nodeLineMap, arrowLineMap, messageLineNums, edgeLabelLineMap) {
    // Detect diagram type from first line
    var firstLine = sourceLines[0] ? sourceLines[0].trim().toLowerCase() : '';
    var diagramType = 'unknown';
    if (firstLine.indexOf('graph') === 0 || firstLine.indexOf('flowchart') === 0) diagramType = 'flowchart';
    else if (firstLine.indexOf('sequencediagram') === 0) diagramType = 'sequence';
    else if (firstLine.indexOf('classdiagram') === 0) diagramType = 'class';
    else if (firstLine.indexOf('statediagram') === 0) diagramType = 'state';
    else if (firstLine.indexOf('erdiagram') === 0) diagramType = 'er';
    else if (firstLine.indexOf('gantt') === 0) diagramType = 'gantt';
    else if (firstLine.indexOf('pie') === 0) diagramType = 'pie';
    else if (firstLine.indexOf('gitgraph') === 0) diagramType = 'git';
    else if (firstLine.indexOf('mindmap') === 0) diagramType = 'mindmap';

    for (var i = 0; i < sourceLines.length; i++) {
        var line = sourceLines[i];
        var lineNum = baseLine + i + 1;

        // Flowchart node
        var nodeMatch = line.match(/^\s*([^\s\[\{\(]+)\s*[\[\{\(]/);
        if (nodeMatch && !nodeLineMap[nodeMatch[1]]) {
            nodeLineMap[nodeMatch[1]] = lineNum;
        }

        // Flowchart subgraph
        var subgraphMatch = line.match(/^\s*subgraph\s+(\S+)/);
        if (subgraphMatch) {
            nodeLineMap['subgraph:' + subgraphMatch[1]] = lineNum;
        }

        // Flowchart arrow
        var arrowMatch = line.match(/^\s*([^\s\[\{\(-]+)[^\-]*--[->](\|[^|]*\|)?\s*([^\s\[\{\(]+)/);
        if (arrowMatch) {
            var key = arrowMatch[1] + '-' + arrowMatch[3];
            arrowLineMap[key] = lineNum;
            if (arrowMatch[2]) {
                var label = arrowMatch[2].replace(/\|/g, '');
                edgeLabelLineMap[label] = lineNum;
            }
            // Also register the target node
            if (!nodeLineMap[arrowMatch[3]]) {
                nodeLineMap[arrowMatch[3]] = lineNum;
            }
        }

        // Sequence diagram message - also build text-to-line mapping
        if (/->>|-->|->/.test(line) && line.indexOf(':') !== -1) {
            messageLineNums.push(lineNum);
            var colonIdx = line.indexOf(':');
            var msgText = line.substring(colonIdx + 1).trim();
            if (msgText && !edgeLabelLineMap['seq:' + msgText]) {
                edgeLabelLineMap['seq:' + msgText] = lineNum;
            }
        }

        // Sequence participant/actor - handle "participant A as Alice" format
        var actorMatch = line.match(/^\s*(participant|actor)\s+(\S+)(?:\s+as\s+(.+))?/);
        if (actorMatch) {
            var actorId = actorMatch[2];
            var actorAlias = actorMatch[3] ? actorMatch[3].trim() : actorId;
            if (!nodeLineMap[actorId]) {
                nodeLineMap[actorId] = lineNum;
            }
            if (actorAlias !== actorId && !nodeLineMap[actorAlias]) {
                nodeLineMap[actorAlias] = lineNum;
            }
        }

        // Sequence implicit actor (from message lines like Alice->>Bob: Hello)
        var seqMatch = line.match(/^\s*([^\s-]+)\s*-/);
        if (seqMatch && !nodeLineMap[seqMatch[1]]) {
            nodeLineMap[seqMatch[1]] = lineNum;
        }

        // Class diagram, state diagram, ER diagram, Gantt, Pie, Git graph patterns
        // (Additional pattern matching for various Mermaid diagram types)
        parseAdditionalPatterns(line, lineNum, nodeLineMap, arrowLineMap, edgeLabelLineMap, diagramType);
    }
}

function parseAdditionalPatterns(line, lineNum, nodeLineMap, arrowLineMap, edgeLabelLineMap, diagramType) {
    // Class diagram relationships
    var classRelPatterns = [
        { regex: /^\s*(\S+)\s*(<\|--|&lt;\|--)\s*(\S+)/, type: 'extends', swap: true, g1: 1, g2: 3 },
        { regex: /^\s*(\S+)\s*(--\|>|--\|&gt;)\s*(\S+)/, type: 'extends', swap: false, g1: 1, g2: 3 },
        { regex: /^\s*(\S+)\s*\*--\s*(\S+)/, type: 'composition', swap: false, g1: 1, g2: 2 },
        { regex: /^\s*(\S+)\s*--\*\s*(\S+)/, type: 'composition', swap: true, g1: 1, g2: 2 },
        { regex: /^\s*(\S+)\s*o--\s*(\S+)/, type: 'aggregation', swap: false, g1: 1, g2: 2 },
        { regex: /^\s*(\S+)\s*--o\s*(\S+)/, type: 'aggregation', swap: true, g1: 1, g2: 2 },
        { regex: /^\s*(\S+)\s*-->\s*(\S+)/, type: 'association', swap: false, g1: 1, g2: 2 },
        { regex: /^\s*(\S+)\s*<--\s*(\S+)/, type: 'association', swap: true, g1: 1, g2: 2 }
    ];

    for (var pi = 0; pi < classRelPatterns.length; pi++) {
        var p = classRelPatterns[pi];
        var m = line.match(p.regex);
        if (m) {
            var class1 = m[p.g1];
            var class2 = m[p.g2];
            if (!nodeLineMap['class:' + class1]) nodeLineMap['class:' + class1] = lineNum;
            if (!nodeLineMap['class:' + class2]) nodeLineMap['class:' + class2] = lineNum;
            var from = p.swap ? class2 : class1;
            var to = p.swap ? class1 : class2;
            arrowLineMap['class-rel:' + class1 + '_' + class2] = lineNum + ':' + p.type + ':' + from + ':' + to;
            break;
        }
    }

    // Class diagram class name from member definition: ClassName : member
    var classNameMatch = line.match(/^\s*(\S+)\s*:\s*.+$/);
    if (classNameMatch) {
        if (!nodeLineMap['class:' + classNameMatch[1]]) nodeLineMap['class:' + classNameMatch[1]] = lineNum;
    }

    // Class diagram member/method: ClassName : +memberName or ClassName: +methodName()
    if (diagramType === 'class') {
        var classMemberMatch = line.match(/^\s*(\S+)\s*:\s*(.+)$/);
        if (classMemberMatch) {
            if (!nodeLineMap['class-member-lines']) nodeLineMap['class-member-lines'] = [];
            nodeLineMap['class-member-lines'].push(lineNum);
        }
    }

    // State diagram transition with optional label
    var stateTransMatch = line.match(/^\s*(\[\*\]|[^\s-]+)\s*-->\s*(\[\*\]|[^\s:]+)(?:\s*:\s*(.+))?/);
    if (stateTransMatch) {
        arrowLineMap[stateTransMatch[1] + '->' + stateTransMatch[2]] = lineNum;
        // Register transition label
        if (stateTransMatch[3]) {
            edgeLabelLineMap['state:' + stateTransMatch[3].trim()] = lineNum;
        }
        // Also register state names
        if (stateTransMatch[1] !== '[*]' && !nodeLineMap['state:' + stateTransMatch[1]]) {
            nodeLineMap['state:' + stateTransMatch[1]] = lineNum;
        }
        if (stateTransMatch[2] !== '[*]' && !nodeLineMap['state:' + stateTransMatch[2]]) {
            nodeLineMap['state:' + stateTransMatch[2]] = lineNum;
        }
    }
    // ER diagram relationship: ENTITY1 ||--o{ ENTITY2 : label
    var erRelMatch = line.match(/^\s*([^\s\|\}o]+)\s*(\||\}|o).*(\||\{|o)\s*([^\s\|\{o:]+)\s*:\s*(.+)$/);
    if (erRelMatch) {
        var erKey = erRelMatch[1] + '-' + erRelMatch[4];
        arrowLineMap[erKey] = lineNum;
        // Map the label to edgeLabelLineMap
        var erLabel = erRelMatch[5].trim();
        edgeLabelLineMap['er:' + erLabel] = lineNum;
        // Map entity names from relationship (for entities without explicit definition)
        if (!nodeLineMap['errel:' + erRelMatch[1]]) nodeLineMap['errel:' + erRelMatch[1]] = lineNum;
        if (!nodeLineMap['errel:' + erRelMatch[4]]) nodeLineMap['errel:' + erRelMatch[4]] = lineNum;
    }

    // ER diagram entity definition: ENTITY {
    var erEntityMatch = line.match(/^\s*([^\s\{]+)\s*\{/);
    if (erEntityMatch && !nodeLineMap['entity:' + erEntityMatch[1]]) {
        nodeLineMap['entity:' + erEntityMatch[1]] = lineNum;
    }

    // ER diagram attribute: type name (inside entity block)
    if (diagramType === 'er') {
        var erAttrMatch = line.match(/^\s+(\S+)\s+(\S+)\s*$/);
        if (erAttrMatch) {
            // Store attribute with type and name
            edgeLabelLineMap['er-attr:' + erAttrMatch[1] + ':' + erAttrMatch[2]] = lineNum;
            edgeLabelLineMap['er-attr-name:' + erAttrMatch[2]] = lineNum;
        }
    }

    // Gantt task: TaskName :... (any task line with colon)
    if (diagramType === 'gantt') {
        // Match task lines: "Task Name :something" (but not section/title/dateFormat)
        var ganttTaskMatch = line.match(/^\s*([^:]+?)\s*:/);
        if (ganttTaskMatch) {
            var taskName = ganttTaskMatch[1].trim();
            // Exclude keywords
            if (taskName !== 'section' && taskName !== 'title' && taskName !== 'dateFormat' && 
                taskName !== 'axisFormat' && taskName !== 'excludes') {
                if (!nodeLineMap['gantt-task-lines']) nodeLineMap['gantt-task-lines'] = [];
                nodeLineMap['gantt-task-lines'].push(lineNum);
            }
        }
    }

    // Gantt section: section SectionName
    var ganttSectionMatch = line.match(/^\s*section\s+(.+)$/);
    if (ganttSectionMatch) {
        nodeLineMap['gantt-section:' + ganttSectionMatch[1].trim()] = lineNum;
    }

    // Gantt title: title TitleText
    var ganttTitleMatch = line.match(/^\s*title\s+(.+)$/);
    if (ganttTitleMatch) {
        nodeLineMap['gantt-title:' + ganttTitleMatch[1].trim()] = lineNum;
    }

    // Pie chart slice: "Label" : value (index-based for duplicate labels)
    if (diagramType === 'pie') {
        var pieSliceMatch = line.match(/^\s*"([^"]+)"\s*:\s*(\d+)/);
        if (pieSliceMatch) {
            if (!nodeLineMap['pie-slice-lines']) nodeLineMap['pie-slice-lines'] = [];
            nodeLineMap['pie-slice-lines'].push(lineNum);
        }
    }

    // Pie chart title: pie title TitleText
    var pieTitleMatch = line.match(/^\s*pie\s+title\s+(.+)$/);
    if (pieTitleMatch) {
        nodeLineMap['pie-title:' + pieTitleMatch[1].trim()] = lineNum;
    }

    // Git graph commit: commit or commit id: "label"
    var gitCommitMatch = line.match(/^\s*commit(\s+id:\s*"([^"]+)")?/);
    if (gitCommitMatch) {
        if (!nodeLineMap['git-commit-count']) nodeLineMap['git-commit-count'] = 0;
        nodeLineMap['git-commit:' + nodeLineMap['git-commit-count']] = lineNum;
        if (gitCommitMatch[2]) nodeLineMap['git-label:' + gitCommitMatch[2]] = lineNum;
        nodeLineMap['git-commit-count']++;
    }

    // Git graph branch: branch BranchName
    var gitBranchMatch = line.match(/^\s*branch\s+(\S+)/);
    if (gitBranchMatch) {
        nodeLineMap['git-branch:' + gitBranchMatch[1]] = lineNum;
    }

    // Git graph merge: merge BranchName (also creates a commit circle)
    var gitMergeMatch = line.match(/^\s*merge\s+(\S+)/);
    if (gitMergeMatch) {
        if (!nodeLineMap['git-commit-count']) nodeLineMap['git-commit-count'] = 0;
        nodeLineMap['git-commit:' + nodeLineMap['git-commit-count']] = lineNum;
        nodeLineMap['git-commit-count']++;
    }

    // Mindmap: collect line numbers in order (index-based for duplicate labels)
    if (diagramType === 'mindmap') {
        if (!nodeLineMap['mindmap-lines']) nodeLineMap['mindmap-lines'] = [];
        // root node
        var mindmapRootMatch = line.match(/^\s*root\s*\(\((.+)\)\)/);
        if (mindmapRootMatch) {
            nodeLineMap['mindmap-lines'].push(lineNum);
        }
        // leaf nodes (indented text)
        var mindmapNodeMatch = line.match(/^\s{2,}(\S+)\s*$/);
        if (mindmapNodeMatch && !line.includes('root')) {
            nodeLineMap['mindmap-lines'].push(lineNum);
        }
    }
}

function applyMappingsToSvg(svg, nodeLineMap, arrowLineMap, messageLineNums, edgeLabelLineMap) {
    // Mark flowchart nodes, sequence actors, class diagram nodes, etc.
    svg.querySelectorAll('g.node, g.cluster, g.edgeLabel, g[id^="state-"], g[id^="root-"], g.note, g.activation').forEach(function(node) {
        node.style.cursor = 'pointer';
        node.setAttribute('data-mermaid-node', 'true');

        var nodeId = node.id || '';

        // Flowchart node: flowchart-NodeName-0
        var flowMatch = nodeId.match(/^flowchart-([^-]+)-/);
        if (flowMatch && nodeLineMap[flowMatch[1]]) {
            node.setAttribute('data-source-line', String(nodeLineMap[flowMatch[1]]));
            return;
        }

        // Flowchart subgraph (cluster)
        if (node.classList && node.classList.contains('cluster')) {
            var clusterLabel = node.querySelector('.nodeLabel, text');
            if (clusterLabel) {
                var subgraphName = clusterLabel.textContent.trim();
                if (nodeLineMap['subgraph:' + subgraphName]) {
                    node.setAttribute('data-source-line', String(nodeLineMap['subgraph:' + subgraphName]));
                    return;
                }
            }
        }

        // Class diagram node: classId-ClassName-0
        var classMatch = nodeId.match(/^classId-([^-]+)-/);
        if (classMatch) {
            var className = classMatch[1];
            if (nodeLineMap['class:' + className]) {
                node.setAttribute('data-source-line', String(nodeLineMap['class:' + className]));
            }
            return;
        }
        // State diagram node: state-StateName-N
        var stateMatch = nodeId.match(/^state-([^-]+)-/);
        if (stateMatch) {
            var stateName = stateMatch[1];
            if (stateName === 'root_start') {
                node.setAttribute('data-state-node', '[*] (start)');
            } else if (stateName === 'root_end') {
                node.setAttribute('data-state-node', '[*] (end)');
            } else {
                node.setAttribute('data-state-node', stateName);
                if (nodeLineMap['state:' + stateName]) {
                    node.setAttribute('data-source-line', String(nodeLineMap['state:' + stateName]));
                }
            }
            return;
        }

        // Flowchart/State/ER diagram edge label
        if (node.classList.contains('edgeLabel')) {
            var labelText = node.textContent.trim();
            // Try direct lookup (flowchart) first, then state/ER diagram prefix
            var sourceLine = edgeLabelLineMap[labelText] || edgeLabelLineMap['state:' + labelText] || edgeLabelLineMap['er:' + labelText];
            if (sourceLine) {
                node.setAttribute('data-source-line', String(sourceLine));
            }
            return;
        }

        // Sequence actor container: root-ActorName
        if (nodeId.indexOf('root-') === 0) {
            var actorText = node.querySelector('text.actor');
            if (actorText) {
                var actorName = actorText.textContent.trim();
                if (nodeLineMap[actorName]) {
                    node.setAttribute('data-source-line', String(nodeLineMap[actorName]));
                }
            }
            return;
        }
    });

    // Sequence diagram: mark bottom actors
    svg.querySelectorAll('rect.actor-bottom').forEach(function(rect) {
        var parent = rect.parentElement;
        if (parent && parent.tagName.toLowerCase() === 'g') {
            parent.style.cursor = 'pointer';
            parent.setAttribute('data-mermaid-node', 'true');
            var textEl = parent.querySelector('text.actor');
            if (textEl) {
                var actorName = textEl.textContent.trim();
                if (nodeLineMap[actorName]) {
                    parent.setAttribute('data-source-line', String(nodeLineMap[actorName]));
                }
            }
        }
    });

    // Sequence messages
    var msgIdx = 0;
    svg.querySelectorAll('text.messageText').forEach(function(msg) {
        msg.style.cursor = 'pointer';
        msg.setAttribute('data-mermaid-node', 'true');
        if (msgIdx < messageLineNums.length) {
            msg.setAttribute('data-source-line', String(messageLineNums[msgIdx]));
        }
        msgIdx++;
    });

    // Sequence diagram arrows (message lines) with hit areas
    svg.querySelectorAll('line.messageLine0, line.messageLine1').forEach(function(line) {
        // Get message text from previous sibling
        var msgText = '';
        var prev = line.previousElementSibling;
        if (prev && prev.classList && prev.classList.contains('messageText')) {
            msgText = prev.textContent.trim();
        }

        // Look up line number by message text
        var sourceLine = edgeLabelLineMap['seq:' + msgText] ? String(edgeLabelLineMap['seq:' + msgText]) : null;

        // Create hit area for easier clicking
        var bbox = line.getBBox();
        var minSize = 16;
        var rectW = Math.max(bbox.width, minSize);
        var rectH = Math.max(bbox.height, minSize);
        var rectX = bbox.x - (rectW - bbox.width) / 2;
        var rectY = bbox.y - (rectH - bbox.height) / 2;

        var hitRect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
        hitRect.setAttribute('x', rectX);
        hitRect.setAttribute('y', rectY);
        hitRect.setAttribute('width', rectW);
        hitRect.setAttribute('height', rectH);
        hitRect.setAttribute('fill', 'transparent');
        hitRect.style.cursor = 'pointer';
        hitRect.setAttribute('data-mermaid-node', 'true');
        hitRect.setAttribute('data-seq-arrow-text', msgText);
        if (sourceLine) {
            hitRect.setAttribute('data-source-line', sourceLine);
        }
        line.parentNode.insertBefore(hitRect, line.nextSibling);

        line.style.cursor = 'pointer';
        line.setAttribute('data-mermaid-node', 'true');
        if (sourceLine) {
            line.setAttribute('data-source-line', sourceLine);
        }

    });

    // Flowchart arrows with hit areas
    svg.querySelectorAll('path.flowchart-link').forEach(function(path) {
        var pathId = path.id || '';
        var linkMatch = pathId.match(/^L[-_]([^-_]+)[-_]([^-_]+)[-_]/);
        var sourceLine = null;
        if (linkMatch && arrowLineMap[linkMatch[1] + '-' + linkMatch[2]]) {
            sourceLine = String(arrowLineMap[linkMatch[1] + '-' + linkMatch[2]]);
        }

        createHitArea(path, sourceLine, 'data-hit-area-for', pathId);
        path.setAttribute('data-mermaid-node', 'true');
        if (sourceLine) path.setAttribute('data-source-line', sourceLine);
    });

    // Class diagram class names
    svg.querySelectorAll('g.label-group g.label').forEach(function(label) {
        var className = label.textContent.trim();
        if (nodeLineMap['class:' + className]) {
            label.style.cursor = 'pointer';
            label.setAttribute('data-mermaid-node', 'true');
            label.setAttribute('data-class-name', className);
            label.setAttribute('data-source-line', String(nodeLineMap['class:' + className]));
        }
    });

    // Class diagram members and methods (index-based for duplicate member names)
    var classMemberLines = nodeLineMap['class-member-lines'] || [];
    svg.querySelectorAll('g.members-group g.label, g.methods-group g.label').forEach(function(label, idx) {
        label.style.cursor = 'pointer';
        label.setAttribute('data-mermaid-node', 'true');
        label.setAttribute('data-class-member', 'true');
        var memberText = label.textContent.trim();
        if (classMemberLines[idx]) {
            label.setAttribute('data-source-line', String(classMemberLines[idx]));
        }
    });

    // Class diagram relations (inheritance, composition, etc.)
    svg.querySelectorAll('path.relation').forEach(function(path) {
        var pathId = path.id || '';
        var relMatch = pathId.match(/^id_([^_]+)_([^_]+)_\d+$/);
        var relKey = relMatch ? 'class-rel:' + relMatch[1] + '_' + relMatch[2] : '';
        var relInfo = arrowLineMap[relKey];
        var sourceLine = null;
        var relText = '';

        if (relInfo) {
            var parts = relInfo.split(':');
            sourceLine = parts[0];
            var relType = parts[1];
            var fromClass = parts[2];
            var toClass = parts[3];
            var typeLabels = {
                'extends': ' extends ',
                'composition': ' *-- ',
                'aggregation': ' o-- ',
                'association': ' --> ',
                'dependency': ' ..> ',
                'realization': ' implements '
            };
            relText = fromClass + (typeLabels[relType] || ' -- ') + toClass;
        }

        // Create hit area including marker
        var totalLen = path.getTotalLength();
        var startPoint = path.getPointAtLength(0);
        var endPoint = path.getPointAtLength(totalLen);
        var markerStart = path.getAttribute('marker-start') || '';
        var markerEnd = path.getAttribute('marker-end') || '';
        var markerLen = 18;
        var tipX, tipY;

        if (markerStart && markerStart !== 'none') {
            var nearStart = path.getPointAtLength(Math.min(1, totalLen));
            var dx = startPoint.x - nearStart.x;
            var dy = startPoint.y - nearStart.y;
            var len = Math.sqrt(dx * dx + dy * dy);
            if (len > 0) { dx /= len; dy /= len; }
            tipX = startPoint.x + dx * markerLen;
            tipY = startPoint.y + dy * markerLen;
        } else if (markerEnd && markerEnd !== 'none') {
            var nearEnd = path.getPointAtLength(Math.max(0, totalLen - 1));
            var dx = endPoint.x - nearEnd.x;
            var dy = endPoint.y - nearEnd.y;
            var len = Math.sqrt(dx * dx + dy * dy);
            if (len > 0) { dx /= len; dy /= len; }
            tipX = endPoint.x + dx * markerLen;
            tipY = endPoint.y + dy * markerLen;
        } else {
            tipX = startPoint.x;
            tipY = startPoint.y;
        }

        var minX = Math.min(startPoint.x, endPoint.x, tipX);
        var maxX = Math.max(startPoint.x, endPoint.x, tipX);
        var minY = Math.min(startPoint.y, endPoint.y, tipY);
        var maxY = Math.max(startPoint.y, endPoint.y, tipY);
        var rectWidth = maxX - minX;
        var rectX = minX;
        if (rectWidth < 16) {
            rectX = minX - (16 - rectWidth) / 2;
            rectWidth = 16;
        }

        var hitRect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
        hitRect.setAttribute('x', rectX);
        hitRect.setAttribute('y', minY);
        hitRect.setAttribute('width', rectWidth);
        hitRect.setAttribute('height', maxY - minY);
        hitRect.setAttribute('fill', 'transparent');
        hitRect.style.cursor = 'pointer';
        hitRect.setAttribute('data-mermaid-node', 'true');
        hitRect.setAttribute('data-class-relation', relText);
        if (sourceLine) hitRect.setAttribute('data-source-line', String(sourceLine));
        path.parentNode.insertBefore(hitRect, path.nextSibling);

        path.style.cursor = 'pointer';
        path.setAttribute('data-mermaid-node', 'true');
        path.setAttribute('data-class-relation', relText);
        if (sourceLine) path.setAttribute('data-source-line', String(sourceLine));
    });

    // State diagram transitions
    var stateTransKeys = Object.keys(arrowLineMap).filter(function(k) { return k.indexOf('->') !== -1; });
    var transIdx = 0;
    svg.querySelectorAll('path.transition').forEach(function(path) {
        var sourceLine = null;
        var transKey = stateTransKeys[transIdx] || '';
        if (transKey && arrowLineMap[transKey]) {
            sourceLine = String(arrowLineMap[transKey]);
        }
        createHitArea(path, sourceLine, 'data-state-transition', transKey);
        path.style.cursor = 'pointer';
        path.setAttribute('data-mermaid-node', 'true');
        path.setAttribute('data-state-transition', transKey);
        if (sourceLine) path.setAttribute('data-source-line', sourceLine);
        transIdx++;
    });

    // ER diagram: entity names
    svg.querySelectorAll('g.entityLabel, g.label.name').forEach(function(label) {
        var entityName = label.textContent.trim();
        label.style.cursor = 'pointer';
        label.setAttribute('data-mermaid-node', 'true');
        label.setAttribute('data-er-entity', entityName);
        if (nodeLineMap['entity:' + entityName]) {
            label.setAttribute('data-source-line', String(nodeLineMap['entity:' + entityName]));
        } else if (nodeLineMap['errel:' + entityName]) {
            // Use relationship line for entities without explicit definition
            label.setAttribute('data-source-line', String(nodeLineMap['errel:' + entityName]));
        }
    });

    // ER diagram: relationship lines
    svg.querySelectorAll('path.relationshipLine').forEach(function(path) {
        var pathId = path.id || '';
        // Extract entity names from id like 'id_entity-CUSTOMER-0_entity-ORDER-1_0'
        var relMatch = pathId.match(/entity-([A-Za-z0-9_-]+)-\d+_entity-([A-Za-z0-9_-]+)-/);
        var sourceLine = null;
        var relText = '';

        if (relMatch) {
            var entity1 = relMatch[1];
            var entity2 = relMatch[2];
            relText = entity1 + ' -- ' + entity2;
            var key = entity1 + '-' + entity2;
            if (arrowLineMap[key]) {
                sourceLine = String(arrowLineMap[key]);
            } else {
                // Try reverse order
                key = entity2 + '-' + entity1;
                if (arrowLineMap[key]) {
                    sourceLine = String(arrowLineMap[key]);
                }
            }
        }

        // Create hit area
        var bbox = path.getBBox();
        var minSize = 16;
        var rectW = Math.max(bbox.width, minSize);
        var rectH = Math.max(bbox.height, minSize);
        var rectX = bbox.x - (rectW - bbox.width) / 2;
        var rectY = bbox.y - (rectH - bbox.height) / 2;

        var hitRect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
        hitRect.setAttribute('x', rectX);
        hitRect.setAttribute('y', rectY);
        hitRect.setAttribute('width', rectW);
        hitRect.setAttribute('height', rectH);
        hitRect.setAttribute('fill', 'transparent');
        hitRect.style.cursor = 'pointer';
        hitRect.setAttribute('data-mermaid-node', 'true');
        hitRect.setAttribute('data-er-relation', relText);
        if (sourceLine) {
            hitRect.setAttribute('data-source-line', sourceLine);
        }
        path.parentNode.insertBefore(hitRect, path.nextSibling);

        path.style.cursor = 'pointer';
        path.setAttribute('data-mermaid-node', 'true');
        if (sourceLine) {
            path.setAttribute('data-source-line', sourceLine);
        }
    });

    // ER diagram: attributes
    svg.querySelectorAll('g.label.attribute-name, g.label.attribute-type').forEach(function(label) {
        var attrText = label.textContent.trim();
        var isType = label.classList.contains('attribute-type');
        label.style.cursor = 'pointer';
        label.setAttribute('data-mermaid-node', 'true');
        label.setAttribute('data-er-attr', attrText);

        // For attribute-type, get line from next sibling (attribute-name)
        if (isType) {
            var nextSib = label.nextElementSibling;
            if (nextSib && nextSib.classList.contains('attribute-name')) {
                var nameText = nextSib.textContent.trim();
                var sourceLine = edgeLabelLineMap['er-attr-name:' + nameText];
                if (sourceLine) {
                    label.setAttribute('data-source-line', String(sourceLine));
                }
            }
        } else {
            // attribute-name
            var sourceLine = edgeLabelLineMap['er-attr-name:' + attrText];
            if (sourceLine) {
                label.setAttribute('data-source-line', String(sourceLine));
            }
        }
    });

    // Gantt: tasks (index-based for duplicate task names)
    var ganttTaskLines = nodeLineMap['gantt-task-lines'] || [];
    svg.querySelectorAll('rect.task').forEach(function(task, idx) {
        var taskId = task.id || '';
        task.style.cursor = 'pointer';
        task.setAttribute('data-mermaid-node', 'true');
        task.setAttribute('data-gantt-task', taskId);
        if (ganttTaskLines[idx]) {
            task.setAttribute('data-source-line', String(ganttTaskLines[idx]));
        }
    });

    // Gantt: task text (index-based for duplicate task names)
    svg.querySelectorAll('text.taskText').forEach(function(text, idx) {
        var taskName = text.textContent.trim();
        text.style.cursor = 'pointer';
        text.setAttribute('data-mermaid-node', 'true');
        text.setAttribute('data-gantt-task-name', taskName);
        if (ganttTaskLines[idx]) {
            text.setAttribute('data-source-line', String(ganttTaskLines[idx]));
        }
    });

    // Gantt: section titles
    svg.querySelectorAll('text.sectionTitle').forEach(function(text) {
        var sectionName = text.textContent.trim();
        text.style.cursor = 'pointer';
        text.setAttribute('data-mermaid-node', 'true');
        text.setAttribute('data-gantt-section', sectionName);
        if (nodeLineMap['gantt-section:' + sectionName]) {
            text.setAttribute('data-source-line', String(nodeLineMap['gantt-section:' + sectionName]));
        }
    });

    // Gantt: title
    svg.querySelectorAll('text.titleText').forEach(function(text) {
        var titleText = text.textContent.trim();
        text.style.cursor = 'pointer';
        text.setAttribute('data-mermaid-node', 'true');
        text.setAttribute('data-gantt-title', titleText);
        if (nodeLineMap['gantt-title:' + titleText]) {
            text.setAttribute('data-source-line', String(nodeLineMap['gantt-title:' + titleText]));
        }
    });

    // Pie chart: slices (index-based for duplicate labels)
    var pieSliceLines = nodeLineMap['pie-slice-lines'] || [];
    var pieLegends = svg.querySelectorAll('g.legend text');
    svg.querySelectorAll('path.pieCircle, .pieCircle').forEach(function(slice, idx) {
        slice.style.cursor = 'pointer';
        slice.setAttribute('data-mermaid-node', 'true');
        var legendText = pieLegends[idx] ? pieLegends[idx].textContent.trim() : '';
        slice.setAttribute('data-pie-slice', legendText);
        if (pieSliceLines[idx]) {
            slice.setAttribute('data-source-line', String(pieSliceLines[idx]));
        }
    });

    // Pie chart: legend (index-based for duplicate labels)
    svg.querySelectorAll('g.legend').forEach(function(legend, idx) {
        var legendText = legend.textContent.trim();
        legend.style.cursor = 'pointer';
        legend.setAttribute('data-mermaid-node', 'true');
        legend.setAttribute('data-pie-legend', legendText);
        if (pieSliceLines[idx]) {
            legend.setAttribute('data-source-line', String(pieSliceLines[idx]));
        }
    });

    // Pie chart: title
    svg.querySelectorAll('text.pieTitleText').forEach(function(text) {
        var titleText = text.textContent.trim();
        text.style.cursor = 'pointer';
        text.setAttribute('data-mermaid-node', 'true');
        text.setAttribute('data-pie-title', titleText);
        if (nodeLineMap['pie-title:' + titleText]) {
            text.setAttribute('data-source-line', String(nodeLineMap['pie-title:' + titleText]));
        }
    });

    // Git graph: commits (excluding merge decorations)
    var gitCommitIdx = 0;
    svg.querySelectorAll('circle.commit:not(.commit-merge)').forEach(function(commit) {
        commit.style.cursor = 'pointer';
        commit.setAttribute('data-mermaid-node', 'true');
        commit.setAttribute('data-git-commit', String(gitCommitIdx));
        if (nodeLineMap['git-commit:' + gitCommitIdx]) {
            commit.setAttribute('data-source-line', String(nodeLineMap['git-commit:' + gitCommitIdx]));
        }
        gitCommitIdx++;
    });

    // Git graph: merge decorations (same line as previous commit)
    svg.querySelectorAll('circle.commit-merge').forEach(function(merge) {
        merge.style.cursor = 'pointer';
        merge.setAttribute('data-mermaid-node', 'true');
        var prevSibling = merge.previousElementSibling;
        if (prevSibling && prevSibling.hasAttribute('data-source-line')) {
            merge.setAttribute('data-source-line', prevSibling.getAttribute('data-source-line'));
            merge.setAttribute('data-git-commit', prevSibling.getAttribute('data-git-commit'));
        }
    });

    // Git graph: commit labels
    svg.querySelectorAll('text.commit-label').forEach(function(text) {
        var labelText = text.textContent.trim();
        text.style.cursor = 'pointer';
        text.setAttribute('data-mermaid-node', 'true');
        text.setAttribute('data-git-label', labelText);
        if (nodeLineMap['git-label:' + labelText]) {
            text.setAttribute('data-source-line', String(nodeLineMap['git-label:' + labelText]));
        }
    });

    // Git graph: branch labels
    svg.querySelectorAll('g.branchLabel').forEach(function(branch) {
        var branchName = branch.textContent.trim();
        branch.style.cursor = 'pointer';
        branch.setAttribute('data-mermaid-node', 'true');
        branch.setAttribute('data-git-branch', branchName);
        if (nodeLineMap['git-branch:' + branchName]) {
            branch.setAttribute('data-source-line', String(nodeLineMap['git-branch:' + branchName]));
        }
    });

    // Mindmap: nodes (index-based for duplicate labels)
    var mindmapLines = nodeLineMap['mindmap-lines'] || [];
    svg.querySelectorAll('g.mindmap-node, g.node.mindmap-node').forEach(function(node, idx) {
        var labelEl = node.querySelector('.nodeLabel');
        if (labelEl) {
            var nodeText = labelEl.textContent.trim();
            node.style.cursor = 'pointer';
            node.setAttribute('data-mermaid-node', 'true');
            node.setAttribute('data-mindmap-node', nodeText);
            if (mindmapLines[idx]) {
                node.setAttribute('data-source-line', String(mindmapLines[idx]));
            }
        }
    });
}

function createHitArea(element, sourceLine, dataAttr, dataValue) {
    var bbox = element.getBBox();
    var minSize = 16;
    var rectW = Math.max(bbox.width, minSize);
    var rectH = Math.max(bbox.height, minSize);
    var rectX = bbox.x - (rectW - bbox.width) / 2;
    var rectY = bbox.y - (rectH - bbox.height) / 2;

    var hitRect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
    hitRect.setAttribute('x', rectX);
    hitRect.setAttribute('y', rectY);
    hitRect.setAttribute('width', rectW);
    hitRect.setAttribute('height', rectH);
    hitRect.setAttribute('fill', 'transparent');
    hitRect.style.cursor = 'pointer';
    hitRect.setAttribute('data-mermaid-node', 'true');
    if (dataAttr && dataValue) hitRect.setAttribute(dataAttr, dataValue);
    if (sourceLine) hitRect.setAttribute('data-source-line', sourceLine);

    element.parentNode.insertBefore(hitRect, element.nextSibling);
}