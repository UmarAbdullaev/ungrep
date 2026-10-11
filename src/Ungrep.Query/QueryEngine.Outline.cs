using System.Globalization;
using Ungrep.Core.Graph;

namespace Ungrep.Query;

public sealed partial class QueryEngine
{
    private const int ComponentNames = 6;

    // One row of an outline: a type, member, game object or prefab instance with its file lines.
    private sealed record OutlineRow(int Depth, string Kind, string Name, int Start, int End, string Detail);

    private sealed record OutlineModel(string Path, string Kind, string Counts, List<OutlineRow> Rows, string? Note = null);

    /// <summary>
    /// `ug outline &lt;file&gt;`: the shape of a `.cs` file from the code graph, or the object tree of
    /// a scene, prefab or other Unity YAML asset from the asset graph, with the file lines to read.
    /// </summary>
    public string Outline(string text, bool json, int budget)
    {
        string command = "ug outline " + Names.Quote(text) + (json ? " --json" : "") + AllFlag;
        Answer answer = Start(budget, json);
        string? path = ProjectPath(text, answer);
        OutlineModel? model = null;
        if (path is not null)
        {
            model = CodeFile(path) is { } code ? code : OutlineOf(path, text, answer);
        }

        if (model is null)
        {
            return json ? JsonAnswer.Error(answer.Finish(command)) : answer.Finish(command);
        }

        return json ? OutlineJson(model, command, budget) : OutlineText(model, command, budget);
    }

    // The text as a project path, or null (with the reason in answer) when it points outside the project.
    private string? ProjectPath(string text, Answer answer)
    {
        string path = text.Trim().Replace('\\', '/');
        if (_projectRoot is not null && Path.IsPathRooted(path))
        {
            string relative = Path.GetRelativePath(_projectRoot, path).Replace('\\', '/');
            if (relative.StartsWith("../", StringComparison.Ordinal) || relative == ".." || Path.IsPathRooted(relative))
            {
                answer.Add("outside the project: " + text);
                return null;
            }

            path = relative;
        }

        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        if (path.StartsWith("../", StringComparison.Ordinal))
        {
            answer.Add("outside the project: " + text);
            return null;
        }

        return path;
    }

    // A `.cs` file in the code graph by path, or by file name when one file has it: also files with no .meta yet.
    private OutlineModel? CodeFile(string path)
    {
        if (!path.EndsWith(".cs", StringComparison.Ordinal))
        {
            return null;
        }

        if (path.Contains('/', StringComparison.Ordinal))
        {
            return CodeOutline(path);
        }

        IReadOnlyList<string> files = _store.FilesNamed(path);
        return files.Count == 1 ? CodeOutline(files[0]) : null;
    }

    // A path or name that is not a `.cs` file already in the graph: resolve it like `explain` does.
    private OutlineModel? OutlineOf(string path, string text, Answer answer)
    {
        if (!Resolved("outline", path, answer, out IReadOnlyList<Node> nodes))
        {
            return null;
        }

        Node node = nodes[0];
        if (node.Kind != NodeKind.Asset)
        {
            answer.Add("not a file: " + text);
            answer.Add("  more  ug explain " + Names.Quote(text) + " --members");
            return null;
        }

        string file = Names.AssetPath(node);
        if (IsScript(node))
        {
            return CodeOutline(file) ?? new OutlineModel(file, "script", "", [], "no types in " + file);
        }

        return ObjectOutline(node, file);
    }

    private OutlineModel? CodeOutline(string path)
    {
        List<Node> nodes = [.. _store.KeysInFile(path).Select(Node).OfType<Node>()];
        if (nodes.Count == 0)
        {
            return null;
        }

        var owned = nodes.Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
        var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (EdgeHit hit in FileEdges(path, EdgeKindSet.Of(EdgeKind.Contains)))
        {
            parentOf[hit.Edge.Dst] = hit.Edge.Src;
        }

        var children = new Dictionary<string, List<Node>>(StringComparer.Ordinal);
        var top = new List<(int Line, Node Node, string? Foreign)>();
        foreach (Node node in nodes)
        {
            if (parentOf.TryGetValue(node.Key, out string? parent))
            {
                if (!children.TryGetValue(parent, out List<Node>? list))
                {
                    children[parent] = list = [];
                }

                list.Add(node);
            }
            else
            {
                top.Add((node.LineStart, node, null));
            }
        }

        // Members of a partial type whose declaration is in another file: the type's part here.
        foreach ((string parent, List<Node> members) in children.Where(c => !owned.Contains(c.Key)))
        {
            if (Node(parent) is { } type)
            {
                top.Add((members.Min(m => m.LineStart), type, parent));
            }
        }

        var rows = new List<OutlineRow>();
        foreach ((int _, Node node, string? foreign) in top.OrderBy(t => t.Line).ThenBy(t => t.Node.Key, StringComparer.Ordinal))
        {
            Emit(node, 0, foreign is not null);
        }

        int types = rows.Count(r => r.Kind is not ("method" or "field" or "property" or "event"));
        string counts = Count(types, "type") + ", " + Count(rows.Count - types, "member");
        return new OutlineModel(path, "script", counts, rows);

        void Emit(Node node, int depth, bool part)
        {
            List<Node> inside = children.TryGetValue(node.Key, out List<Node>? list)
                ? [.. list.OrderBy(n => n.LineStart).ThenBy(n => n.Key, StringComparer.Ordinal)]
                : [];
            if (node.Kind == NodeKind.Type)
            {
                (int start, int end) = part ? (inside.Min(n => n.LineStart), inside.Max(n => Math.Max(n.LineStart, n.LineEnd))) : (node.LineStart, node.LineEnd);
                rows.Add(new OutlineRow(depth, Names.Kind(node), depth == 0 ? Names.Qualified(node.Key) : node.Name, start, end, TypeDetail(node, path, part)));
            }
            else
            {
                rows.Add(new OutlineRow(depth, Names.Kind(node), node.Name, node.LineStart, node.LineEnd, string.Join("  ", MemberDetail(node))));
            }

            foreach (Node child in inside)
            {
                Emit(child, depth + 1, false);
            }
        }
    }

    // `: Base, IFace  public abstract  MonoBehaviour  partial, also in X.cs`
    private string TypeDetail(Node type, string path, bool part)
    {
        if (part)
        {
            return "part of " + Names.Location(type);
        }

        List<Edge> outgoing = Out(type.Key);
        var parts = new List<string>();
        string bases = string.Join(", ", outgoing.Where(e => e.Kind is EdgeKind.Inherits or EdgeKind.Implements)
            .OrderBy(e => e.Kind).Select(e => Names.Qualified(e.Dst)));
        if (bases.Length > 0)
        {
            parts.Add(": " + bases);
        }

        var flags = new List<string>();
        if (Names.Prop(type, "access") is { } access && access is not ("internal" or "private"))
        {
            flags.Add(access);
        }

        foreach (string flag in new[] { "static", "abstract", "sealed" })
        {
            if (Names.Flag(type, flag))
            {
                flags.Add(flag);
            }
        }

        if (Names.Prop(type, "entry") is { } entry)
        {
            flags.Add($"[{entry}]");
        }

        if (Names.Prop(type, "unity") is { } unity)
        {
            flags.Add(UnityKind(unity));
        }

        if (flags.Count > 0)
        {
            parts.Add(string.Join(' ', flags));
        }

        string[] otherFiles = [.. outgoing.Where(e => e.Kind == EdgeKind.Contains).Select(e => e.File).Where(f => f != path).Distinct().Order(StringComparer.Ordinal)];
        if (otherFiles.Length > 0)
        {
            parts.Add("partial, also in " + Join(otherFiles, 3, "partial files past 3"));
        }

        return string.Join("  ", parts);
    }

    private static IEnumerable<string> MemberDetail(Node node) =>
        new[] { Names.Prop(node, "sig") ?? Names.Prop(node, "type") ?? "" }.Concat(Flags(node)).Where(s => s.Length > 0);

    private OutlineModel ObjectOutline(Node asset, string path)
    {
        string kind = Names.Kind(asset);
        List<Node> nodes = [.. _store.KeysInFile(path).Select(Node).OfType<Node>()];
        List<Node> objects = [.. nodes.Where(n => n.Kind is NodeKind.GameObject or NodeKind.PrefabInstance or NodeKind.SubAsset)];
        if (objects.Count == 0)
        {
            return new OutlineModel(path, kind, "", [], "no objects in " + path);
        }

        string counts = string.Join(", ", nodes.GroupBy(n => n.Kind).OrderBy(g => g.Key).Select(g => Count(g.Count(), KindNames.Of(g.Key).Replace('_', ' '))));
        var byKey = objects.ToDictionary(n => n.Key, StringComparer.Ordinal);
        var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (EdgeHit hit in FileEdges(path, EdgeKindSet.Of(EdgeKind.ChildOf)))
        {
            if (byKey.ContainsKey(hit.Edge.Dst))
            {
                parentOf[hit.Edge.Src] = hit.Edge.Dst;
            }
        }

        var components = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (EdgeHit hit in FileEdges(path, EdgeKindSet.Of(EdgeKind.Contains)))
        {
            if (byKey.TryGetValue(hit.Edge.Src, out Node? owner) && owner.Kind == NodeKind.GameObject && Node(hit.Edge.Dst) is { Kind: NodeKind.Component } component)
            {
                if (!components.TryGetValue(owner.Key, out List<string>? names))
                {
                    components[owner.Key] = names = [];
                }

                names.Add(component.Name);
            }
        }

        var children = objects.Where(o => parentOf.ContainsKey(o.Key)).GroupBy(o => parentOf[o.Key], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(o => o.LineStart).ThenBy(o => o.Key, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var rows = new List<OutlineRow>();
        foreach (Node root in objects.Where(o => !parentOf.ContainsKey(o.Key)).OrderBy(o => o.LineStart).ThenBy(o => o.Key, StringComparer.Ordinal))
        {
            Emit(root, 0, 0);
        }

        return new OutlineModel(path, kind, counts, rows);

        void Emit(Node node, int depth, int guard)
        {
            string detail = node.Kind switch
            {
                NodeKind.GameObject => components.TryGetValue(node.Key, out List<string>? names) ? Join(names, ComponentNames, "component lists past 6") : "(no components)",
                NodeKind.PrefabInstance => Names.Prop(node, "source") is { } source ? "of " + Label(NodeKeys.Asset(source)) : "",
                _ => Names.Prop(node, "class") ?? "",
            };
            if (Names.Prop(node, "active") == "false")
            {
                detail += "  inactive";
            }

            rows.Add(new OutlineRow(depth, KindNames.Of(node.Kind), node.Name, node.LineStart, node.LineEnd, detail.Trim()));
            if (guard < 100 && children.TryGetValue(node.Key, out List<Node>? below))
            {
                foreach (Node child in below)
                {
                    Emit(child, depth + 1, guard + 1);
                }
            }
        }
    }

    private static string Lines(int start, int end) =>
        end > start ? $"{start.ToString(CultureInfo.InvariantCulture)}-{end.ToString(CultureInfo.InvariantCulture)}" : start.ToString(CultureInfo.InvariantCulture);

    private string OutlineText(OutlineModel model, string command, int budget)
    {
        var answer = new Answer(budget, From);
        answer.Add($"{model.Path}  {model.Kind}{(model.Counts.Length > 0 ? "  " + model.Counts : "")}  [exact]");
        if (model.Note is not null)
        {
            answer.Add(model.Note);
            answer.Add("  more  ug explain " + Names.Quote(model.Path));
            return answer.Finish(command);
        }

        answer.AddRange(Answer.Table([.. model.Rows.Select(r => new[] { new string(' ', r.Depth * 2) + r.Name, r.Kind, Lines(r.Start, r.End), r.Detail })], "  "));
        AddCutNote(answer, command, "  ");
        return answer.Finish(command, countDropped: true);
    }

    private string OutlineJson(OutlineModel model, string command, int budget) =>
        JsonAnswer.Fit(model.Rows.Count, budget, (w, keep) =>
        {
            w.WriteStartObject();
            w.WriteString("file", model.Path);
            w.WriteString("kind", model.Kind);
            w.WriteString("counts", model.Counts);
            if (model.Note is not null)
            {
                w.WriteString("note", model.Note);
            }

            if (_cuts.Count > 0)
            {
                w.WriteString("capped", $"{string.Join(", ", _cuts)}: {command} --all");
            }

            w.WriteStartArray("rows");
            foreach (OutlineRow row in model.Rows.Take(keep))
            {
                w.WriteStartObject();
                w.WriteNumber("depth", row.Depth);
                w.WriteString("kind", row.Kind);
                w.WriteString("name", row.Name);
                w.WriteStartArray("lines");
                w.WriteNumberValue(row.Start);
                w.WriteNumberValue(Math.Max(row.Start, row.End));
                w.WriteEndArray();
                w.WriteString("detail", row.Detail);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteBoolean("cut", keep < model.Rows.Count);
            w.WriteEndObject();
        });
}
