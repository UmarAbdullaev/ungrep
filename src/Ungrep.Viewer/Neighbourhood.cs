using System.Globalization;
using Ungrep.Core.Graph;
using Ungrep.Query;

namespace Ungrep.Viewer;

/// <summary>What `ug view` walks: depth, kinds, whether the baked cap holds, and the stamp.</summary>
public sealed record ViewOptions(int Depth, IReadOnlySet<string>? Kinds, bool All, DateTimeOffset Generated);

/// <summary>
/// The neighbourhood of one symbol or asset (SPEC-viewer, Command and Caps): a breadth-first walk
/// over every edge kind, both ways, one hop past the depth asked for. Stepping up to a container
/// is free and does not spread; stepping down is a hop. Then the containers of every walked node,
/// the first-open cuts (150 drawn, 12 per node, family and direction) and the `+N more` groups.
/// </summary>
public static class Neighbourhood
{
    public const int MinDepth = 1;
    public const int MaxDepth = 4;
    public const int MaxDrawn = 150;
    public const int PerGroup = 12;
    public const int MaxBaked = 1500;

    private const int NoLimit = int.MaxValue - 1;

    /// <summary>Asset types `--kind` takes besides node kinds (SPEC-graph-core, Node kinds).</summary>
    public static readonly IReadOnlyList<string> AssetTypes =
    [
        "script", "scene", "prefab", "scriptable_object", "material", "shader", "animator_controller", "animation_clip",
        "input_actions", "asmdef", "texture", "audio", "model", "other",
    ];

    /// <summary>Every name `--kind` takes: node kinds, then asset types.</summary>
    public static IReadOnlyList<string> KindChoices { get; } =
        [.. Enum.GetValues<NodeKind>().Select(KindNames.Of), .. AssetTypes];

    // Relation families in the order a node's neighbours are taken (SPEC-viewer, Edges).
    private static readonly string[] _families = ["structure", "hierarchy", "calls", "references", "instances", "assembly"];

    public static ViewGraph Walk(GraphStore store, IReadOnlyList<Node> roots, string query, ViewOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Depth, MinDepth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Depth, MaxDepth);
        var walk = new Walker(store, options);
        walk.Run(roots);
        string kinds = options.Kinds is { Count: > 0 } k ? " --kind " + string.Join(',', k.Order(StringComparer.Ordinal)) : "";
        return new ViewGraph
        {
            Query = query,
            Depth = options.Depth,
            Generated = options.Generated.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            Revision = store.Revision(),
            Kinds = options.Kinds is null ? [] : [.. options.Kinds.Order(StringComparer.Ordinal)],
            Nodes = walk.Nodes,
            Edges = walk.Edges,
            More = walk.More,
            Unresolved = walk.Unresolved,
            Cut = walk.Cut,
            AllCommand = $"ug view {Names.Quote(query)} --depth {options.Depth.ToString(CultureInfo.InvariantCulture)}{kinds} --all",
        };
    }

    /// <summary>The legend group of a node (SPEC-viewer, Nodes).</summary>
    internal static string Group(Node node) => node.Kind switch
    {
        NodeKind.Type => "type",
        NodeKind.Method => "method",
        NodeKind.Field or NodeKind.Property or NodeKind.Event => "member",
        NodeKind.Asset => Names.Prop(node, "asset_type") switch
        {
            "script" => "script",
            "scene" => "scene",
            "prefab" => "prefab",
            "scriptable_object" => "so",
            _ => "asset",
        },
        NodeKind.GameObject => "object",
        NodeKind.Component => "component",
        NodeKind.PrefabInstance => "prefab",
        _ => "so",
    };

    /// <summary>The relation family of an edge kind (SPEC-viewer, Edges).</summary>
    internal static string Family(EdgeKind kind) => kind switch
    {
        EdgeKind.Contains or EdgeKind.ChildOf or EdgeKind.Places => "structure",
        EdgeKind.Inherits or EdgeKind.Implements or EdgeKind.Overrides => "hierarchy",
        EdgeKind.Calls or EdgeKind.Dispatches or EdgeKind.EventCalls or EdgeKind.AnimEventCalls => "calls",
        EdgeKind.References or EdgeKind.OverridesValue or EdgeKind.UsesType => "references",
        EdgeKind.InstanceOf or EdgeKind.ScriptDefines or EdgeKind.VariantOf => "instances",
        _ => "assembly",
    };

    // A `calls` edge that reads or writes a field or property is a reference (B1g).
    internal static string Family(Edge edge) =>
        edge.Kind == EdgeKind.Calls && Names.Prop(edge, "use") is "read" or "write" ? "references" : Family(edge.Kind);

    // What a `+N more` node calls its group: `+18 more callers`.
    private static string Noun(string family, bool incoming) => (family, incoming) switch
    {
        ("structure", _) => "inside",
        ("hierarchy", true) => "subtypes and overriders",
        ("hierarchy", false) => "bases and overridden",
        ("calls", true) => "callers",
        ("calls", false) => "callees",
        ("references", true) => "users",
        ("references", false) => "used",
        ("instances", true) => "instances",
        ("instances", false) => "sources",
        (_, true) => "dependents",
        _ => "dependencies",
    };

    // One neighbour of a walked node: the edge, which end is the other one, and how it walks.
    private readonly record struct Step(Edge Edge, bool Incoming, string Other, bool Resolved, string Family, bool Down);

    private sealed class Bundle(int of, string family, bool incoming, int depth)
    {
        public int Of { get; } = of;
        public string Family { get; } = family;
        public bool Incoming { get; } = incoming;
        public int Depth { get; } = depth;
        public List<int> Members { get; } = [];
        public HashSet<string> Missing { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> MissingKinds { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Walker(GraphStore store, ViewOptions options)
    {
        private readonly Dictionary<string, Node?> _cache = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
        private readonly Dictionary<int, Bundle> _groupOf = [];
        private readonly List<Bundle> _groups = [];
        private readonly HashSet<Edge> _edgeSeen = [];
        private readonly HashSet<Edge> _unresolved = [];

        public List<ViewNode> Nodes { get; } = [];
        public List<ViewEdge> Edges { get; } = [];
        public List<ViewMore> More { get; } = [];
        public int Unresolved => _unresolved.Count;
        public bool Cut { get; private set; }

        public void Run(IReadOnlyList<Node> roots)
        {
            var level = new List<int>();
            foreach (Node root in roots.Where(r => !_ids.ContainsKey(r.Key)))
            {
                level.Add(Add(root, depth: 0, root: true, container: false));
            }

            for (int depth = 0; depth < options.Depth + 1 && level.Count > 0; depth++)
            {
                var next = new List<int>();
                foreach (int id in level)
                {
                    Expand(id, depth, next);
                }

                level = next;
            }

            AddContainers();
            Draw();
            Count();
        }

        // Every neighbour of a walked node, in family order: already baked ones get the edge,
        // new ones join the next level, the rest past the baked cap are counted as missing.
        private void Expand(int id, int depth, List<int> next)
        {
            ViewNode from = Nodes[id];
            foreach (Step step in Steps(from))
            {
                if (!step.Resolved)
                {
                    _unresolved.Add(step.Edge);
                    continue;
                }

                if (_ids.TryGetValue(step.Other, out int known))
                {
                    AddEdge(step.Edge, step.Incoming ? known : id, step.Incoming ? id : known);
                    continue;
                }

                Node? other = null;
                if (options.Kinds is not null)
                {
                    other = Node(step.Other);
                    if (other is null || !Passes(other))
                    {
                        continue;
                    }
                }

                Bundle group = GroupFor(id, step.Family, step.Incoming, depth + 1);
                if (!options.All && Nodes.Count >= MaxBaked)
                {
                    Cut = true;
                    if (group.Missing.Add(step.Other))
                    {
                        string kind = (other ?? Node(step.Other)) is { } n ? KindNames.Of(n.Kind) : "node";
                        group.MissingKinds[kind] = group.MissingKinds.GetValueOrDefault(kind) + 1;
                    }

                    continue;
                }

                other ??= Node(step.Other);
                if (other is null)
                {
                    continue;
                }

                int added = Add(other, depth + 1, root: false, container: false);
                group.Members.Add(added);
                _groupOf[added] = group;
                next.Add(added);
                AddEdge(step.Edge, step.Incoming ? added : id, step.Incoming ? id : added);
            }
        }

        // The edges to walk from a node, structure-up ones left out (containers come later), in
        // family order, outgoing first, then by edge kind and the other end's key.
        private List<Step> Steps(ViewNode from)
        {
            var steps = new List<Step>();
            foreach (EdgeHit hit in store.EdgesFrom(from.Key, EdgeKindSet.All, NoLimit).Items)
            {
                Edge e = hit.Edge;
                bool up = e.Kind == EdgeKind.ChildOf;
                if (!up)
                {
                    steps.Add(new Step(e, Incoming: false, e.Dst, hit.Resolved, Family(e), Down: e.Kind is EdgeKind.Contains or EdgeKind.Places));
                }
            }

            IEnumerable<EdgeHit> incoming = store.EdgesTo(from.Key, EdgeKindSet.All, NoLimit).Items;
            int paren = from.Key.IndexOf('(', StringComparison.Ordinal);
            if (from.Kind == NodeKind.Method && paren > 0)
            {
                // A dynamic UnityEvent call names its method without parameters (SPEC-asset-index, event_calls).
                incoming = incoming.Concat(store.EdgesTo(from.Key[..paren], EdgeKindSet.Of(EdgeKind.EventCalls), NoLimit).Items);
            }

            foreach (EdgeHit hit in incoming)
            {
                Edge e = hit.Edge;
                if (e.Kind is not (EdgeKind.Contains or EdgeKind.Places))
                {
                    steps.Add(new Step(e, Incoming: true, e.Src, hit.Resolved, Family(e), Down: e.Kind == EdgeKind.ChildOf));
                }
            }

            // An asset holds every game object of its file, but a child is reached through its parent.
            if (from.Kind == NodeKind.Asset)
            {
                string file = from.At;
                var children = store.EdgesInFile(file, EdgeKindSet.Of(EdgeKind.ChildOf), NoLimit).Items
                    .Select(h => h.Edge.Src).ToHashSet(StringComparer.Ordinal);
                steps.RemoveAll(s => s.Down && !s.Incoming && children.Contains(s.Other));
            }

            return [.. steps
                .OrderBy(s => Array.IndexOf(_families, s.Family))
                .ThenBy(s => s.Incoming)
                .ThenBy(s => s.Edge.Kind)
                .ThenBy(s => s.Other, StringComparer.Ordinal)
                .ThenBy(s => s.Edge.File, StringComparer.Ordinal)
                .ThenBy(s => s.Edge.Line)];
        }

        // Containers of every baked node, up to the top: free, not walked, not cut.
        private void AddContainers()
        {
            for (int i = 0; i < Nodes.Count; i++)
            {
                ViewNode node = Nodes[i];
                if (node.Parent is not null || ContainerOf(node) is not { } holder)
                {
                    continue;
                }

                if (!_ids.TryGetValue(holder.Key, out int parent))
                {
                    if (!Passes(holder))
                    {
                        continue;
                    }

                    parent = Add(holder, node.Depth, root: false, container: true);
                }

                if (!IsAncestor(i, parent))
                {
                    node.Parent = parent;
                }
            }

            foreach (ViewNode node in Nodes.Where(n => n.Kind == NodeKind.Component && n.Parent is int p && Nodes[p].Kind == NodeKind.GameObject))
            {
                node.Title = Nodes[node.Parent!.Value].Title + " > " + node.Label;
            }
        }

        private bool IsAncestor(int node, int of)
        {
            for (int? id = of; id is int i; id = Nodes[i].Parent)
            {
                if (i == node)
                {
                    return true;
                }
            }

            return false;
        }

        // The node that holds this one: a member's type, a component's game object, a game
        // object's parent or else its asset, a prefab instance's parent or else its holder.
        private Node? ContainerOf(ViewNode node)
        {
            string? key = node.Kind switch
            {
                NodeKind.Type or NodeKind.Method or NodeKind.Field or NodeKind.Property or NodeKind.Event
                    or NodeKind.Component or NodeKind.SubAsset => Source(node.Key, EdgeKind.Contains),
                NodeKind.GameObject => Target(node.Key, EdgeKind.ChildOf) ?? Source(node.Key, EdgeKind.Contains),
                NodeKind.PrefabInstance => Target(node.Key, EdgeKind.ChildOf) ?? Source(node.Key, EdgeKind.Places),
                _ => null,
            };
            return key is null ? null : Node(key);
        }

        private string? Source(string key, EdgeKind kind) => store.EdgesTo(key, EdgeKindSet.Of(kind), 1).Items.FirstOrDefault()?.Edge.Src;

        private string? Target(string key, EdgeKind kind) => store.EdgesFrom(key, EdgeKindSet.Of(kind), 1).Items.FirstOrDefault()?.Edge.Dst;

        // First-open cuts: roots always; then walked nodes in walk order, at most 12 per group
        // and 150 in all. Groups with undrawn or unbaked members become `+N more` nodes.
        private void Draw()
        {
            int drawn = 0;
            var perGroup = new Dictionary<Bundle, int>();
            foreach (ViewNode node in Nodes.Where(n => !n.Container))
            {
                if (node.Root)
                {
                    node.Drawn = true;
                    drawn++;
                    continue;
                }

                Bundle group = _groupOf[node.Id];
                int inGroup = perGroup.GetValueOrDefault(group);
                if (inGroup < PerGroup && drawn < MaxDrawn)
                {
                    node.Drawn = true;
                    perGroup[group] = inGroup + 1;
                    drawn++;
                }
            }

            foreach (Bundle group in _groups)
            {
                List<int> hidden = [.. group.Members.Where(m => !Nodes[m].Drawn)];
                if (hidden.Count == 0 && group.Missing.Count == 0)
                {
                    continue;
                }

                var kinds = new Dictionary<string, int>(group.MissingKinds, StringComparer.Ordinal);
                foreach (int m in hidden)
                {
                    string kind = KindNames.Of(Nodes[m].Kind);
                    kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                }

                int count = hidden.Count + group.Missing.Count;
                string detail = string.Join(", ", kinds.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal)
                    .Select(k => $"{k.Value.ToString(CultureInfo.InvariantCulture)} {k.Key}"));
                if (group.Missing.Count > 0)
                {
                    detail += $"; {group.Missing.Count.ToString(CultureInfo.InvariantCulture)} not in this page";
                }

                More.Add(new ViewMore(
                    "m" + More.Count.ToString(CultureInfo.InvariantCulture), group.Of, group.Family, group.Incoming ? "in" : "out", group.Depth,
                    hidden, group.Missing.Count, $"+{count.ToString(CultureInfo.InvariantCulture)} more {Noun(group.Family, group.Incoming)}", detail));
            }
        }

        private void Count()
        {
            foreach (ViewEdge edge in Edges)
            {
                Nodes[edge.Dst].Refs++;
            }
        }

        private Bundle GroupFor(int of, string family, bool incoming, int depth)
        {
            Bundle? group = _groups.LastOrDefault();
            if (group is null || group.Of != of || group.Family != family || group.Incoming != incoming)
            {
                group = new Bundle(of, family, incoming, depth);
                _groups.Add(group);
            }

            return group;
        }

        // `subscribe, handler Game.Hud.OnDied`: how a `calls` edge uses its target (B1g).
        private static string? Use(Edge edge) =>
            Names.Prop(edge, "use") is not { } use ? null
            : Names.Prop(edge, "handler") is { } handler ? $"{use}, handler {Names.Qualified(handler)}"
            : Names.Prop(edge, "event") is { } listened ? $"{use}, {Names.Qualified(listened)}"
            : use;

        private void AddEdge(Edge edge, int src, int dst)
        {
            if (Family(edge) == "structure" || !_edgeSeen.Add(edge))
            {
                return;
            }

            string? note = edge.Kind switch
            {
                EdgeKind.References => Names.Prop(edge, "field"),
                EdgeKind.EventCalls => Names.Prop(edge, "event"),
                EdgeKind.OverridesValue => Names.Prop(edge, "path"),
                EdgeKind.Addressable => Names.Prop(edge, "address"),
                EdgeKind.Calls => Use(edge),
                _ => null,
            };
            Edges.Add(new ViewEdge(src, dst, edge.Kind, Family(edge), edge.Confidence != Confidence.Exact, note, Names.Location(edge)));
        }

        private int Add(Node node, int depth, bool root, bool container)
        {
            int id = Nodes.Count;
            _ids[node.Key] = id;
            Nodes.Add(new ViewNode
            {
                Id = id,
                Key = node.Key,
                Kind = node.Kind,
                Group = Group(node),
                Label = Label(node),
                Title = Title(node),
                What = Names.Kind(node),
                At = node.Kind == NodeKind.Asset ? Names.AssetPath(node) : Names.Location(node),
                Command = "ug explain " + Names.Quote(Explained(node)),
                Depth = depth,
                Root = root,
                Container = container,
            });
            return id;
        }

        private bool Passes(Node node) =>
            options.Kinds is null
            || options.Kinds.Contains(KindNames.Of(node.Kind))
            || (node.Kind == NodeKind.Asset && Names.Prop(node, "asset_type") is { } type && options.Kinds.Contains(type));

        private Node? Node(string key)
        {
            if (!_cache.TryGetValue(key, out Node? node))
            {
                node = store.GetNode(key);
                _cache[key] = node;
            }

            return node;
        }
    }

    // Short text on the node: a method with its parameters, an asset by file name, else the name.
    private static string Label(Node node) => node.Kind switch
    {
        NodeKind.Method => Names.Prop(node, "sig") ?? node.Name,
        NodeKind.Asset => Path.GetFileName(Names.AssetPath(node)),
        _ => node.Name,
    };

    // The full name in the side panel: qualified code names, an asset's path, a game object's hierarchy path.
    private static string Title(Node node) => node.Kind switch
    {
        NodeKind.Type or NodeKind.Method or NodeKind.Field or NodeKind.Property or NodeKind.Event => Names.Display(node),
        NodeKind.Asset => Names.AssetPath(node),
        NodeKind.GameObject => Names.Prop(node, "path") ?? node.Name,
        _ => node.Name,
    };

    // What `ug explain` resolves to exactly this node: a method with its parameters, an asset path, an object key.
    private static string Explained(Node node) => node.Kind switch
    {
        NodeKind.Type or NodeKind.Field or NodeKind.Property or NodeKind.Event => Names.Qualified(node.Key),
        NodeKind.Method => Names.Display(node),
        NodeKind.Asset => Names.AssetPath(node),
        _ => node.Key,
    };
}
