using UnityGraph.Core.Graph;

namespace UnityGraph.Query;

/// <summary>
/// `ug find`, `ug refs`, `ug explain`, `ug outline` and `ug impact` over an open graph (SPEC-query,
/// Commands). Reads only; every answer is within the budget, deterministic for a given graph.
/// </summary>
public sealed partial class QueryEngine
{
    private const int FindLimit = 50;
    private const int AmbiguousLimit = 20;
    private const int EdgeLimit = 2000;
    private const int FileEdgeLimit = 50_000;

    private static readonly NodeKindSet _findKinds = NodeKindSet.Of(
        NodeKind.Type, NodeKind.Method, NodeKind.Field, NodeKind.Property, NodeKind.Event,
        NodeKind.Asset, NodeKind.GameObject, NodeKind.PrefabInstance);

    private readonly GraphStore _store;
    private readonly string? _projectRoot;
    private readonly Dictionary<string, Node?> _nodes = new(StringComparer.Ordinal);

    /// <param name="store">The open graph.</param>
    /// <param name="projectRoot">The Unity project folder, to turn an absolute path into a project path (`outline`).</param>
    public QueryEngine(GraphStore store, string? projectRoot = null)
    {
        _store = store;
        _projectRoot = projectRoot;
    }

    public string Find(string text, NodeKind? kind, int budget)
    {
        string command = "ug find " + Names.Quote(text) + (kind is { } k ? " --kind " + KindNames.Of(k) : "");
        var answer = new Answer(budget);
        Page<Node> page = _store.FindByName(text, kind is { } only ? NodeKindSet.Of(only) : _findKinds, FindLimit);
        if (page.Items.Count == 0)
        {
            answer.Add("nothing found: " + text);
            return answer.Finish(command);
        }

        answer.AddRange(Answer.Table([.. page.Items.Select(FindRow)]));
        answer.Add(page.More ? $"{FindLimit}+ found, narrow the text" + (kind is null ? " or add --kind" : "") : $"{page.Items.Count} found");
        return answer.Finish(command);
    }

    public string Refs(string text, int budget)
    {
        string command = "ug refs " + Names.Quote(text);
        var answer = new Answer(budget);
        if (!Resolved("refs", text, answer, out IReadOnlyList<Node> nodes))
        {
            return answer.Finish(command);
        }

        foreach (Node node in nodes)
        {
            answer.Add(Header(node));
        }

        var rows = new List<(int Order, string Label, string Source, int Line, string[] Row)>();
        bool more = false;
        foreach (Node node in nodes)
        {
            foreach (Edge edge in Incoming(node, ref more))
            {
                (int order, string label) = IncomingLabel(edge, node);
                string source = Source(edge);
                var row = new List<string> { label, source, Names.Location(edge) };
                if (nodes.Count > 1)
                {
                    row.Add("→ " + (Names.Prop(node, "sig") ?? node.Name));
                }

                if (edge.Confidence != Confidence.Exact)
                {
                    row.Add(Names.Confidence(edge.Confidence));
                }

                rows.Add((order, label, source, edge.Line, [.. row]));
            }
        }

        rows.Sort((a, b) => (a.Order, a.Label, a.Source, a.Line, string.Join(' ', a.Row))
            .CompareTo((b.Order, b.Label, b.Source, b.Line, string.Join(' ', b.Row))));
        answer.AddRange(Answer.Table([.. rows.Select(r => r.Row)], "  "));
        answer.Add(more ? $"{rows.Count}+ references" : rows.Count == 1 ? "1 reference" : $"{rows.Count} references");
        return answer.Finish(command);
    }

    public string Explain(string text, bool members, int budget)
    {
        string command = "ug explain " + Names.Quote(text) + (members ? " --members" : "");
        var answer = new Answer(budget);
        if (!Resolved("explain", text, answer, out IReadOnlyList<Node> nodes))
        {
            return answer.Finish(command);
        }

        Node node = nodes[0];
        switch (node.Kind)
        {
            case NodeKind.Type:
                ExplainType(node, members, text, answer);
                break;
            case NodeKind.Method or NodeKind.Field or NodeKind.Property or NodeKind.Event:
                ExplainMember(node, nodes.Count, text, answer);
                break;
            case NodeKind.Asset:
                ExplainAsset(node, text, answer);
                break;
            default:
                ExplainObject(node, text, answer);
                break;
        }

        return answer.Finish(command);
    }

    private void ExplainType(Node type, bool members, string text, Answer answer)
    {
        List<Edge> outgoing = Out(type.Key);
        List<Edge> incoming = In(type.Key);
        string bases = string.Join(", ", outgoing.Where(e => e.Kind is EdgeKind.Inherits or EdgeKind.Implements)
            .OrderBy(e => e.Kind).Select(e => Names.Qualified(e.Dst)));
        answer.Add($"{Names.Qualified(type.Key)}  {Names.Kind(type)}{(bases.Length > 0 ? " : " + bases : "")}  {Names.Location(type)}  [exact]");

        var lines = new List<string[]>();
        List<Edge> contains = outgoing.Where(e => e.Kind == EdgeKind.Contains).ToList();
        string[] otherFiles = [.. contains.Select(e => e.File).Where(f => f != type.File).Distinct().Order(StringComparer.Ordinal)];
        if (otherFiles.Length > 0)
        {
            lines.Add(["also in", Names.Join(otherFiles)]);
        }

        string? guid = incoming.Where(e => e.Kind == EdgeKind.ScriptDefines).Select(e => e.Src["a:".Length..]).FirstOrDefault();
        if (Names.Prop(type, "unity") is { } unity)
        {
            lines.Add(["unity", UnityKind(unity) + (guid is null ? "" : ", script guid " + guid)]);
        }
        else if (guid is not null)
        {
            lines.Add(["unity", "script guid " + guid]);
        }

        List<Node> memberNodes = [.. contains.Select(e => Node(e.Dst)).OfType<Node>().OrderBy(n => n.File, StringComparer.Ordinal).ThenBy(n => n.LineStart).ThenBy(n => n.Key, StringComparer.Ordinal)];
        AddIfAny(lines, "engine calls", [.. memberNodes.Where(n => Names.Flag(n, "message")).Select(n => n.Name)]);
        AddIfAny(lines, "entry points", [.. memberNodes.Where(n => Names.Prop(n, "entry") is not null).Select(n => $"{n.Name} [{Names.Prop(n, "entry")}]")]);
        if (Names.Prop(type, "entry") is { } typeEntry)
        {
            lines.Add(["entry", $"[{typeEntry}]"]);
        }

        AddIfAny(lines, "serialized", [.. memberNodes.Where(n => Names.Flag(n, "serialized")).Select(n => $"{n.Name}: {Names.Prop(n, "type")}")]);
        if (memberNodes.Count > 0)
        {
            lines.Add(["members", string.Join(", ", memberNodes.GroupBy(n => n.Kind).OrderBy(g => g.Key).Select(g => Count(g.Count(), KindNames.Of(g.Key))))]);
        }

        lines.Add(["used by", Names.Join(Sources(incoming, EdgeKind.UsesType))]);
        bool isInterface = Names.Prop(type, "subkind") == "interface";
        AddIfAny(lines, isInterface ? "extended by" : "subclasses", Sources(incoming, EdgeKind.Inherits));
        AddIfAny(lines, "implemented by", Sources(incoming, EdgeKind.Implements));
        AddIfAny(lines, "on", On(incoming.Where(e => e.Kind == EdgeKind.ScriptDefines).Select(e => e.Src)));
        answer.AddRange(Answer.Table(lines, "  "));

        if (members)
        {
            answer.AddRange(Answer.Table([.. memberNodes.Select(MemberRow)], "    "));
            answer.Add("  more  ug refs " + Names.Quote(text));
        }
        else
        {
            answer.Add($"  more  ug explain {Names.Quote(text)} --members | ug refs {Names.Quote(text)}");
        }
    }

    private void ExplainMember(Node member, int overloads, string text, Answer answer)
    {
        string access = Names.Prop(member, "access") is { } a && a != "private" ? "  " + a : "";
        answer.Add($"{Names.Display(member)}  {Names.Kind(member)}  {Names.Location(member)}{access}  [exact]");

        List<Edge> incoming = In(member.Key);
        List<Edge> outgoing = Out(member.Key);
        var lines = new List<string[]>();
        if (incoming.FirstOrDefault(e => e.Kind == EdgeKind.Contains) is { } parent)
        {
            lines.Add(["in", Names.Qualified(parent.Src)]);
        }

        if (overloads > 1)
        {
            lines.Add(["overloads", $"{overloads}: ug refs {Names.Quote(text)} lists them all"]);
        }

        if (member.Kind != NodeKind.Method && Names.Prop(member, "type") is { } type)
        {
            lines.Add(["type", type]);
        }

        if (Names.Flag(member, "serialized"))
        {
            IReadOnlyList<string> former = Names.List(member, "former");
            lines.Add(["serialized", former.Count == 0 ? "yes" : "yes, formerly " + string.Join(", ", former)]);
        }

        AddIfAny(lines, "overrides", [.. outgoing.Where(e => e.Kind == EdgeKind.Overrides).Select(e => Names.Qualified(e.Dst)).Distinct().Order(StringComparer.Ordinal)]);
        if (member.Kind != NodeKind.Field)
        {
            lines.Add(["called from", Names.Join(Counted(incoming, EdgeKind.Calls))]);
        }

        AddIfAny(lines, "dispatched from", [.. Counted(incoming, EdgeKind.Dispatches).Select(s => s + " [heuristic]")]);
        if (member.Kind == NodeKind.Method)
        {
            IEnumerable<Edge> events = incoming.Concat(UntypedEventCalls(member)).Where(e => e.Kind == EdgeKind.EventCalls);
            AddIfAny(lines, "unity events", [.. events.Select(e => InFile(e) + (e.Confidence == Confidence.Exact ? "" : " [heuristic]"))
                .Distinct().Order(StringComparer.Ordinal)]);
        }

        AddIfAny(lines, InInterface(member) ? "implemented by" : "overridden by", Sources(incoming, EdgeKind.Overrides));
        if (member.Kind is NodeKind.Method or NodeKind.Property or NodeKind.Event)
        {
            lines.Add(["calls", Names.Join([.. outgoing.Where(e => e.Kind is EdgeKind.Calls or EdgeKind.Dispatches)
                .Select(e => Names.Qualified(e.Dst)).Distinct().Order(StringComparer.Ordinal)])]);
        }

        AddIfAny(lines, "flags", [.. Flags(member).Where(f => f != "serialized" && f != Names.Prop(member, "access"))]);
        answer.AddRange(Answer.Table(lines, "  "));
        answer.Add("  more  ug refs " + Names.Quote(text));
    }

    private void ExplainAsset(Node asset, string text, Answer answer)
    {
        string path = Names.AssetPath(asset);
        answer.Add(Header(asset));
        var lines = new List<string[]>();
        List<Edge> outgoing = Out(asset.Key);
        AddIfAny(lines, "defines", [.. outgoing.Where(e => e.Kind == EdgeKind.ScriptDefines).Select(e => Names.Qualified(e.Dst)).Order(StringComparer.Ordinal)]);

        List<Node> objects = IsScript(asset) ? [] : [.. _store.KeysInFile(path).Select(Node).OfType<Node>()];
        if (objects.Count > 0)
        {
            lines.Add(["objects", string.Join(", ", objects.GroupBy(n => n.Kind).OrderBy(g => g.Key).Select(g => Count(g.Count(), KindNames.Of(g.Key).Replace('_', ' '))))]);
        }

        List<Edge> inFile = [.. _store.EdgesInFile(path, EdgeKindSet.All, FileEdgeLimit).Items.Select(h => h.Edge)];
        string guid = asset.Key["a:".Length..];
        AddIfAny(lines, "scripts", [.. inFile.Where(e => e.Kind == EdgeKind.InstanceOf && Node(e.Src)?.Kind != NodeKind.PrefabInstance)
            .Select(ScriptName).Distinct().Order(StringComparer.Ordinal)]);
        AddIfAny(lines, "variant of", [.. inFile.Where(e => e.Kind == EdgeKind.VariantOf).Select(e => Label(e.Dst)).Distinct().Order(StringComparer.Ordinal)]);
        var bases = inFile.Where(e => e.Kind == EdgeKind.VariantOf).Select(e => e.Dst).ToHashSet(StringComparer.Ordinal);
        AddIfAny(lines, "prefabs", [.. inFile.Where(e => e.Kind == EdgeKind.InstanceOf && Node(e.Src)?.Kind == NodeKind.PrefabInstance && !bases.Contains(e.Dst))
            .Select(e => Label(e.Dst)).Distinct().Order(StringComparer.Ordinal)]);
        AddIfAny(lines, "uses", [.. inFile.Where(e => e.Kind == EdgeKind.References && AssetOf(e.Dst) is { } other && other != guid)
            .Select(e => Label(NodeKeys.Asset(AssetOf(e.Dst)!))).Distinct().Order(StringComparer.Ordinal)]);
        AddIfAny(lines, "unity events", [.. inFile.Where(e => e.Kind == EdgeKind.EventCalls)
            .Select(e => Names.Qualified(e.Dst) + (e.Confidence == Confidence.Exact ? "" : " [heuristic]")).Distinct().Order(StringComparer.Ordinal)]);

        var ignored = false;
        List<Edge> incoming = [.. Incoming(asset, ref ignored)];
        lines.Add(["used by", Names.Join([.. incoming.Where(e => e.Kind != EdgeKind.InstanceOf || !IsScript(asset))
            .Select(e => e.File).Distinct().Order(StringComparer.Ordinal)])]);
        foreach (Edge entry in outgoing.Where(e => e.Kind == EdgeKind.Addressable))
        {
            IReadOnlyList<string> labels = Names.List(entry, "labels");
            lines.Add(["addressable", $"\"{Names.Prop(entry, "address")}\" in {Label(entry.Dst)}" + (labels.Count == 0 ? "" : ", labels " + string.Join(", ", labels))]);
        }

        if (IsScript(asset))
        {
            AddIfAny(lines, "on", On([asset.Key]));
        }

        answer.AddRange(Answer.Table(lines, "  "));
        answer.Add("  more  ug refs " + Names.Quote(text));
    }

    private void ExplainObject(Node node, string text, Answer answer)
    {
        answer.Add(Header(node));
        var lines = new List<string[]>();
        List<Edge> outgoing = Out(node.Key);
        List<Edge> incoming = In(node.Key);
        if (AssetOf(node.Key) is { } guid)
        {
            lines.Add(["in", Label(NodeKeys.Asset(guid))]);
        }

        AddIfAny(lines, "parent", [.. outgoing.Where(e => e.Kind == EdgeKind.ChildOf).Select(e => Label(e.Dst))]);
        AddIfAny(lines, "on", [.. incoming.Where(e => e.Kind == EdgeKind.Contains && Node(e.Src)?.Kind == NodeKind.GameObject).Select(e => Label(e.Src))]);
        AddIfAny(lines, "components", [.. outgoing.Where(e => e.Kind == EdgeKind.Contains).Select(e => Node(e.Dst)?.Name ?? e.Dst)]);
        AddIfAny(lines, "children", [.. incoming.Where(e => e.Kind == EdgeKind.ChildOf).Select(e => Label(e.Src)).Order(StringComparer.Ordinal)]);
        bool instance = node.Kind == NodeKind.PrefabInstance;
        AddIfAny(lines, instance ? "instance of" : "script", [.. outgoing.Where(e => e.Kind == EdgeKind.InstanceOf).Select(e => instance ? Label(e.Dst) : ScriptName(e))]);
        AddIfAny(lines, "overrides", [.. outgoing.Where(e => e.Kind == EdgeKind.OverridesValue).Select(Override)]);
        AddIfAny(lines, "references", [.. outgoing.Where(e => e.Kind == EdgeKind.References && !instance).Select(e => $"{Names.Prop(e, "field")} -> {Label(e.Dst)}")]);
        AddIfAny(lines, "unity events", [.. outgoing.Where(e => e.Kind == EdgeKind.EventCalls)
            .Select(e => $"{Names.Prop(e, "event")} -> {Names.Qualified(e.Dst)}" + (e.Confidence == Confidence.Exact ? "" : " [heuristic]"))]);
        answer.AddRange(Answer.Table(lines, "  "));
        answer.Add("  more  ug refs " + Names.Quote(text));
    }

    // Every edge into the symbol that `refs` lists: for an asset, edges into it or its objects
    // from other files; for a type, also the objects its script is on; for a method, also
    // UnityEvent calls that name it without parameters.
    private IEnumerable<Edge> Incoming(Node node, ref bool more)
    {
        var edges = new List<Edge>();
        if (node.Kind == NodeKind.Asset)
        {
            string content = Names.AssetPath(node);
            Page<EdgeHit> page = _store.EdgesIntoAsset(node.Key["a:".Length..], EdgeKindSet.All, EdgeLimit);
            more |= page.More;
            edges.AddRange(page.Items.Select(h => h.Edge).Where(e => e.File != content && e.File != node.File));
        }
        else
        {
            Page<EdgeHit> page = _store.EdgesTo(node.Key, EdgeKindSet.All, EdgeLimit);
            more |= page.More;
            edges.AddRange(page.Items.Select(h => h.Edge));
            if (node.Kind == NodeKind.Method)
            {
                edges.AddRange(UntypedEventCalls(node));
            }

            if (node.Kind == NodeKind.Type)
            {
                foreach (string script in edges.Where(e => e.Kind == EdgeKind.ScriptDefines).Select(e => e.Src).ToList())
                {
                    Page<EdgeHit> on = _store.EdgesTo(script, EdgeKindSet.Of(EdgeKind.InstanceOf), EdgeLimit);
                    more |= on.More;
                    edges.AddRange(on.Items.Select(h => h.Edge));
                }
            }
        }

        return edges.Where(e => e.Kind != EdgeKind.Contains);
    }

    // A dynamic UnityEvent call names its method without parameters (SPEC-asset-index, event_calls).
    private List<Edge> UntypedEventCalls(Node method)
    {
        int paren = method.Key.IndexOf('(', StringComparison.Ordinal);
        return paren < 0 ? [] : In(method.Key[..paren]).Where(e => e.Kind == EdgeKind.EventCalls).ToList();
    }

    private bool Resolved(string verb, string text, Answer answer, out IReadOnlyList<Node> nodes)
    {
        Resolution resolution = SymbolResolver.Resolve(_store, text);
        nodes = resolution.Nodes;
        if (resolution.Found)
        {
            return true;
        }

        if (resolution.Ambiguous)
        {
            answer.Add("ambiguous: " + text);
            answer.AddRange(Answer.Table([.. nodes.Take(AmbiguousLimit).Select(FindRow)], "  "));
            answer.Add($"  more  ug {verb} {Names.Quote(Name(nodes[0]))}");
            return false;
        }

        answer.Add("not found: " + text);
        string last = text.Contains('/', StringComparison.Ordinal) ? text[(text.LastIndexOf('/') + 1)..] : text.Split('(')[0].Split('.')[^1];
        if (last.Length > 0)
        {
            Page<Node> near = _store.FindByName(last, _findKinds, 5);
            answer.AddRange(Answer.Table([.. near.Items.Select(FindRow)], "  "));
        }

        return false;
    }

    private string Header(Node node) => node.Kind switch
    {
        NodeKind.Asset => $"{Names.AssetPath(node)}  {Names.Kind(node)}  guid {node.Key["a:".Length..]}  [exact]",
        NodeKind.Type or NodeKind.Method or NodeKind.Field or NodeKind.Property or NodeKind.Event
            => $"{Names.Display(node)}  {Names.Kind(node)}  {Names.Location(node)}  [exact]",
        _ => $"{Label(node.Key)}  {Names.Kind(node)}  {Names.Location(node)}  [exact]",
    };

    // How a node is named in commands: the qualified name, or an asset's path, or its key.
    private static string Name(Node node) => node.Kind switch
    {
        NodeKind.Asset => Names.AssetPath(node),
        NodeKind.Type or NodeKind.Method or NodeKind.Field or NodeKind.Property or NodeKind.Event => Names.Qualified(node.Key),
        _ => node.Key,
    };

    private string[] FindRow(Node node)
    {
        switch (node.Kind)
        {
            case NodeKind.Asset:
                return [Names.AssetPath(node), Names.Kind(node), "guid " + node.Key["a:".Length..]];
            case NodeKind.GameObject:
                return [Label(node.Key), Names.Kind(node), Names.Location(node), Names.Prop(node, "active") == "false" ? "inactive" : ""];
            case NodeKind.PrefabInstance:
                return [Label(node.Key), Names.Kind(node), Names.Location(node), Names.Prop(node, "source") is { } source ? "of " + Label(NodeKeys.Asset(source)) : ""];
        }

        var detail = new List<string>();
        if (Names.Prop(node, "sig") is { } sig)
        {
            detail.Add(sig);
        }
        else if (Names.Prop(node, "type") is { } type)
        {
            detail.Add(type);
        }

        if (Names.Flag(node, "serialized"))
        {
            detail.Add("serialized");
        }

        return [Names.Qualified(node.Key), Names.Kind(node), Names.Location(node), string.Join(", ", detail)];
    }

    /// <summary>
    /// A key as answers show it: code by qualified name, an asset by path, a game object by its
    /// hierarchy path, a component as `game object > Name`; a key with no node as itself.
    /// </summary>
    private string Label(string key)
    {
        if (key.StartsWith("cs:", StringComparison.Ordinal) || Node(key) is not { } node)
        {
            return Names.Qualified(key);
        }

        return node.Kind switch
        {
            NodeKind.Asset => Names.AssetPath(node),
            NodeKind.GameObject => Names.Prop(node, "path") ?? node.Name,
            NodeKind.Component => In(key).FirstOrDefault(e => e.Kind == EdgeKind.Contains) is { } owner && Node(owner.Src)?.Kind == NodeKind.GameObject
                ? Label(owner.Src) + " > " + node.Name
                : node.Name,
            _ => node.Name,
        };
    }

    // An edge's source with what the edge says about it: the field, event or overridden value.
    private string Source(Edge edge)
    {
        string source = Label(edge.Src);
        return edge.Kind switch
        {
            EdgeKind.References when Names.Prop(edge, "field") is { } field => source + ": " + field,
            EdgeKind.EventCalls when Names.Prop(edge, "event") is { } name => source + ": " + name,
            EdgeKind.OverridesValue => source + ": " + Override(edge),
            EdgeKind.Addressable when Names.Prop(edge, "address") is { } address => $"{source} \"{address}\"",
            _ => source,
        };
    }

    private string Override(Edge edge) =>
        Names.Prop(edge, "path") + (Names.Prop(edge, "ref") is { } target ? " -> " + Label(target) : " = " + Names.Prop(edge, "value"));

    // An object with the file it is in: `Lab.unity > Canvas/Open Button > Button: m_OnClick`.
    private string InFile(Edge edge)
    {
        string source = Source(edge);
        return edge.Src.StartsWith("a:", StringComparison.Ordinal) ? source : Path.GetFileName(edge.File) + " > " + source;
    }

    // Where the scripts <paramref name="scripts"/> (asset keys) are used: components and
    // ScriptableObjects with an `instance_of` edge to one of them.
    private List<string> On(IEnumerable<string> scripts) =>
        [.. scripts.SelectMany(s => In(s).Where(e => e.Kind == EdgeKind.InstanceOf))
            .Select(e => e.Src.StartsWith("a:", StringComparison.Ordinal) ? Label(e.Src) : Path.GetFileName(e.File) + " > " + Label(e.Src))
            .Distinct().Order(StringComparer.Ordinal)];

    // The type an `instance_of` edge's script defines; for a script the graph has no code for
    // (a package DLL), the class the object names, else the script's GUID.
    private string ScriptName(Edge instanceOf) =>
        Out(instanceOf.Dst).FirstOrDefault(e => e.Kind == EdgeKind.ScriptDefines) is { } defines ? Names.Qualified(defines.Dst)
        : Node(instanceOf.Src) is { } source && Names.Prop(source, "class") is { } name ? name
        : "script guid " + AssetOf(instanceOf.Dst);

    private static bool IsScript(Node node) => node.Kind == NodeKind.Asset && Names.Prop(node, "asset_type") == "script";

    // The GUID of an `a:` or `o:` key.
    private static string? AssetOf(string key) =>
        key.StartsWith("a:", StringComparison.Ordinal) ? key[2..]
        : key.StartsWith("o:", StringComparison.Ordinal) && key.IndexOf(':', 2) is > 2 and var end ? key[2..end]
        : null;

    private static string[] MemberRow(Node node) =>
        [node.Name, Names.Kind(node), node.LineStart.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join("  ", new[] { Names.Prop(node, "sig") ?? Names.Prop(node, "type") ?? "" }.Concat(Flags(node)).Where(s => s.Length > 0))];

    private static List<string> Flags(Node node)
    {
        var flags = new List<string>();
        if (Names.Prop(node, "access") is { } access && access != "private")
        {
            flags.Add(access);
        }

        foreach (string flag in new[] { "static", "abstract", "virtual", "override", "const", "readonly", "serialized", "message" })
        {
            if (Names.Flag(node, flag))
            {
                flags.Add(flag);
            }
        }

        if (Names.Prop(node, "entry") is { } entry)
        {
            flags.Add($"[{entry}]");
        }

        return flags;
    }

    private (int Order, string Label) IncomingLabel(Edge edge, Node target) => edge.Kind switch
    {
        EdgeKind.Calls => (0, "called from"),
        EdgeKind.Dispatches => (1, "dispatched from"),
        EdgeKind.Overrides => (2, InInterface(target) ? "implemented by" : "overridden by"),
        EdgeKind.Inherits => (3, Names.Prop(target, "subkind") == "interface" ? "extended by" : "subclass"),
        EdgeKind.Implements => (4, "implemented by"),
        EdgeKind.UsesType => (5, "used by"),
        EdgeKind.ScriptDefines => (6, "script asset"),
        EdgeKind.InstanceOf when Node(edge.Src)?.Kind == NodeKind.PrefabInstance => (9, "prefab instance"),
        EdgeKind.InstanceOf => (7, "on"),
        EdgeKind.EventCalls => (8, "unity event"),
        EdgeKind.VariantOf => (10, "variant"),
        EdgeKind.References => (11, "referenced by"),
        EdgeKind.OverridesValue => (12, "overridden in"),
        EdgeKind.Addressable => (13, "addressable entry"),
        _ => (14, KindNames.Of(edge.Kind) + " from"),
    };

    private bool InInterface(Node member) =>
        In(member.Key).FirstOrDefault(e => e.Kind == EdgeKind.Contains) is { } parent
        && Node(parent.Src) is { } type && Names.Prop(type, "subkind") == "interface";

    private static string UnityKind(string unity) => unity switch
    {
        "mono_behaviour" => "MonoBehaviour",
        "scriptable_object" => "ScriptableObject",
        "editor" => "Editor",
        "editor_window" => "EditorWindow",
        "property_drawer" => "PropertyDrawer",
        "serializable" => "[Serializable]",
        "object" => "UnityEngine.Object",
        _ => unity,
    };

    private static string Count(int count, string noun) =>
        count == 1 ? $"1 {noun}" : noun.EndsWith('y') ? $"{count} {noun[..^1]}ies" : $"{count} {noun}s";

    private static void AddIfAny(List<string[]> lines, string label, IReadOnlyList<string> items)
    {
        if (items.Count > 0)
        {
            lines.Add([label, Names.Join(items)]);
        }
    }

    private static List<string> Sources(List<Edge> edges, EdgeKind kind) =>
        [.. edges.Where(e => e.Kind == kind).Select(e => Names.Qualified(e.Src)).Distinct().Order(StringComparer.Ordinal)];

    // Callers with the number of files they call from, when more than one.
    private static List<string> Counted(List<Edge> edges, EdgeKind kind) =>
        [.. edges.Where(e => e.Kind == kind).GroupBy(e => Names.Qualified(e.Src)).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Count() > 1 ? $"{g.Key} ({g.Count()})" : g.Key)];

    private List<Edge> In(string key) => [.. _store.EdgesTo(key, EdgeKindSet.All, EdgeLimit).Items.Select(h => h.Edge)];

    private List<Edge> Out(string key) => [.. _store.EdgesFrom(key, EdgeKindSet.All, EdgeLimit).Items.Select(h => h.Edge)];

    private Node? Node(string key)
    {
        if (!_nodes.TryGetValue(key, out Node? node))
        {
            node = _store.GetNode(key);
            _nodes[key] = node;
        }

        return node;
    }
}
