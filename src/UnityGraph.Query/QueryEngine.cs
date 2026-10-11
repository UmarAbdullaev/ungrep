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

    // `--all`: no cap but the graph's size (one less than int.MaxValue: the store reads one more to say `more`).
    private const int NoLimit = int.MaxValue - 1;

    private static readonly NodeKindSet _findKinds = NodeKindSet.Of(
        NodeKind.Type, NodeKind.Method, NodeKind.Field, NodeKind.Property, NodeKind.Event,
        NodeKind.Asset, NodeKind.GameObject, NodeKind.PrefabInstance);

    private readonly GraphStore _store;
    private readonly string? _projectRoot;
    private readonly Dictionary<string, Node?> _nodes = new(StringComparer.Ordinal);

    // What the current answer's capped reads left out (`edges past 2000`), for its `note  cut:` line.
    private readonly List<string> _cuts = [];

    // Whether the current answer cut a list to 8 items (`(+N)`).
    private bool _listCut;

    /// <param name="store">The open graph.</param>
    /// <param name="projectRoot">The Unity project folder, to turn an absolute path into a project path (`outline`).</param>
    public QueryEngine(GraphStore store, string? projectRoot = null)
    {
        _store = store;
        _projectRoot = projectRoot;
    }

    /// <summary>`--all` (SPEC-query, Nothing out of reach): no list, edge-read or walk caps.</summary>
    public bool All { get; init; }

    /// <summary>`--from`: a text answer skips its first lines (0: none); JSON answers do not page.</summary>
    public int From { get; init; }

    // ` --all` when asked, for the commands a cut answer names.
    private string AllFlag => All ? " --all" : "";

    private int EdgeCap => All ? NoLimit : EdgeLimit;

    public string Find(string text, NodeKind? kind, int budget)
    {
        string command = "ug find " + Names.Quote(text) + (kind is { } k ? " --kind " + KindNames.Of(k) : "") + AllFlag;
        Answer answer = Start(budget);

        // No asset is named by its GUID: a GUID finds its asset by key.
        string guid = text.Trim().ToLowerInvariant();
        if (kind is null or NodeKind.Asset && guid.Length == 32 && guid.All(char.IsAsciiHexDigitLower) && Node(NodeKeys.Asset(guid)) is { } asset)
        {
            answer.AddRange(Answer.Table([FindRow(asset)]));
            answer.Add("1 found");
            return answer.Finish(command);
        }

        Page<Node> page = _store.FindByName(text, kind is { } only ? NodeKindSet.Of(only) : _findKinds, All ? NoLimit : FindLimit);
        if (page.Items.Count == 0)
        {
            answer.Add("nothing found: " + text);
            return answer.Finish(command);
        }

        answer.AddRange(Answer.Table([.. page.Items.Select(FindRow)]));
        answer.Add(page.More ? $"{FindLimit}+ found, narrow the text" + (kind is null ? " or add --kind" : "") + $"; all: {command} --all" : $"{page.Items.Count} found");
        return answer.Finish(command);
    }

    public string Refs(string text, int budget)
    {
        string command = "ug refs " + Names.Quote(text) + AllFlag;
        Answer answer = Start(budget);
        if (!Resolved("refs", text, answer, out IReadOnlyList<Node> nodes))
        {
            return answer.Finish(command);
        }

        foreach (Node node in nodes)
        {
            answer.Add(Header(node));
        }

        var rows = new List<RefRow>();
        bool more = false;
        foreach (Node node in nodes)
        {
            string? target = nodes.Count > 1 ? "→ " + (Names.Prop(node, "sig") ?? node.Name) : null;
            foreach (Edge edge in Incoming(node, ref more))
            {
                (int order, string label) = IncomingLabel(edge, node);
                rows.Add(new RefRow(order, label, Source(edge), Label(edge.Src), edge, target, null));
            }

            foreach ((Edge edge, string via) in ImplementedVia(node))
            {
                rows.Add(new RefRow(4, "implemented by", Names.Qualified(edge.Src), Names.Qualified(edge.Src), edge, target, "via " + via));
            }
        }

        rows.Sort((a, b) => (a.Order, a.Label, a.Source, a.Edge.Line, string.Join(' ', Cells(a)))
            .CompareTo((b.Order, b.Label, b.Source, b.Edge.Line, string.Join(' ', Cells(b)))));
        (List<string[]> lines, int folded, int into) = All ? (rows.Select(Cells).ToList(), 0, 0) : Fold(rows);
        answer.AddRange(Answer.Table(lines, "  "));
        string count = more ? $"{rows.Count}+ references" : rows.Count == 1 ? "1 reference" : $"{rows.Count} references";
        answer.Add(count + (folded > 0 ? $", {folded} folded into {Count(into, "line")}" : "") + (more || folded > 0 ? $": {command} --all" : ""));
        return answer.Finish(command);
    }

    // A `refs` line: an edge in, or (B1c) a type implementing the interface through one extending it.
    private sealed record RefRow(int Order, string Label, string Source, string Owner, Edge Edge, string? Target, string? Via);

    private static string[] Cells(RefRow row)
    {
        var cells = new List<string> { row.Label, row.Source, Names.Location(row.Edge) };
        if (row.Target is not null)
        {
            cells.Add(row.Target);
        }

        if (row.Via is not null)
        {
            cells.Add(row.Via);
        }

        if (row.Edge.Confidence != Confidence.Exact)
        {
            cells.Add(Names.Confidence(row.Edge.Confidence));
        }

        return [.. cells];
    }

    // Four or more asset lines of one label from one file fold into one where the first of them
    // was (B1a: a prefab used 19 times in a data asset took 19 lines; which assets use it is the
    // question): `referenced by  19 fields  Assets/Data/Nodes/C1_Central.asset:68-265`.
    private static (List<string[]> Lines, int Folded, int Into) Fold(List<RefRow> rows)
    {
        var groups = rows.Where(r => FoldNoun(r.Label) is not null)
            .GroupBy(r => (r.Label, r.Edge.File, r.Target))
            .Where(g => g.Count() > 3)
            .ToDictionary(g => g.Key, g => g.ToList());
        var lines = new List<string[]>();
        var done = new HashSet<(string, string, string?)>();
        foreach (RefRow row in rows)
        {
            if (!groups.TryGetValue((row.Label, row.Edge.File, row.Target), out List<RefRow>? group))
            {
                lines.Add(Cells(row));
                continue;
            }

            if (!done.Add((row.Label, row.Edge.File, row.Target)))
            {
                continue;
            }

            List<string> owners = [.. group.Select(r => r.Owner).Distinct()];
            string counted = $"{group.Count} {FoldNoun(row.Label)}";
            int first = group.Min(r => r.Edge.Line);
            int last = group.Max(r => r.Edge.Line);
            var cells = new List<string>
            {
                row.Label,
                owners.Count == 1 && owners[0] != row.Edge.File ? owners[0] + ": " + counted : counted,
                first == last ? $"{row.Edge.File}:{first}" : $"{row.Edge.File}:{first}-{last}",
            };
            if (row.Target is not null)
            {
                cells.Add(row.Target);
            }

            int heuristic = group.Count(r => r.Edge.Confidence != Confidence.Exact);
            if (heuristic > 0)
            {
                cells.Add(heuristic == group.Count ? Names.Confidence(Confidence.Heuristic) : $"[{heuristic} heuristic]");
            }

            lines.Add([.. cells]);
        }

        return (lines, groups.Values.Sum(g => g.Count), groups.Count);
    }

    // What a folded line counts, by label; code lines (callers, users) do not fold.
    private static string? FoldNoun(string label) => label switch
    {
        "on" => "components",
        "unity event" => "events",
        "prefab instance" => "instances",
        "variant" => "variants",
        "referenced by" => "fields",
        "overridden in" => "overrides",
        "addressable entry" => "entries",
        _ => null,
    };

    // Types implementing an interface through interfaces that extend it, at any depth, with the
    // extending interface (B1c: which classes implement IPartRule took one `refs` per interface).
    // A type that also implements it directly is listed once, as the direct line.
    private List<(Edge Edge, string Via)> ImplementedVia(Node type)
    {
        var found = new List<(Edge, string)>();
        if (type.Kind != NodeKind.Type || Names.Prop(type, "subkind") != "interface")
        {
            return found;
        }

        var implementers = In(type.Key).Where(e => e.Kind == EdgeKind.Implements).Select(e => e.Src).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal) { type.Key };
        var queue = new Queue<string>([type.Key]);
        while (queue.Count > 0)
        {
            foreach (Edge extends in In(queue.Dequeue()).Where(e => e.Kind == EdgeKind.Inherits))
            {
                if (!seen.Add(extends.Src))
                {
                    continue;
                }

                queue.Enqueue(extends.Src);
                found.AddRange(In(extends.Src)
                    .Where(e => e.Kind == EdgeKind.Implements && implementers.Add(e.Src))
                    .Select(e => (e, Names.Qualified(extends.Src))));
            }
        }

        return found;
    }

    public string Explain(string text, bool members, int budget)
    {
        string command = "ug explain " + Names.Quote(text) + (members ? " --members" : "") + AllFlag;
        Answer answer = Start(budget);
        if (!Resolved("explain", text, answer, out IReadOnlyList<Node> nodes))
        {
            return answer.Finish(command);
        }

        Node node = nodes[0];
        switch (node.Kind)
        {
            case NodeKind.Type:
                ExplainType(node, members, answer);
                break;
            case NodeKind.Method or NodeKind.Field or NodeKind.Property or NodeKind.Event:
                ExplainMember(node, nodes.Count, text, answer);
                break;
            case NodeKind.Asset:
                ExplainAsset(node, answer);
                break;
            default:
                ExplainObject(node, answer);
                break;
        }

        // A cut list or edge read names `--all`; `ug refs` lists the incoming edges whole.
        string refs = "ug refs " + Names.Quote(text);
        string more = node.Kind == NodeKind.Type && !members ? $"ug explain {Names.Quote(text)} --members | {refs}" : refs;
        AddCutNote(answer, command, "  ");
        answer.Add("  more  " + (_listCut ? command + " --all | " : "") + more);
        return answer.Finish(command);
    }

    private void ExplainType(Node type, bool members, Answer answer)
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
            lines.Add(["also in", Join(otherFiles)]);
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

        lines.Add(["used by", Join(Sources(incoming, EdgeKind.UsesType))]);
        bool isInterface = Names.Prop(type, "subkind") == "interface";
        AddIfAny(lines, isInterface ? "extended by" : "subclasses", Sources(incoming, EdgeKind.Inherits));
        AddIfAny(lines, "implemented by", [.. Sources(incoming, EdgeKind.Implements)
            .Concat(ImplementedVia(type).Select(v => $"{Names.Qualified(v.Edge.Src)} (via {v.Via})").Order(StringComparer.Ordinal))]);
        List<string> scripts = [.. incoming.Where(e => e.Kind == EdgeKind.ScriptDefines).Select(e => e.Src)];
        AddIfAny(lines, "on", On(scripts));
        List<string> placed = PlacedIn(scripts);
        if (placed.Count > 0)
        {
            lines.Add(["instances in", Join(placed)]);
        }

        answer.AddRange(Answer.Table(lines, "  "));

        if (members)
        {
            answer.AddRange(Answer.Table([.. memberNodes.Select(MemberRow)], "    "));
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
            lines.Add(["called from", Join(Counted(incoming, EdgeKind.Calls))]);
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
            lines.Add(["calls", Join([.. outgoing.Where(e => e.Kind is EdgeKind.Calls or EdgeKind.Dispatches)
                .Select(e => Names.Qualified(e.Dst)).Distinct().Order(StringComparer.Ordinal)])]);
        }

        AddIfAny(lines, "flags", [.. Flags(member).Where(f => f != "serialized" && f != Names.Prop(member, "access"))]);
        answer.AddRange(Answer.Table(lines, "  "));
    }

    private void ExplainAsset(Node asset, Answer answer)
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

        List<Edge> inFile = [.. FileEdges(path, EdgeKindSet.All).Select(h => h.Edge)];
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

        var edgesCut = false;
        List<Edge> incoming = [.. Incoming(asset, ref edgesCut)];
        if (edgesCut)
        {
            Cut(EdgesCut);
        }

        lines.Add(["used by", Join([.. incoming.Where(e => e.Kind != EdgeKind.InstanceOf || !IsScript(asset))
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
    }

    private void ExplainObject(Node node, Answer answer)
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
        // In the order the inspector shows the fields: the order they are saved in.
        AddIfAny(lines, "references", [.. outgoing.Where(e => e.Kind == EdgeKind.References && !instance).OrderBy(e => e.Line)
            .Select(e => $"{Names.Prop(e, "field")} -> {Label(e.Dst)}")]);
        AddIfAny(lines, "unity events", [.. outgoing.Where(e => e.Kind == EdgeKind.EventCalls)
            .Select(e => $"{Names.Prop(e, "event")} -> {Names.Qualified(e.Dst)}" + (e.Confidence == Confidence.Exact ? "" : " [heuristic]"))]);
        answer.AddRange(Answer.Table(lines, "  "));
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
            Page<EdgeHit> page = _store.EdgesIntoAsset(node.Key["a:".Length..], EdgeKindSet.All, EdgeCap);
            more |= page.More;
            edges.AddRange(page.Items.Select(h => h.Edge).Where(e => e.File != content && e.File != node.File));
        }
        else
        {
            Page<EdgeHit> page = _store.EdgesTo(node.Key, EdgeKindSet.All, EdgeCap);
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
                    Page<EdgeHit> on = _store.EdgesTo(script, EdgeKindSet.Of(EdgeKind.InstanceOf), EdgeCap);
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

    /// <summary>
    /// The nodes <paramref name="text"/> names as one symbol, or null with <paramref name="answer"/>
    /// set to what `ug explain` says for an unknown or ambiguous symbol, naming `ug <paramref name="verb"/>`.
    /// </summary>
    public IReadOnlyList<Node>? Resolve(string verb, string text, int budget, out string answer)
    {
        Answer unresolved = Start(budget);
        if (Resolved(verb, text, unresolved, out IReadOnlyList<Node> nodes))
        {
            answer = "";
            return nodes;
        }

        answer = unresolved.Finish($"ug {verb} {Names.Quote(text)}{AllFlag}");
        return null;
    }

    private bool Resolved(string verb, string text, Answer answer, out IReadOnlyList<Node> nodes)
    {
        Resolution resolution = SymbolResolver.Resolve(_store, text, All);
        nodes = resolution.Nodes;
        if (resolution.Found)
        {
            return true;
        }

        if (resolution.Ambiguous)
        {
            answer.Add("ambiguous: " + text);
            answer.AddRange(Answer.Table([.. nodes.Take(All ? NoLimit : AmbiguousLimit).Select(FindRow)], "  "));
            if (!All && nodes.Count > AmbiguousLimit)
            {
                answer.Add($"  +{nodes.Count - AmbiguousLimit} more candidates: ug {verb} {Names.Quote(text)} --all");
            }

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
            case NodeKind.Component:
                return [Label(node.Key), Names.Kind(node), Names.Location(node), ""];
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

    // The scenes and prefabs that place an asset a script is on, as a prefab instance or a variant,
    // nested to any depth: where its components live besides the assets `on` names.
    private List<string> PlacedIn(IEnumerable<string> scripts)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (Edge on in scripts.SelectMany(s => In(s).Where(e => e.Kind == EdgeKind.InstanceOf)))
        {
            if (AssetOf(on.Src) is { } guid && seen.Add(NodeKeys.Asset(guid)))
            {
                queue.Enqueue(NodeKeys.Asset(guid));
            }
        }

        var placed = new List<string>();
        while (queue.Count > 0)
        {
            foreach (Edge edge in In(queue.Dequeue()))
            {
                bool places = edge.Kind == EdgeKind.VariantOf || (edge.Kind == EdgeKind.InstanceOf && Node(edge.Src)?.Kind == NodeKind.PrefabInstance);
                if (places && AssetOf(edge.Src) is { } holder && seen.Add(NodeKeys.Asset(holder)))
                {
                    placed.Add(Label(NodeKeys.Asset(holder)));
                    queue.Enqueue(NodeKeys.Asset(holder));
                }
            }
        }

        return [.. placed.Order(StringComparer.Ordinal)];
    }

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

    private void AddIfAny(List<string[]> lines, string label, IReadOnlyList<string> items)
    {
        if (items.Count > 0)
        {
            lines.Add([label, Join(items)]);
        }
    }

    private static List<string> Sources(List<Edge> edges, EdgeKind kind) =>
        [.. edges.Where(e => e.Kind == kind).Select(e => Names.Qualified(e.Src)).Distinct().Order(StringComparer.Ordinal)];

    // Callers with the number of files they call from, when more than one.
    private static List<string> Counted(List<Edge> edges, EdgeKind kind) =>
        [.. edges.Where(e => e.Kind == kind).GroupBy(e => Names.Qualified(e.Src)).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Count() > 1 ? $"{g.Key} ({g.Count()})" : g.Key)];

    private List<Edge> In(string key) => Edges(_store.EdgesTo(key, EdgeKindSet.All, EdgeCap));

    private List<Edge> Out(string key) => Edges(_store.EdgesFrom(key, EdgeKindSet.All, EdgeCap));

    private const string EdgesCut = "edges past 2000";

    private List<Edge> Edges(Page<EdgeHit> page)
    {
        if (page.More)
        {
            Cut(EdgesCut);
        }

        return [.. page.Items.Select(h => h.Edge)];
    }

    // The edges a file owns: at most 50,000 unless --all.
    private IReadOnlyList<EdgeHit> FileEdges(string path, EdgeKindSet kinds)
    {
        Page<EdgeHit> page = _store.EdgesInFile(path, kinds, All ? NoLimit : FileEdgeLimit);
        if (page.More)
        {
            Cut("file edges past 50000");
        }

        return page.Items;
    }

    // A new answer for a query: what the last one cut is forgotten; JSON answers do not page.
    private Answer Start(int budget, bool json = false)
    {
        _cuts.Clear();
        _listCut = false;
        return new Answer(budget, json ? null : From);
    }

    private void Cut(string what)
    {
        if (!_cuts.Contains(what))
        {
            _cuts.Add(what);
        }
    }

    // `  note  cut: holders past 500, edges past 2000: ug impact X --all` when a capped read left something out.
    private void AddCutNote(Answer answer, string command, string indent)
    {
        if (_cuts.Count > 0)
        {
            answer.Add($"{indent}note  cut: {string.Join(", ", _cuts)}: {command} --all");
        }
    }

    // A list of at most 8 items, then `(+N)`; every item with --all.
    private string Join(IReadOnlyList<string> items)
    {
        _listCut |= !All && items.Count > 8;
        return Names.Join(items, All ? int.MaxValue : 8);
    }

    // A list of at most <paramref name="max"/> items, then `(+N)`, named in the note line as <paramref name="cut"/>; every item with --all.
    private string Join(IReadOnlyList<string> items, int max, string cut)
    {
        if (!All && items.Count > max)
        {
            Cut(cut);
        }

        return Names.Join(items, All ? int.MaxValue : max);
    }

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
