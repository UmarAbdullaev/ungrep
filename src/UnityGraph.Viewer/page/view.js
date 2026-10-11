// The `ug view` page (SPEC-viewer, Page). Reads the baked neighbourhood from #ug-data and draws it
// with Cytoscape.js. Containment is nesting (a node's `parent`), every other relation an edge.
(function () {
  'use strict';

  const data = JSON.parse(document.getElementById('ug-data').textContent);
  const nodes = data.nodes;
  const edges = data.edges;
  const more = data.more;

  const GROUPS = [
    { id: 'type', name: 'Type', shape: 'ellipse', color: '#0072B2' },
    { id: 'method', name: 'Method', shape: 'ellipse', color: '#56B4E9' },
    { id: 'member', name: 'Field, property, event', shape: 'ellipse', color: '#009E73' },
    { id: 'script', name: 'Script', shape: 'rectangle', color: '#F0E442' },
    { id: 'scene', name: 'Scene', shape: 'rectangle', color: '#E69F00' },
    { id: 'prefab', name: 'Prefab, prefab instance', shape: 'rectangle', color: '#D55E00' },
    { id: 'so', name: 'ScriptableObject, sub-asset', shape: 'rectangle', color: '#CC79A7' },
    { id: 'asset', name: 'Other asset', shape: 'rectangle', color: '#999999' },
    { id: 'object', name: 'Game object', shape: 'round-rectangle', color: '#999999' },
    { id: 'component', name: 'Component', shape: 'round-rectangle', color: '#009E73' },
  ];
  const FAMILIES = [
    { id: 'calls', name: 'Calls, dispatches', color: '#0072B2' },
    { id: 'references', name: 'References, uses type', color: '#E69F00' },
    { id: 'hierarchy', name: 'Inherits, overrides', color: '#CC79A7' },
    { id: 'instances', name: 'Instances, scripts', color: '#D55E00' },
    { id: 'assembly', name: 'Assemblies, addresses', color: '#009E73' },
  ];
  const CODE = new Set(['type', 'method', 'field', 'property', 'event']);
  const groupOf = new Map(GROUPS.map((g) => [g.id, g]));
  const familyOf = new Map(FAMILIES.map((f) => [f.id, f]));
  const FONT = getComputedStyle(document.body).fontFamily;

  // Neighbours in the page: edges both ways plus the containment parent and children.
  const adj = nodes.map(() => new Set());
  const edgesOf = nodes.map(() => []);
  const children = nodes.map(() => []);
  edges.forEach((e, i) => {
    adj[e.s].add(e.t);
    adj[e.t].add(e.s);
    edgesOf[e.s].push(i);
    if (e.t !== e.s) {
      edgesOf[e.t].push(i);
    }
  });
  nodes.forEach((n) => {
    if (n.parent !== undefined) {
      adj[n.parent].add(n.id);
      adj[n.id].add(n.parent);
      children[n.parent].push(n.id);
    }
  });
  more.forEach((b) => {
    b.noun = b.label.replace(/^\+\d+ more /, '');
  });

  const state = {
    depth: data.depth,
    heuristic: true,
    off: new Set(), // legend groups switched off
    forced: new Set(), // nodes drawn by a double-click, a `+N more` node or a search
    expanded: new Set(), // nodes double-clicked: their `+N more` nodes show at any depth
  };
  let visible = new Set();
  let matches = [];
  let selected = null; // { type: 'node' | 'more' | 'edge', ref }

  const $ = (id) => document.getElementById(id);
  const details = $('details');
  const results = $('results');
  const search = $('search');
  const depth = $('depth');
  const depthValue = $('depth-value');
  const heuristic = $('heuristic');

  // ---- what shows ----

  const isOff = (n) => state.off.has(n.group) && !n.root;

  function walkedShown(n) {
    if (isOff(n)) {
      return false;
    }

    if (n.root || state.forced.has(n.id)) {
      return true;
    }

    return !n.container && !!n.drawn && n.depth <= state.depth;
  }

  // Shown walked nodes and every box holding one, unless its group is off (then they sit loose).
  function computeVisible() {
    const shown = new Set();
    nodes.forEach((n) => {
      if (!walkedShown(n)) {
        return;
      }

      shown.add(n.id);
      for (let p = n.parent; p !== undefined && !shown.has(p); p = nodes[p].parent) {
        if (!isOff(nodes[p])) {
          shown.add(p);
        }
      }
    });
    return shown;
  }

  function shownParent(n) {
    let p = n.parent;
    while (p !== undefined && !visible.has(p)) {
      p = nodes[p].parent;
    }

    return p;
  }

  function hiddenCount(id) {
    let count = 0;
    adj[id].forEach((o) => {
      if (o !== id && !visible.has(o) && !isOff(nodes[o])) {
        count++;
      }
    });
    return count;
  }

  const bundleLeft = (b) => b.members.filter((m) => !visible.has(m) && !isOff(nodes[m]));

  function bundleShown(b, left) {
    return visible.has(b.of) && (b.depth <= state.depth || state.expanded.has(b.of)) && left.length + (b.missing || 0) > 0;
  }

  function bundleText(b, left) {
    const count = left.length + (b.missing || 0);
    const label = '+' + count + ' more ' + b.noun;
    if (left.length === b.members.length) {
      return { label, detail: b.detail };
    }

    const kinds = new Map();
    left.forEach((m) => kinds.set(nodes[m].kind, (kinds.get(nodes[m].kind) || 0) + 1));
    const parts = [...kinds].sort((x, y) => y[1] - x[1] || (x[0] < y[0] ? -1 : 1)).map(([k, c]) => c + ' ' + k);
    let detail = parts.join(', ');
    if (b.missing) {
      detail += (detail ? '; ' : '') + b.missing + ' not in this page';
    }

    return { label, detail };
  }

  const shapeOf = (n) => (n.kind === 'asset' ? 'rectangle' : CODE.has(n.kind) ? 'ellipse' : 'round-rectangle');
  const sizeOf = (n) => Math.round((n.root ? 30 : 20) + Math.min(30, 7 * Math.sqrt(n.refs || 0)));

  function level(id) {
    let l = 0;
    for (let p = shownParent(nodes[id]); p !== undefined; p = shownParent(nodes[p])) {
      l++;
    }

    return l;
  }

  function elements() {
    const out = [];
    [...visible].map((id) => [level(id), id]).sort((x, y) => x[0] - y[0] || x[1] - y[1]).forEach(([, id]) => {
      const n = nodes[id];
      const hidden = hiddenCount(id);
      const parent = shownParent(n);
      const d = {
        id: 'n' + id,
        ref: id,
        label: hidden ? n.label + '  +' + hidden : n.label,
        color: groupOf.get(n.group).color,
        shape: shapeOf(n),
        size: sizeOf(n),
        root: !!n.root,
      };
      if (parent !== undefined) {
        d.parent = 'n' + parent;
      }

      out.push({ group: 'nodes', data: d });
    });
    edges.forEach((e, i) => {
      if (visible.has(e.s) && visible.has(e.t)) {
        out.push({
          group: 'edges',
          data: { id: 'e' + i, ref: i, source: 'n' + e.s, target: 'n' + e.t, color: familyOf.get(e.family).color, label: e.kind, heuristic: !!e.heuristic },
        });
      }
    });
    more.forEach((b, i) => {
      const left = bundleLeft(b);
      if (!bundleShown(b, left)) {
        return;
      }

      const color = familyOf.has(b.family) ? familyOf.get(b.family).color : '#999999';
      out.push({ group: 'nodes', classes: 'more', data: { id: b.id, bundle: i, label: bundleText(b, left).label, color, shape: 'round-rectangle', size: 16, root: false } });
      const [source, target] = b.dir === 'in' ? [b.id, 'n' + b.of] : ['n' + b.of, b.id];
      out.push({ group: 'edges', classes: 'more', data: { id: 'x' + b.id, source, target, color, label: '', heuristic: false } });
    });
    return out;
  }

  // ---- drawing ----

  function css(name) {
    return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  }

  function styles() {
    const text = css('--text');
    const bg = css('--bg');
    const muted = css('--muted');
    const accent = css('--accent');
    return [
      {
        selector: 'node',
        style: {
          shape: 'data(shape)',
          'background-color': 'data(color)',
          width: 'data(size)',
          height: 'data(size)',
          'border-width': 1,
          'border-color': css('--node-border'),
          label: 'data(label)',
          color: text,
          'font-family': FONT,
          'font-size': 11,
          'text-valign': 'bottom',
          'text-halign': 'center',
          'text-margin-y': 3,
          'text-wrap': 'ellipsis',
          'text-max-width': 220,
          'text-outline-color': bg,
          'text-outline-width': 2,
          'min-zoomed-font-size': 6,
        },
      },
      {
        selector: ':parent',
        style: {
          shape: 'round-rectangle',
          'background-opacity': 0.08,
          'border-color': 'data(color)',
          'border-width': 1.5,
          'text-valign': 'top',
          'text-margin-y': -3,
          'font-weight': 600,
          padding: 12,
        },
      },
      { selector: 'node[?root]', style: { 'border-width': 3, 'border-color': accent, 'font-weight': 700 } },
      {
        selector: 'node.more',
        style: {
          'background-color': css('--surface'),
          'border-style': 'dashed',
          'border-color': muted,
          color: muted,
          'font-style': 'italic',
        },
      },
      {
        selector: 'edge',
        style: {
          width: 1.4,
          'line-color': 'data(color)',
          'target-arrow-color': 'data(color)',
          'target-arrow-shape': 'triangle',
          'arrow-scale': 0.8,
          'curve-style': 'bezier',
          label: 'data(label)',
          'font-family': FONT,
          'font-size': 9,
          'min-zoomed-font-size': 10,
          color: muted,
          'text-rotation': 'autorotate',
          'text-background-color': bg,
          'text-background-opacity': 0.85,
          'text-background-padding': 1,
        },
      },
      { selector: 'edge[?heuristic]', style: { 'line-style': 'dashed' } },
      { selector: 'edge.more', style: { 'line-style': 'dotted' } },
      { selector: 'edge.off', style: { display: 'none' } },
      { selector: 'node:selected', style: { 'border-width': 3, 'border-color': accent } },
      { selector: 'edge:selected', style: { width: 3 } },
      { selector: '.match', style: { 'underlay-color': accent, 'underlay-opacity': 0.35, 'underlay-padding': 6 } },
      { selector: '.faded', style: { opacity: 0.18 } },
    ];
  }

  let cy;
  try {
    cy = cytoscape({
      container: $('graph'),
      style: styles(),
      minZoom: 0.05,
      maxZoom: 4,
      boxSelectionEnabled: false,
      textureOnViewport: nodes.length > 600,
      hideEdgesOnViewport: nodes.length > 600,
    });
  } catch (error) {
    $('graph').textContent = 'Could not draw the graph: ' + error.message;
    return;
  }

  // Cytoscape hears of window resizes only; the panel and the narrow layout resize the graph too.
  if (window.ResizeObserver) {
    new ResizeObserver(() => cy.resize()).observe($('graph'));
  }

  function runLayout(first, focus) {
    const animate = !first && cy.nodes().length <= 300;
    const options =
      data.layout === 'dagre'
        ? { name: 'dagre', rankDir: 'LR', nodeSep: 18, rankSep: 70, edgeSep: 8, nodeDimensionsIncludeLabels: true }
        : { name: 'fcose', quality: 'default', randomize: first, nodeDimensionsIncludeLabels: true, idealEdgeLength: 90, nestingFactor: 0.6, packComponents: true };
    Object.assign(options, { animate, animationDuration: 250, fit: false, padding: 30 });
    const layout = cy.layout(options);
    const done = layout.promiseOn('layoutstop').then(() => {
      // The panel may have changed the graph's size since the last resize event.
      cy.resize();
      const ele = focus === undefined ? cy.collection() : cy.getElementById('n' + focus);
      if (ele.nonempty()) {
        cy.animate({ center: { eles: ele }, duration: animate ? 200 : 0 });
      } else if (animate) {
        cy.animate({ fit: { eles: cy.elements(), padding: 30 }, duration: 200 });
      } else {
        cy.fit(cy.elements(), 30);
      }
    });
    layout.run();
    return done;
  }

  // Redraws what shows. Positions carry over (fCoSE starts from them); new nodes start next to
  // a neighbour already placed.
  function refresh(options) {
    const opts = options || {};
    const old = new Map();
    cy.nodes().forEach((n) => {
      if (n.isChildless()) {
        old.set(n.id(), Object.assign({}, n.position()));
      }
    });
    visible = computeVisible();
    cy.batch(() => {
      cy.elements().remove();
      cy.add(elements());
      if (!opts.first) {
        cy.nodes().forEach((n) => {
          if (n.isChildless()) {
            const p = old.get(n.id()) || near(n, old);
            if (p) {
              n.position(p);
            }
          }
        });
      }

      cy.edges('[?heuristic]').toggleClass('off', !state.heuristic);
    });
    markMatches();
    reselect();
    facts();
    return runLayout(!!opts.first, opts.focus);
  }

  function near(n, old) {
    const ref = n.data('bundle') !== undefined ? [more[n.data('bundle')].of] : [...adj[n.data('ref')]];
    for (const o of ref) {
      const p = old.get('n' + o);
      if (p) {
        return { x: p.x + (Math.random() - 0.5) * 60, y: p.y + (Math.random() - 0.5) * 60 };
      }
    }

    return null;
  }

  function highlight(ele) {
    cy.elements().removeClass('faded');
    if (!ele || ele.empty()) {
      return;
    }

    const core = ele.isNode() ? ele.union(ele.descendants()) : ele.connectedNodes().union(ele);
    const keep = core.union(core.closedNeighborhood());
    cy.elements().not(keep.union(keep.ancestors())).addClass('faded');
  }

  function reselect() {
    if (!selected) {
      return;
    }

    const { type, ref } = selected;
    const ele = cy.getElementById(type === 'node' ? 'n' + ref : type === 'edge' ? 'e' + ref : more[ref].id);
    ele.select();
    highlight(ele);
    if (type === 'node') {
      showNode(ref);
    } else if (type === 'edge') {
      showEdge(ref);
    } else {
      showMore(ref);
    }
  }

  // ---- side panel ----

  function h(tag, props, ...kids) {
    const e = document.createElement(tag);
    Object.assign(e, props || {});
    kids.forEach((k) => {
      if (k !== null && k !== undefined && k !== false) {
        e.append(k instanceof Node ? k : String(k));
      }
    });
    return e;
  }

  function copy(text, code, button) {
    const done = (ok) => {
      button.textContent = ok ? 'Copied' : 'Selected: copy it';
      setTimeout(() => {
        button.textContent = 'Copy';
      }, 1500);
    };
    const fallback = () => {
      const range = document.createRange();
      range.selectNodeContents(code);
      const selection = window.getSelection();
      selection.removeAllRanges();
      selection.addRange(range);
      let ok = false;
      try {
        ok = document.execCommand('copy');
      } catch (error) {
        ok = false;
      }

      done(ok);
    };
    if (navigator.clipboard && window.isSecureContext) {
      navigator.clipboard.writeText(text).then(() => done(true), fallback);
    } else {
      fallback();
    }
  }

  function commandBox(text) {
    const code = h('code', {}, text);
    const button = h('button', { type: 'button', title: 'Copy the command' }, 'Copy');
    button.addEventListener('click', () => copy(text, code, button));
    return h('div', { className: 'command' }, code, button);
  }

  function link(id) {
    const n = nodes[id];
    const button = h('button', { type: 'button', title: n.title }, n.label);
    button.addEventListener('click', () => focus(id));
    return button;
  }

  function list(items, limit) {
    const shown = items.slice(0, limit);
    const ul = h('ul', { className: 'links' }, ...shown);
    if (items.length > limit) {
      ul.append(h('li', { className: 'muted' }, '+' + (items.length - limit) + ' more'));
    }

    return ul;
  }

  function row(dl, term, value) {
    if (value !== undefined && value !== null && value !== '') {
      dl.append(h('dt', {}, term), h('dd', {}, value));
    }
  }

  function showNode(id) {
    const n = nodes[id];
    const dl = h('dl');
    row(dl, 'Kind', n.what === n.kind ? n.kind : n.what + ' (' + n.kind + ')');
    row(dl, 'At', n.at);
    row(dl, 'Key', n.key);
    row(dl, 'Hops', n.container ? 'holds a node here' : String(n.depth));
    row(dl, 'Used by', n.refs ? String(n.refs) + ' in this page' : '');
    const parts = [h('h3', {}, n.title), dl, commandBox(n.cmd)];
    if (n.parent !== undefined) {
      parts.push(h('h2', {}, 'Inside'), list([h('li', {}, link(n.parent))], 1));
    }

    if (children[id].length) {
      parts.push(h('h2', {}, 'Holds ' + children[id].length), list(children[id].map((c) => h('li', {}, link(c))), 100));
    }

    const out = edgesOf[id].filter((i) => edges[i].s === id);
    const into = edgesOf[id].filter((i) => edges[i].t === id && edges[i].s !== id);
    if (out.length) {
      parts.push(h('h2', {}, 'Out ' + out.length), list(out.map((i) => edgeItem(i, edges[i].t)), 100));
    }

    if (into.length) {
      parts.push(h('h2', {}, 'In ' + into.length), list(into.map((i) => edgeItem(i, edges[i].s)), 100));
    }

    const hidden = hiddenCount(id);
    if (hidden) {
      const button = h('button', { type: 'button' }, 'Draw ' + hidden + ' more');
      button.addEventListener('click', () => expand(id));
      parts.push(h('p', {}, button));
    }

    details.replaceChildren(...parts);
  }

  function edgeItem(i, other) {
    const e = edges[i];
    const kind = e.kind + (e.heuristic ? ' (heuristic)' : '') + (e.note ? ' ' + e.note : '');
    return h('li', {}, h('span', { className: 'kind' }, kind + ' '), link(other));
  }

  function showMore(i) {
    const b = more[i];
    const left = bundleLeft(b);
    const text = bundleText(b, left);
    const parts = [h('h3', {}, text.label), h('p', { className: 'small' }, 'Of ', link(b.of), ': ' + text.detail)];
    if (left.length) {
      const button = h('button', { type: 'button' }, 'Draw ' + left.length);
      button.addEventListener('click', () => openMore(i));
      parts.push(h('p', {}, button), list(left.map((m) => h('li', {}, link(m))), 100));
    }

    if (b.missing) {
      parts.push(h('p', { className: 'small muted' }, b.missing + ' not in this page (cut at 1,500 nodes). To bake them all:'));
      if (data.cut) {
        parts.push(commandBox(data.cut));
      }
    }

    details.replaceChildren(...parts);
  }

  function showEdge(i) {
    const e = edges[i];
    const dl = h('dl');
    row(dl, 'Kind', e.kind);
    row(dl, 'Confidence', e.heuristic ? 'heuristic' : 'exact');
    row(dl, 'Note', e.note);
    row(dl, 'At', e.at);
    details.replaceChildren(h('h3', {}, nodes[e.s].label + ' → ' + nodes[e.t].label), dl, h('p', { className: 'small' }, 'From ', link(e.s), ' to ', link(e.t)));
  }

  // ---- actions ----

  function select(type, ref) {
    selected = { type, ref };
    cy.elements(':selected').unselect();
    reselect();
  }

  function clearSelection() {
    selected = null;
    cy.elements(':selected').unselect();
    highlight(null);
    details.replaceChildren();
  }

  function focus(id) {
    const n = nodes[id];
    let redraw = false;
    if (isOff(n)) {
      state.off.delete(n.group);
      legendSync();
      redraw = true;
    }

    if (!visible.has(id)) {
      state.forced.add(id);
      redraw = true;
    }

    if (redraw) {
      selected = { type: 'node', ref: id };
      cy.elements(':selected').unselect();
      refresh({ focus: id });
    } else {
      select('node', id);
      cy.animate({ center: { eles: cy.getElementById('n' + id) }, duration: 200 });
    }
  }

  function expand(id) {
    state.expanded.add(id);
    adj[id].forEach((o) => {
      if (!isOff(nodes[o])) {
        state.forced.add(o);
      }
    });
    selected = { type: 'node', ref: id };
    refresh({ focus: id });
  }

  function openMore(i) {
    const b = more[i];
    b.members.forEach((m) => state.forced.add(m));
    selected = { type: 'node', ref: b.of };
    refresh({ focus: b.of });
  }

  cy.on('tap', 'node', (event) => {
    const d = event.target.data();
    if (d.bundle !== undefined) {
      select('more', d.bundle);
    } else {
      select('node', d.ref);
    }
  });
  cy.on('tap', 'edge', (event) => {
    const d = event.target.data();
    if (d.ref !== undefined) {
      select('edge', d.ref);
    }
  });
  cy.on('tap', (event) => {
    if (event.target === cy) {
      clearSelection();
    }
  });
  cy.on('dbltap', 'node', (event) => {
    const d = event.target.data();
    if (d.bundle !== undefined) {
      openMore(d.bundle);
    } else {
      expand(d.ref);
    }
  });

  // ---- search ----

  function markMatches() {
    cy.nodes('.match').removeClass('match');
    matches.forEach((id) => cy.getElementById('n' + id).addClass('match'));
  }

  function runSearch() {
    const q = search.value.trim().toLowerCase();
    matches = [];
    if (q) {
      matches = nodes.filter((n) => (n.label + '\n' + n.title + '\n' + n.key).toLowerCase().includes(q)).map((n) => n.id);
      matches.sort((a, b) => Number(visible.has(b)) - Number(visible.has(a)) || a - b);
    }

    markMatches();
    if (!q) {
      results.hidden = true;
      results.replaceChildren();
      return;
    }

    const items = matches.map((id) => h('li', {}, link(id), visible.has(id) ? '' : h('span', { className: 'kind' }, ' (not drawn)')));
    results.replaceChildren(h('h2', {}, matches.length === 1 ? '1 match' : matches.length + ' matches'), list(items, 50));
    results.hidden = false;
  }

  search.addEventListener('input', runSearch);
  search.addEventListener('keydown', (event) => {
    if (event.key === 'Enter' && matches.length) {
      focus(matches[0]);
    } else if (event.key === 'Escape') {
      search.value = '';
      runSearch();
    }
  });

  // ---- controls, legend, notes ----

  depth.max = String(data.baked);
  depth.value = String(state.depth);

  function depthText() {
    depthValue.textContent = state.depth + ' of ' + data.baked;
  }

  depth.addEventListener('input', () => {
    state.depth = Number(depth.value);
    depthText();
    refresh();
  });
  heuristic.addEventListener('change', () => {
    state.heuristic = heuristic.checked;
    cy.edges('[?heuristic]').toggleClass('off', !state.heuristic);
  });
  $('fit').addEventListener('click', () => cy.animate({ fit: { eles: cy.elements(), padding: 30 }, duration: 200 }));
  $('reset').addEventListener('click', () => {
    state.depth = data.depth;
    state.heuristic = true;
    state.off.clear();
    state.forced.clear();
    state.expanded.clear();
    depth.value = String(state.depth);
    heuristic.checked = true;
    search.value = '';
    depthText();
    legendSync();
    runSearch();
    clearSelection();
    refresh();
  });

  const counts = new Map();
  nodes.forEach((n) => counts.set(n.group, (counts.get(n.group) || 0) + 1));
  const boxes = new Map();
  GROUPS.forEach((g) => {
    const count = counts.get(g.id) || 0;
    const box = h('input', { type: 'checkbox', checked: true, disabled: count === 0 });
    box.addEventListener('change', () => {
      if (box.checked) {
        state.off.delete(g.id);
      } else {
        state.off.add(g.id);
      }

      refresh();
    });
    boxes.set(g.id, box);
    const swatch = h('span', { className: 'swatch ' + g.shape });
    swatch.style.background = g.color;
    $('legend-nodes').append(h('li', {}, h('label', {}, box, swatch, g.name + ' ', h('span', { className: 'muted' }, String(count)))));
  });
  FAMILIES.forEach((f) => {
    const line = h('span', { className: 'line' });
    line.style.borderTopColor = f.color;
    $('legend-edges').append(h('li', {}, h('span', { className: 'item' }, line, f.name)));
  });

  function legendSync() {
    boxes.forEach((box, id) => {
      box.checked = !state.off.has(id);
    });
  }

  function facts() {
    const drawn = visible.size;
    const shownEdges = cy.edges().filter((e) => !e.hasClass('more')).length;
    $('stamp').textContent =
      'generated ' + data.generated + ' from graph ' + data.revision + ' · ' + drawn + ' of ' + nodes.length + ' nodes, ' + shownEdges + ' of ' + edges.length + ' edges drawn';
  }

  $('query').textContent = data.query;
  const notes = [];
  if (data.kinds.length) {
    notes.push(h('p', {}, 'Kinds: ' + data.kinds.join(', ') + '.'));
  }

  if (data.unresolved) {
    notes.push(h('p', {}, data.unresolved + (data.unresolved === 1 ? ' edge' : ' edges') + ' to code outside the graph (engine or package APIs, deleted symbols) left out.'));
  }

  if (data.cut) {
    notes.push(h('p', {}, 'This page stops at 1,500 nodes; the +N more nodes say what was left out. To bake them all:'), commandBox(data.cut));
  }

  $('notes').replaceChildren(...notes);
  depthText();

  const dark = window.matchMedia('(prefers-color-scheme: dark)');
  if (dark.addEventListener) {
    dark.addEventListener('change', () => cy.style(styles()));
  }

  // The symbol's details show from the start (before the layout, so the fit sees the final
  // size); nothing is faded until a click.
  visible = computeVisible();
  const root = nodes.findIndex((n) => n.root);
  if (root >= 0) {
    showNode(root);
  }

  refresh({ first: true });
})();
