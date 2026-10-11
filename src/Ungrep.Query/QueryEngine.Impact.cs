using System.Globalization;
using Ungrep.Core.Graph;

namespace Ungrep.Query;

public sealed partial class QueryEngine
{
    private const int GroupRows = 10;
    private const int HolderLimit = 500;
    private const int ClosureLimit = 200;
    private const int ViaDepth = 3;

    // One row of an impact group: what depends on the symbol, where, and how sure the edge is.
    private sealed record ImpactRow(string Name, string File, int Line, bool Heuristic);

    // A group of rows under one label; `Data` groups hold serialized data, the rest hold code.
    private sealed class ImpactGroup(string label, bool data = false, bool heuristic = false)
    {
        public string Label { get; } = label;

        public bool Data { get; } = data;

        public bool Heuristic { get; } = heuristic;

        public string? Summary { get; set; }

        public List<ImpactRow> Rows { get; } = [];
    }

    /// <summary>
    /// `ug impact &lt;symbol&gt;`: what breaks if the symbol changes. Direct dependents grouped by how
    /// they depend, and the assets that hold serialized data for it (SPEC-query, `ug impact`).
    /// </summary>
    public string Impact(string text, bool json, int budget)
    {
        string command = "ug impact " + Names.Quote(text) + (json ? " --json" : "") + AllFlag;
        Answer answer = Start(budget, json);
        if (!Resolved("impact", text, answer, out IReadOnlyList<Node> resolved))
        {
            return json ? JsonAnswer.Error(answer.Finish(command)) : answer.Finish(command);
        }

        List<Node> symbols = [];
        foreach (Node node in resolved)
        {
            if (node.Kind is NodeKind.Type or NodeKind.Method or NodeKind.Field or NodeKind.Property or NodeKind.Event)
            {
                symbols.Add(node);
            }
            else if (IsScript(node))
            {
                symbols.AddRange(Out(node.Key).Where(e => e.Kind == EdgeKind.ScriptDefines).Select(e => Node(e.Dst)).OfType<Node>());
            }
        }

        if (symbols.Count == 0)
        {
            answer.Add("impact is for code symbols; use ug refs " + Names.Quote(text));
            return json ? JsonAnswer.Error(answer.Finish(command)) : answer.Finish(command);
        }

        var groups = new List<ImpactGroup>();
        foreach (Node symbol in symbols)
        {
            AddImpact(symbol, symbols.Count > 1 ? Names.Prop(symbol, "sig") ?? symbol.Name : null, groups);
        }

        groups = [.. Merge(groups).OrderBy(g => GroupOrder(g.Label))];
        string affects = Affects(groups);
        return json ? ImpactJson(symbols, affects, groups, command, budget) : ImpactText(symbols, affects, groups, command, budget);
    }

    private void AddImpact(Node symbol, string? overload, List<ImpactGroup> groups)
    {
        List<Edge> incoming = In(symbol.Key);
        if (symbol.Kind == NodeKind.Method)
        {
            incoming.AddRange(UntypedEventCalls(symbol));
        }

        string Named(string name) => overload is null ? name : name + " → " + overload;
        bool isInterface = Names.Prop(symbol, "subkind") == "interface";
        foreach (Edge edge in incoming)
        {
            (string label, string name) = edge.Kind switch
            {
                EdgeKind.Calls => Use(edge),
                EdgeKind.Dispatches => ("dispatched from", Names.Qualified(edge.Src)),
                EdgeKind.Overrides => (InInterface(symbol) ? "implemented by" : "overridden by", Names.Qualified(edge.Src)),
                EdgeKind.Inherits => (isInterface ? "extended by" : "subclasses", Names.Qualified(edge.Src)),
                EdgeKind.Implements => ("implemented by", Names.Qualified(edge.Src)),
                EdgeKind.UsesType => ("type uses", Names.Qualified(edge.Src)),
                EdgeKind.EventCalls => ("unity events", InFile(edge)),
                _ => ("", ""),
            };
            if (label.Length > 0)
            {
                Group(groups, label).Rows.Add(new ImpactRow(Named(name), edge.File, edge.Line, edge.Confidence != Confidence.Exact));
            }
        }

        foreach ((Edge edge, string via) in ImplementedVia(symbol))
        {
            Group(groups, "implemented by").Rows.Add(new ImpactRow(Named($"{Names.Qualified(edge.Src)} (via {via})"), edge.File, edge.Line, false));
        }

        foreach (Edge edge in Out(symbol.Key).Where(e => e.Kind == EdgeKind.Overrides))
        {
            Node? baseMember = Node(edge.Dst);
            Group(groups, "overrides").Rows.Add(new ImpactRow(Named(Names.Qualified(edge.Dst)), baseMember?.File ?? edge.File, baseMember?.LineStart ?? edge.Line, false));
        }

        if (symbol.Kind == NodeKind.Type)
        {
            AddTypeHolders(symbol, groups);
        }
        else if (symbol.Kind is NodeKind.Field or NodeKind.Property && Names.Flag(symbol, "serialized"))
        {
            AddFieldHolders(symbol, groups);
        }
    }

    // A `calls` edge by how its source uses the symbol (B1g): `Game.Hud.OnEnable, handler
    // Game.Hud.OnDied` on an event, `Game.Hud.OnEnable, to Game.Health.Died` on its handler.
    private static (string Label, string Name) Use(Edge edge)
    {
        string source = Names.Qualified(edge.Src);
        return Names.Prop(edge, "use") switch
        {
            "read" => ("readers", source),
            "write" => ("writers", source),
            "raise" => ("raised by", source),
            "subscribe" or "unsubscribe" => ("subscribers", OtherEnd(edge) is { } end ? source + ", " + end : source),
            _ => ("callers", source),
        };
    }

    // A Unity type's own holders, then the holders it reaches as serialized data of another type.
    private void AddTypeHolders(Node type, List<ImpactGroup> groups)
    {
        List<string> closure = TypeClosure(type.Key);
        List<Holder> own = HoldersOf(closure);
        if (own.Count > 0)
        {
            AddHolders(Group(groups, "serialized", data: true), own, $"{Count(own.Count, "component")} of {Names.Qualified(type.Key)}");
        }

        foreach ((string via, List<Holder> holders, bool heuristic) in ViaFields(type.Key, All ? int.MaxValue : ViaDepth, [type.Key]))
        {
            ImpactGroup group = Group(groups, "held via " + via, data: true, heuristic: heuristic);
            AddHolders(group, holders, $"{Count(holders.Count, "component")}");
        }

        foreach ((string via, List<Holder> holders) in SerializeReferenceHolders(type))
        {
            ImpactGroup group = Group(groups, "held via " + via, data: true, heuristic: true);
            AddHolders(group, holders, $"{Count(holders.Count, "component")}");
        }
    }

    // `[Serializable]` type T used as the type of a serialized field F in owner O: the holders of O
    // (and, when O has none because it is serializable data itself, of its owners, to a depth).
    private List<(string Via, List<Holder> Holders, bool Heuristic)> ViaFields(string typeKey, int depth, HashSet<string> seen)
    {
        var result = new List<(string, List<Holder>, bool)>();
        if (depth == 0)
        {
            // The walk stops here; a serialized field of this type would have led further.
            if (In(typeKey).Any(e => e.Kind == EdgeKind.UsesType && Node(e.Src) is { Kind: NodeKind.Field or NodeKind.Property } f && Names.Flag(f, "serialized")))
            {
                Cut("held via past 3 levels");
            }

            return result;
        }

        foreach (Edge use in In(typeKey).Where(e => e.Kind == EdgeKind.UsesType).DistinctBy(e => e.Src))
        {
            if (Node(use.Src) is not { Kind: NodeKind.Field or NodeKind.Property } field || !Names.Flag(field, "serialized"))
            {
                continue;
            }

            if (In(field.Key).FirstOrDefault(e => e.Kind == EdgeKind.Contains)?.Src is not { } owner || !seen.Add(owner))
            {
                continue;
            }

            List<Holder> holders = HoldersOf(TypeClosure(owner));
            if (holders.Count > 0)
            {
                result.Add((Names.Qualified(field.Key), holders, false));
            }
            else
            {
                foreach ((string via, List<Holder> further, bool heuristic) in ViaFields(owner, depth - 1, seen))
                {
                    result.Add((Names.Qualified(field.Key) + " > " + via, further, heuristic));
                }
            }
        }

        return result;
    }

    // A `[SerializeReference]` field typed with a base or interface of this type: its owner's
    // holders may hold an instance of this type.
    private List<(string Via, List<Holder> Holders)> SerializeReferenceHolders(Node type)
    {
        var result = new List<(string, List<Holder>)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string ancestor in Ancestors(type.Key))
        {
            foreach (Edge use in In(ancestor).Where(e => e.Kind == EdgeKind.UsesType).DistinctBy(e => e.Src))
            {
                if (Node(use.Src) is not { Kind: NodeKind.Field } field || !Names.Flag(field, "serialize_reference")
                    || In(field.Key).FirstOrDefault(e => e.Kind == EdgeKind.Contains)?.Src is not { } owner || !seen.Add(field.Key))
                {
                    continue;
                }

                List<Holder> holders = HoldersOf(TypeClosure(owner));
                if (holders.Count > 0)
                {
                    result.Add((Names.Qualified(field.Key) + " (SerializeReference)", holders));
                }
            }
        }

        return result;
    }

    private void AddFieldHolders(Node field, List<ImpactGroup> groups)
    {
        string? owner = In(field.Key).FirstOrDefault(e => e.Kind == EdgeKind.Contains)?.Src;
        if (owner is null)
        {
            return;
        }

        string current = field.Kind == NodeKind.Property ? $"<{field.Name}>k__BackingField" : field.Name;
        IReadOnlyList<string> former = Names.List(field, "former");
        List<Holder> holders = HoldersOf(TypeClosure(owner));
        if (holders.Count > 0)
        {
            AddHolders(Group(groups, "serialized", data: true, heuristic: true), holders, $"{Count(holders.Count, "component")} of {Names.Qualified(owner)}");
        }

        var overrides = new List<ImpactRow>();
        var data = new List<ImpactRow>();
        if (!All && holders.Count > HolderLimit)
        {
            Cut("holders past 500");
        }

        foreach (Holder holder in holders.Take(All ? int.MaxValue : HolderLimit))
        {
            foreach (Edge edge in In(holder.Key).Where(e => e.Kind == EdgeKind.OverridesValue))
            {
                if (FieldMatch(Names.Prop(edge, "path"), current, former) is { } heuristic)
                {
                    overrides.Add(new ImpactRow(Source(edge), edge.File, edge.Line, heuristic));
                }
            }

            foreach (Edge edge in Out(holder.Key).Where(e => e.Kind == EdgeKind.References))
            {
                if (FieldMatch(Names.Prop(edge, "field"), current, former) is { } heuristic)
                {
                    data.Add(new ImpactRow($"{Label(edge.Src)}: {Names.Prop(edge, "field")} -> {Label(edge.Dst)}", edge.File, edge.Line, heuristic));
                }
            }
        }

        Group(groups, "overridden in", data: true).Rows.AddRange(overrides);
        Group(groups, "field data", data: true).Rows.AddRange(data);
    }

    // Whether a serialized path names the field: false is an exact match on the current name, true
    // one on a former name (heuristic), null no match.
    private static bool? FieldMatch(string? path, string current, IReadOnlyList<string> former)
    {
        if (path is null)
        {
            return null;
        }

        int end = path.IndexOfAny(['.', '[']);
        string first = end < 0 ? path : path[..end];
        return first == current ? false : former.Contains(first, StringComparer.Ordinal) ? true : null;
    }

    private sealed record Holder(string File, int Line, string Type, string Key);

    // Components and ScriptableObjects whose script defines one of the types.
    private List<Holder> HoldersOf(IEnumerable<string> typeKeys)
    {
        var holders = new List<Holder>();
        foreach (string typeKey in typeKeys)
        {
            string name = Names.Qualified(typeKey);
            name = name[(name.LastIndexOf('.') + 1)..];
            foreach (Edge define in In(typeKey).Where(e => e.Kind == EdgeKind.ScriptDefines))
            {
                Page<EdgeHit> on = _store.EdgesTo(define.Src, EdgeKindSet.Of(EdgeKind.InstanceOf), EdgeCap);
                if (on.More)
                {
                    Cut(EdgesCut);
                }

                foreach (Edge edge in on.Items.Select(h => h.Edge))
                {
                    Node? source = Node(edge.Src);
                    if (source?.Kind != NodeKind.PrefabInstance)
                    {
                        holders.Add(new Holder(edge.File, source is { LineStart: > 0 } ? source.LineStart : edge.Line, name, edge.Src));
                    }
                }
            }
        }

        return holders;
    }

    // One row per file: the first component's line, and how many of which type.
    private static void AddHolders(ImpactGroup group, List<Holder> holders, string what)
    {
        foreach (IGrouping<string, Holder> file in holders.GroupBy(h => h.File).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            string types = string.Join(", ", file.GroupBy(h => h.Type).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key}"));
            group.Rows.Add(new ImpactRow(types, file.Key, file.Min(h => h.Line), false));
        }

        group.Summary = $"{what} in {Count(holders.Select(h => h.File).Distinct().Count(), "asset")}";
    }

    // The type and its subclasses, transitively: an instance of a subclass holds the base's fields.
    private List<string> TypeClosure(string typeKey)
    {
        var seen = new List<string> { typeKey };
        int i = 0;
        for (; i < seen.Count && seen.Count < ClosureCap; i++)
        {
            foreach (Edge edge in In(seen[i]).Where(e => e.Kind == EdgeKind.Inherits))
            {
                if (!seen.Contains(edge.Src))
                {
                    seen.Add(edge.Src);
                }
            }
        }

        ClosureCut(i, seen.Count);
        return seen;
    }

    private int ClosureCap => All ? int.MaxValue : ClosureLimit;

    // A closure stopped at 200 types with types left to walk.
    private void ClosureCut(int walked, int seen)
    {
        if (walked < seen)
        {
            Cut("type closures past 200 types");
        }
    }

    // Base classes and interfaces of the type, transitively, without the type itself.
    private List<string> Ancestors(string typeKey)
    {
        var seen = new List<string> { typeKey };
        int i = 0;
        for (; i < seen.Count && seen.Count < ClosureCap; i++)
        {
            foreach (Edge edge in Out(seen[i]).Where(e => e.Kind is EdgeKind.Inherits or EdgeKind.Implements))
            {
                if (!seen.Contains(edge.Dst))
                {
                    seen.Add(edge.Dst);
                }
            }
        }

        ClosureCut(i, seen.Count);
        return [.. seen.Skip(1)];
    }

    private static ImpactGroup Group(List<ImpactGroup> groups, string label, bool data = false, bool heuristic = false)
    {
        ImpactGroup? group = groups.FirstOrDefault(g => g.Label == label);
        if (group is null)
        {
            groups.Add(group = new ImpactGroup(label, data, heuristic));
        }

        return group;
    }

    // Overloads add rows to the same group; groups with no rows are dropped, rows ordered by name then line.
    private static IEnumerable<ImpactGroup> Merge(List<ImpactGroup> groups)
    {
        foreach (ImpactGroup group in groups.Where(g => g.Rows.Count > 0))
        {
            List<ImpactRow> rows = [.. group.Rows.Distinct().OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Line).ThenBy(r => r.Name, StringComparer.Ordinal)];
            if (!group.Data || group.Label is "overridden in" or "field data")
            {
                rows = [.. rows.OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Line)];
            }

            group.Rows.Clear();
            group.Rows.AddRange(rows);
            group.Summary ??= group.Rows.Count.ToString(CultureInfo.InvariantCulture);
            yield return group;
        }
    }

    private static int GroupOrder(string label) => label switch
    {
        "callers" => 0,
        "readers" => 1,
        "writers" => 2,
        "subscribers" => 3,
        "raised by" => 4,
        "dispatched from" => 5,
        "overridden by" or "implemented by" => 6,
        "overrides" => 7,
        "subclasses" or "extended by" => 8,
        "type uses" => 10,
        "unity events" => 11,
        "serialized" => 12,
        "overridden in" => 14,
        "field data" => 15,
        _ => 13,
    };

    // `3 places in 3 files: 2 callers, 1 unity event (1 heuristic)`, then what holds serialized data.
    private static string Affects(List<ImpactGroup> groups)
    {
        List<ImpactGroup> code = [.. groups.Where(g => !g.Data && g.Label != "overrides")];
        var parts = new List<string>();
        int places = code.Sum(g => g.Rows.Count);
        if (places > 0)
        {
            int files = code.SelectMany(g => g.Rows).Select(r => r.File).Distinct().Count();
            int heuristic = code.SelectMany(g => g.Rows).Count(r => r.Heuristic);
            parts.Add($"{Count(places, "place")} in {Count(files, "file")}: "
                + string.Join(", ", code.Select(g => Count(g.Rows.Count, Noun(g.Label)))) + (heuristic > 0 ? $" ({heuristic} heuristic)" : ""));
        }

        List<ImpactGroup> holders = [.. groups.Where(g => g.Data && g.Label is "serialized" || g.Label.StartsWith("held via", StringComparison.Ordinal))];
        string[] files2 = [.. holders.SelectMany(g => g.Rows).Select(r => r.File).Distinct().Order(StringComparer.Ordinal)];
        if (files2.Length > 0)
        {
            string kinds = string.Join(", ", files2.GroupBy(AssetKind).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => Count(g.Count(), g.Key)));
            bool maybe = holders.All(g => g.Heuristic);
            parts.Add($"{Count(files2.Length, "asset")} {(maybe ? "may hold" : "hold")} data ({kinds})");
        }

        int overrides = groups.Where(g => g.Label is "overridden in").Sum(g => g.Rows.Count);
        if (overrides > 0)
        {
            parts.Add(Count(overrides, "override"));
        }

        int data = groups.Where(g => g.Label is "field data").Sum(g => g.Rows.Count);
        if (data > 0)
        {
            parts.Add(Count(data, "reference") + " in field data");
        }

        return parts.Count == 0 ? "nothing in the graph depends on it" : string.Join("; ", parts);
    }

    private static string AssetKind(string file) => Path.GetExtension(file) switch
    {
        ".unity" => "scene",
        ".prefab" => "prefab",
        _ => "asset",
    };

    private static string Noun(string label) => label switch
    {
        "callers" => "caller",
        "readers" => "read",
        "writers" => "write",
        "subscribers" => "subscription",
        "raised by" => "raise",
        "dispatched from" => "dispatch",
        "overridden by" => "override",
        "implemented by" => "implementation",
        "subclasses" => "subclass",
        "extended by" => "extension",
        "type uses" => "type use",
        "unity events" => "unity event",
        _ => label,
    };

    private string ImpactText(List<Node> symbols, string affects, List<ImpactGroup> groups, string command, int budget)
    {
        int shown = All ? int.MaxValue : GroupRows;
        var answer = new Answer(budget, From);
        foreach (Node symbol in symbols)
        {
            string access = Names.Flag(symbol, "serialized") ? "  serialized" : "";
            answer.Add($"{Names.Display(symbol)}  {Names.Kind(symbol)}  {Names.Location(symbol)}{access}  [exact]");
        }

        int width = new[] { "affects", "note" }.Concat(groups.Select(g => g.Label)).Max(l => l.Length);
        string Head(string label, string summary) => "  " + label.PadRight(width) + "  " + summary;
        answer.Add(Head("affects", affects));
        foreach (ImpactGroup group in groups)
        {
            answer.Add(Head(group.Label, group.Summary + (group.Heuristic ? "  [heuristic]" : "")));
            answer.AddRange(Answer.Table([.. group.Rows.Take(shown).Select(r => Row(r))], "    "));
            if (group.Rows.Count > shown)
            {
                answer.Add($"    +{group.Rows.Count - shown} more  {command} --all");
            }
        }

        if (_cuts.Count > 0)
        {
            answer.Add(Head("note", $"cut: {string.Join(", ", _cuts)}: {command} --all"));
        }

        return answer.Finish(command, countDropped: true);

        static string[] Row(ImpactRow row) => row.Heuristic
            ? [row.Name, $"{row.File}:{row.Line}", "[heuristic]"]
            : [row.Name, $"{row.File}:{row.Line}"];
    }

    private string ImpactJson(List<Node> symbols, string affects, List<ImpactGroup> groups, string command, int budget)
    {
        int rows = All ? int.MaxValue : GroupRows;
        string? capped = _cuts.Count > 0 ? $"{string.Join(", ", _cuts)}: {command} --all" : null;
        int total = groups.Sum(g => Math.Min(g.Rows.Count, rows));
        return JsonAnswer.Fit(total, budget, (w, keep) =>
        {
            w.WriteStartObject();
            w.WriteStartArray("symbol");
            foreach (Node symbol in symbols)
            {
                w.WriteStartObject();
                w.WriteString("key", symbol.Key);
                w.WriteString("kind", Names.Kind(symbol));
                w.WriteString("location", Names.Location(symbol));
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteString("affects", affects);
            w.WriteStartArray("groups");
            int left = keep;
            foreach (ImpactGroup group in groups)
            {
                int shown = Math.Min(Math.Min(group.Rows.Count, rows), left);
                left -= shown;
                w.WriteStartObject();
                w.WriteString("label", group.Label);
                w.WriteNumber("count", group.Rows.Count);
                w.WriteString("summary", group.Summary);
                w.WriteString("confidence", group.Heuristic ? "heuristic" : "exact");
                w.WriteStartArray("rows");
                foreach (ImpactRow row in group.Rows.Take(shown))
                {
                    w.WriteStartObject();
                    w.WriteString("name", row.Name);
                    w.WriteString("file", row.File);
                    w.WriteNumber("line", row.Line);
                    w.WriteString("confidence", row.Heuristic ? "heuristic" : "exact");
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteNumber("more", group.Rows.Count - shown);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            if (capped is not null)
            {
                w.WriteString("capped", capped);
            }

            w.WriteBoolean("cut", keep < total);
            w.WriteEndObject();
        });
    }
}
