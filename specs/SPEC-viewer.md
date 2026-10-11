# Spec: viewer

`ug view`: a self-contained HTML graph for people. Claude sees one line (the file path), so it
costs 0 tokens of graph. Decisions below were made by the user on 2026-10-01 ("all recommended")
and refined on 2026-10-11 ("go ahead" on the picks and gap fixes: Sourcetrail-style page,
nesting for containment, a hidden ring for expand, the kind and relation mapping below, the
revision hash). Depends on `query`. Built after the step-3 checkpoint (SPEC.md Decisions #5).

References: Sourcetrail (one symbol in the middle, callers left, callees right, members nested in
their type, `+N` bundles), Neo4j Browser (double-click expands), Visual Studio Code Map and NDepend
(containment drawn as nesting). Graphify's `graph.html` maps the whole project; this one does not.

## Command

```
ug view <symbol|asset> [--depth N] [--kind <k>[,<k>]] [--all] [--open] [--out <file>]
```

- Scope: the neighbourhood of one symbol or asset, `--depth` 1 to 4 (default 2). No
  whole-project map. Depth counts hops over any edge kind, both directions, with one exception:
  stepping up to a container is free and does not spread (a member's type, a component's game
  object, a game object's parent or its scene or prefab, a prefab instance's parent or holder, a
  nested type's outer type). Those containers are added so each node sits in its box. Stepping
  down (a type's members, a game object's components and children, an asset's objects) is a hop.
- `--kind` limits the node kinds shown and walked: node kinds (`type`, `method`, `field`,
  `property`, `event`, `asset`, `game_object`, `component`, `prefab_instance`, `sub_asset`) or
  asset types (`scene`, `prefab`, `script`, `scriptable_object`, `material`, ...), comma
  separated. The symbol itself is always shown; a container whose kind is left out is dropped and
  its children sit loose. An unknown name is refused with the list.
- Edges whose far end has no node (engine and package APIs, deleted symbols) are left out; the
  page says how many.
- Output: `Library/unitygraph/view.html` (or `--out`), one file, no network at view time,
  Cytoscape.js and the layout plugins inlined. The answer is one line:
  `Library/unitygraph/view.html  41 nodes (38 drawn), 57 edges  generated 2026-10-11 14:03 from graph 3f2a9c1b7e04`.
  The browser opens only with `--open` (SPEC.md Decision 5 said "and opens it"; changed here,
  since `ug` cannot count on a desktop session). `--open` hands the file to the system's default
  browser; when that fails the answer adds `(could not open a browser)`, still exit 0.
- Data: the neighbourhood is baked into the page at generation time, one hop past `--depth` (the
  hidden ring double-click expands into). A static snapshot; the page shows
  "generated <time> from graph <revision>" so staleness is visible. No live server. The revision
  is the first 12 hex characters of a SHA-256 over every indexed file's path and content hash:
  it changes with any reindex that changed a file and with nothing else.
- Unknown or ambiguous symbol: same answers as `ug explain`, with `ug view` in the commands they
  name; no file is written.
- The graph is brought up to date first, as for the query commands.
- The `viewer` module switch (`.claude/unitygraph.json`) turns it off: the answer is
  `viewer is off in .claude/unitygraph.json` and no file is written.

## Caps (SPEC-query, Nothing out of reach)

- Drawn: at most 150 nodes on first open, and at most 12 per node, relation family and
  direction (`calls` in, `calls` out, ...), taken in a fixed order (relation, then name). The
  rest collapse into one `+N more` node per such group, on the node they hang off, that names
  what was cut (`+18 more callers: 15 method, 3 type`). Double-click on it draws them. Containers
  of drawn nodes are drawn too, so a cut can leave a few more than 150.
- Baked: at most 1,500 walked nodes; their containers come on top (RunDirector at depth 4 on
  purge-squad bakes 1,585). Nodes past it are counted in the `+N more` node of the group they
  belong to as "not in this page" and the answer line adds `; cut at 1500 nodes: <command> --all`.
  `--all` lifts the baked cap (the page grows past 2 MB on big neighbourhoods; that is the
  user's call).

## Page

- **Layout:** left to right, so callers sit left of the symbol and callees right of it:
  hierarchical (dagre, `rankDir: LR`) when `--depth` is 3 or less, force-directed (fCoSE) above
  that. Containment is nesting, not lines: a type's box holds its members, a game object's box
  holds its components and child objects, a scene or prefab box holds its objects.
- **Nodes:** colour by kind from the Okabe-Ito palette (colour-blind safe); shape is the second
  axis. Size by number of references inside the page. Light and dark follow
  `prefers-color-scheme`; a legend is always shown.

  | Group | Kinds | Shape | Colour |
  | --- | --- | --- | --- |
  | type | `type` | ellipse | blue `#0072B2` |
  | method | `method` | ellipse | sky blue `#56B4E9` |
  | member | `field`, `property`, `event` | ellipse | bluish green `#009E73` |
  | script | asset `script` | square | yellow `#F0E442` |
  | scene | asset `scene` | square | orange `#E69F00` |
  | prefab | asset `prefab`, `prefab_instance` | square, round-rect | vermillion `#D55E00` |
  | ScriptableObject | asset `scriptable_object`, `sub_asset` | square, round-rect | reddish purple `#CC79A7` |
  | other asset | every other asset type | square | grey `#999999` |
  | game object | `game_object` | round-rect | grey `#999999` |
  | component | `component` | round-rect | bluish green `#009E73` |

  Code is an ellipse, an asset a square, an object inside an asset a round-rect.
- **Edges:** `exact` solid, `heuristic` dashed, with arrowheads for direction (`src kind dst`);
  colour by relation family, label by exact kind (labels show when zoomed in). A toggle hides
  heuristic edges.

  | Family | Edge kinds | Colour |
  | --- | --- | --- |
  | calls | `calls` (but reads and writes), `dispatches`, `event_calls`, `anim_event_calls` | blue |
  | references | `references`, `overrides_value`, `uses_type`, `calls` that read or write (B1g) | orange |
  | hierarchy | `inherits`, `implements`, `overrides` | reddish purple |
  | instances | `instance_of`, `script_defines`, `variant_of` | vermillion |
  | assembly | `in_assembly`, `assembly_refs`, `addressable` | bluish green |
  | structure | `contains`, `child_of`, `places` | drawn as nesting |

- **Interaction:** click a node for a side panel with its kind, `file:line` and the exact
  `ug explain <symbol>` command to copy; double-click a node draws every neighbour already inside
  the page (no regenerate), double-click a `+N more` node draws its group; a node with neighbours
  not drawn yet shows how many. Search box, kind checkboxes (by group), depth slider from 1 to the
  baked depth, heuristic toggle. No "open in editor" links.
  - Shown: the symbol, nodes double-clicked into the page, and walked nodes drawn on first open
    whose depth is on the slider; then every box holding one of them. A legend group switched off
    hides its nodes (never the symbol) and its boxes, whose children then sit loose.
  - Double-click draws every neighbour in the page, the 12-per-group cut aside, and shows that
    node's `+N more` nodes at any depth; a `+N more` node shows while its node shows and its
    depth is on the slider, and counts only members not drawn yet.
  - The panel: kind, `file:line`, key, hops, uses inside the page, the `ug explain` command with
    Copy; then what holds it, what it holds, edges out and in. Every name there jumps to the node
    and draws it if needed. A `+N more` node's panel lists its members and, when some were not
    baked, the `--all` command. An edge's panel: kind, confidence, note, `file:line`.
  - Search matches name, full name and key; Enter goes to the first match, Escape clears. Reset
    returns to first open. Below 760 px wide the panel sits under the graph and the page scrolls.
- **Look:** minimal; system fonts only (no web fonts offline); title bar shows the query and
  stamp. A `Content-Security-Policy` meta (`default-src 'none'`, inline scripts and styles only)
  makes the browser refuse any request the page might make.

## Code

- `src/UnityGraph.Viewer`: `Neighbourhood` (the walk, caps and bundles; plain data, golden
  tested), `ViewPage` (the HTML: template, data, vendored scripts and notices, all embedded
  resources). References `UnityGraph.Query` for symbol resolution and naming. `ViewPage` fills
  the template's placeholders in one pass (a query or path holding one stays text), inlines each
  vendored script in its own `<script>` without its `sourceMappingURL` comment, and refuses a
  script holding `</script` or `<!--`. The data is JSON with `<`, `>`, `&`, `'`, `+` escaped.
- `ug view` lives in `src/UnityGraph.Cli/ViewCommand.cs`; it shares the query commands' graph
  update and lock handling.

## Boundaries

- Reads the graph only; writes only the output file; never from a hook; never over the network;
  the page makes no requests.
- Vendored under `src/UnityGraph.Viewer/vendor`, unmodified, with their licences, all MIT:
  cytoscape 3.34.3 (`dist/cytoscape.min.js`), cytoscape-dagre 4.0.1 (`dist/cytoscape-dagre.min.js`,
  which bundles @dagrejs/dagre 3 and @dagrejs/graphlib 4), cytoscape-fcose 2.2.0, cose-base 2.2.0,
  layout-base 2.0.1 (its SVD routine carries JamaJS's Apache-2.0 notice inline). The page keeps
  every notice in a "Licences" section.

## Testing

- Golden test on the fixture: the node/edge JSON baked into the page for a type, a method, an
  asset and a game object, with a fixed clock.
- Walk rules: going up to a container is free, going down is a hop; `--kind` limits and the
  symbol stays; unresolved edges are counted, not drawn.
- Page checks without a browser: file is self-contained (no `<script src>`, no `<link>`, no
  `@import`, no `url(` outside `data:`), the CSP meta is there, size under 2 MB on the fixture,
  every vendored notice is in it, the 150 and 12 cuts produce `+N more` nodes, the 1,500 cut
  says `--all`, `--kind` and `--depth` limits.
- CLI: the answer line, `--out`, ambiguous and unknown symbols (no file written), bad `--depth`
  and `--kind`.
- One headless-browser smoke test is optional and env-gated (renders, no console errors). Not
  built: it needs a browser dependency (ask first). Checked by hand instead, 2026-10-11, in the
  desktop app's browser pane on purge-squad pages: dagre and fCoSE layouts render, the page makes
  no requests and logs no errors; expand, `+N more`, the depth slider, search, legend boxes, the
  heuristic toggle and the edge panel work; light and dark, wide and narrow.

## Measured (purge-squad, 2026-10-11)

Warm graph, `ug.exe` from `artifacts/bin`; load is the browser's `loadEventEnd`.

| Command | Time | Page | Baked | First open | Load |
| --- | --- | --- | --- | --- | --- |
| `ug view RunDirector --depth 1` | 471 ms | 0.91 MB | 153 nodes, 185 edges | 18 | |
| `ug view RunDirector` | 518 ms | 1.15 MB | 773 nodes, 642 edges | 118 | |
| `ug view RunDirector --depth 3` | 626 ms | 1.53 MB | 1,451 nodes, 2,000 edges | 158 | |
| `ug view RunDirector --depth 4` | 729 ms | 1.69 MB | 1,585 nodes, 2,556 edges, cut | 158 | 274 ms |
| `ug view RunDirector --depth 4 --all` | 793 ms | 2.03 MB | 2,353 nodes, 3,527 edges | 158 | 565 ms |
| `ug view Assets/Prefabs/Player.prefab` | 453 ms | 0.87 MB | 61 nodes, 59 edges | 21 | |

`ug explain RunDirector` takes 421 ms on the same graph, so the walk and page add about 0.1 to
0.4 s. The vendored scripts are 0.81 MB of every page.
