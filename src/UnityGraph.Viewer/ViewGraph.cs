using System.Text;
using System.Text.Json;
using UnityGraph.Core.Graph;

namespace UnityGraph.Viewer;

/// <summary>
/// A node baked into the page. <see cref="Container"/> nodes were not walked to: they hold walked
/// nodes (a member's type, a component's game object) and show while one of those shows.
/// </summary>
public sealed class ViewNode
{
    public required int Id { get; init; }
    public required string Key { get; init; }
    public required NodeKind Kind { get; init; }

    /// <summary>The legend group: type, method, member, script, scene, prefab, so, asset, object, component.</summary>
    public required string Group { get; init; }

    public required string Label { get; init; }
    public required string Title { get; set; }

    /// <summary>What `ug explain` calls it: a type's subkind, an asset's type, else the node kind.</summary>
    public required string What { get; init; }

    public required string At { get; init; }
    public required string Command { get; init; }
    public required int Depth { get; init; }
    public bool Root { get; init; }
    public bool Container { get; init; }
    public int? Parent { get; set; }

    /// <summary>Drawn on first open (within the 150 and per-group cuts) once its depth is on the slider.</summary>
    public bool Drawn { get; set; }

    /// <summary>Edges into it inside the page, containment left out: its size.</summary>
    public int Refs { get; set; }
}

/// <summary>An edge between two baked nodes; containment is nesting, never an edge.</summary>
public sealed record ViewEdge(int Src, int Dst, EdgeKind Kind, string Family, bool Heuristic, string? Note, string At);

/// <summary>
/// A `+N more` node: the walked neighbours of <see cref="Of"/> in one relation family and direction
/// that were not drawn (<see cref="Members"/>, in the page) or not baked (<see cref="Missing"/>).
/// </summary>
public sealed record ViewMore(string Id, int Of, string Family, string Direction, int Depth, IReadOnlyList<int> Members, int Missing, string Label, string Detail);

/// <summary>What `ug view` bakes into the page (SPEC-viewer).</summary>
public sealed class ViewGraph
{
    public required string Query { get; init; }
    public required int Depth { get; init; }
    public int BakedDepth => Depth + 1;

    /// <summary>dagre up to depth 3, fCoSE above (SPEC-viewer, Page).</summary>
    public string Layout => Depth <= 3 ? "dagre" : "fcose";

    public required string Generated { get; init; }
    public required string Revision { get; init; }
    public required IReadOnlyList<string> Kinds { get; init; }
    public required IReadOnlyList<ViewNode> Nodes { get; init; }
    public required IReadOnlyList<ViewEdge> Edges { get; init; }
    public required IReadOnlyList<ViewMore> More { get; init; }

    /// <summary>Edges left out because their far end has no node.</summary>
    public required int Unresolved { get; init; }

    /// <summary>The baked cap left nodes out; `--all` bakes them.</summary>
    public required bool Cut { get; init; }

    /// <summary>The command that bakes everything, named when <see cref="Cut"/>.</summary>
    public required string AllCommand { get; init; }

    /// <summary>
    /// The nodes shown on first open: drawn walked nodes within <see cref="Depth"/>, and every
    /// container holding one (SPEC-viewer, Caps).
    /// </summary>
    public int FirstOpen()
    {
        var shown = new HashSet<int>();
        foreach (ViewNode node in Nodes.Where(n => !n.Container && n.Drawn && n.Depth <= Depth))
        {
            int? id = node.Id;
            while (id is int i && shown.Add(i))
            {
                id = Nodes[i].Parent;
            }
        }

        return shown.Count;
    }

    /// <summary>The page's data. Indented for golden files; `&lt;`, `&gt;` and `&amp;` are always escaped, so it can sit in a script tag.</summary>
    public string ToJson(bool indented = false)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented }))
        {
            json.WriteStartObject();
            json.WriteString("query", Query);
            json.WriteNumber("depth", Depth);
            json.WriteNumber("baked", BakedDepth);
            json.WriteString("layout", Layout);
            json.WriteString("generated", Generated);
            json.WriteString("revision", Revision);
            json.WriteStartArray("kinds");
            foreach (string kind in Kinds)
            {
                json.WriteStringValue(kind);
            }

            json.WriteEndArray();
            json.WriteNumber("unresolved", Unresolved);
            if (Cut)
            {
                json.WriteString("cut", AllCommand);
            }

            json.WriteStartArray("nodes");
            foreach (ViewNode node in Nodes)
            {
                WriteNode(json, node);
            }

            json.WriteEndArray();
            json.WriteStartArray("edges");
            foreach (ViewEdge edge in Edges)
            {
                json.WriteStartObject();
                json.WriteNumber("s", edge.Src);
                json.WriteNumber("t", edge.Dst);
                json.WriteString("kind", KindNames.Of(edge.Kind));
                json.WriteString("family", edge.Family);
                if (edge.Heuristic)
                {
                    json.WriteBoolean("heuristic", true);
                }

                if (edge.Note is not null)
                {
                    json.WriteString("note", edge.Note);
                }

                json.WriteString("at", edge.At);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteStartArray("more");
            foreach (ViewMore more in More)
            {
                json.WriteStartObject();
                json.WriteString("id", more.Id);
                json.WriteNumber("of", more.Of);
                json.WriteString("family", more.Family);
                json.WriteString("dir", more.Direction);
                json.WriteNumber("depth", more.Depth);
                json.WriteString("label", more.Label);
                json.WriteString("detail", more.Detail);
                json.WriteStartArray("members");
                foreach (int member in more.Members)
                {
                    json.WriteNumberValue(member);
                }

                json.WriteEndArray();
                if (more.Missing > 0)
                {
                    json.WriteNumber("missing", more.Missing);
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteNode(Utf8JsonWriter json, ViewNode node)
    {
        json.WriteStartObject();
        json.WriteNumber("id", node.Id);
        json.WriteString("key", node.Key);
        json.WriteString("kind", KindNames.Of(node.Kind));
        json.WriteString("group", node.Group);
        json.WriteString("label", node.Label);
        json.WriteString("title", node.Title);
        json.WriteString("what", node.What);
        json.WriteString("at", node.At);
        json.WriteString("cmd", node.Command);
        json.WriteNumber("depth", node.Depth);
        if (node.Parent is int parent)
        {
            json.WriteNumber("parent", parent);
        }

        if (node.Root)
        {
            json.WriteBoolean("root", true);
        }

        if (node.Container)
        {
            json.WriteBoolean("container", true);
        }

        if (node.Drawn)
        {
            json.WriteBoolean("drawn", true);
        }

        if (node.Refs > 0)
        {
            json.WriteNumber("refs", node.Refs);
        }

        json.WriteEndObject();
    }
}
