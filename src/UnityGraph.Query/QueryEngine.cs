using UnityGraph.Core.Graph;

namespace UnityGraph.Query;

/// <summary>
/// `ug find`, `ug refs` and `ug explain` over an open graph (SPEC-query, Commands). Reads only;
/// every answer is plain text within the budget, deterministic for a given graph.
/// </summary>
public sealed class QueryEngine
{
    private const int FindLimit = 50;
    private const int AmbiguousLimit = 20;
    private const int EdgeLimit = 2000;

    private static readonly NodeKindSet _symbolKinds = NodeKindSet.Of(NodeKind.Type, NodeKind.Method, NodeKind.Field, NodeKind.Property, NodeKind.Event);

    private readonly GraphStore _store;
    private readonly Dictionary<string, Node?> _nodes = new(StringComparer.Ordinal);

    public QueryEngine(GraphStore store) => _store = store;

    public string Find(string text, NodeKind? kind, int budget)
    {
        string command = "ug find " + Names.Quote(text) + (kind is { } k ? " --kind " + KindNames.Of(k) : "");
        var answer = new Answer(budget);
        Page<Node> page = _store.FindByName(text, kind is { } only ? NodeKindSet.Of(only) : _symbolKinds, FindLimit);
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
            answer.Add($"{Names.Display(node)}  {Names.Kind(node)}  {Names.Location(node)}  [exact]");
        }

        var rows = new List<(int Order, string Label, string Source, int Line, string[] Row)>();
        bool more = false;
        foreach (Node node in nodes)
        {
            Page<EdgeHit> page = _store.EdgesTo(node.Key, EdgeKindSet.All, EdgeLimit);
            more |= page.More;
            foreach (Edge edge in page.Items.Select(h => h.Edge).Where(e => e.Kind != EdgeKind.Contains))
            {
                (int order, string label) = IncomingLabel(edge.Kind, node);
                string source = Names.Qualified(edge.Src);
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
        if (node.Kind == NodeKind.Type)
        {
            ExplainType(node, members, text, answer);
        }
        else
        {
            ExplainMember(node, nodes.Count, text, answer);
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
            answer.Add($"  more  ug {verb} {Names.Quote(Names.Qualified(nodes[0].Key))}");
            return false;
        }

        answer.Add("not found: " + text);
        string last = text.Split('(')[0].Split('.')[^1];
        if (last.Length > 0)
        {
            Page<Node> near = _store.FindByName(last, _symbolKinds, 5);
            answer.AddRange(Answer.Table([.. near.Items.Select(FindRow)], "  "));
        }

        return false;
    }

    private static string[] FindRow(Node node)
    {
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

    private (int Order, string Label) IncomingLabel(EdgeKind kind, Node target) => kind switch
    {
        EdgeKind.Calls => (0, "called from"),
        EdgeKind.Dispatches => (1, "dispatched from"),
        EdgeKind.Overrides => (2, InInterface(target) ? "implemented by" : "overridden by"),
        EdgeKind.Inherits => (3, Names.Prop(target, "subkind") == "interface" ? "extended by" : "subclass"),
        EdgeKind.Implements => (4, "implemented by"),
        EdgeKind.UsesType => (5, "used by"),
        EdgeKind.ScriptDefines => (6, "script asset"),
        _ => (7, KindNames.Of(kind) + " from"),
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
