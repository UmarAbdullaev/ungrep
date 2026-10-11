using System.Globalization;
using System.Text;
using Ungrep.Core.Graph;
using Ungrep.Query;
using Ungrep.Query.Tests;

namespace Ungrep.Viewer.Tests;

/// <summary>The walk `ug view` bakes into the page, on the fixture graph (SPEC-viewer, Command and Caps).</summary>
public sealed class NeighbourhoodTests(QueryFixture fixture) : IClassFixture<QueryFixture>
{
    private static readonly DateTimeOffset _clock = new(2026, 10, 11, 14, 3, 0, TimeSpan.Zero);

    internal static ViewGraph Walk(GraphStore store, string query, int depth = 2, string? kinds = null, bool all = false)
    {
        IReadOnlyList<Node>? roots = new QueryEngine(store).Resolve("view", query, 1200, out string answer);
        Assert.True(roots is not null, answer);
        HashSet<string>? set = kinds?.Split(',').ToHashSet(StringComparer.Ordinal);
        return Neighbourhood.Walk(store, roots, query, new ViewOptions(depth, set, all, _clock));
    }

    // The graph as lines: nodes with depth, box and first-open state, edges, `+N more` groups.
    internal static string Dump(ViewGraph graph)
    {
        var text = new StringBuilder();
        string I(int n) => n.ToString(CultureInfo.InvariantCulture);
        text.Append($"  {I(graph.Nodes.Count)} nodes ({I(graph.FirstOpen())} first open), {I(graph.Edges.Count)} edges, {I(graph.Unresolved)} unresolved, layout {graph.Layout}")
            .Append(graph.Cut ? ", cut: " + graph.AllCommand : "").Append('\n');
        foreach (ViewNode n in graph.Nodes)
        {
            var flags = new List<string> { "d" + I(n.Depth) };
            if (n.Root)
            {
                flags.Add("root");
            }

            if (n.Container)
            {
                flags.Add("container");
            }

            if (n.Drawn)
            {
                flags.Add("drawn");
            }

            if (n.Parent is int p)
            {
                flags.Add("in n" + I(p));
            }

            if (n.Refs > 0)
            {
                flags.Add("refs " + I(n.Refs));
            }

            text.Append($"  n{I(n.Id)}  {n.Group}/{n.What}  {n.Label}  [{string.Join(' ', flags)}]  {n.Title}  {n.At}  {n.Command}\n");
        }

        foreach (ViewEdge e in graph.Edges)
        {
            text.Append($"  n{I(e.Src)} -{KindNames.Of(e.Kind)}-> n{I(e.Dst)}  {e.Family}{(e.Heuristic ? " heuristic" : "")}{(e.Note is null ? "" : " " + e.Note)}  {e.At}\n");
        }

        foreach (ViewMore m in graph.More)
        {
            text.Append($"  {m.Id} on n{I(m.Of)}  {m.Label}: {m.Detail}  [{m.Family} {m.Direction} d{I(m.Depth)}] members {string.Join(',', m.Members.Select(x => "n" + I(x)))}\n");
        }

        return text.ToString();
    }

    [Fact]
    public void Walks_on_the_fixture()
    {
        (string Query, int Depth, string? Kinds)[] cases =
        [
            ("LabStage", 1, null),
            ("Turntable.Rotate(float)", 2, null),
            ("Turntable.prefab", 1, null),
            ("o:c3000000000000000000000000000003:512", 1, null),
            ("Lab.unity", 1, null),
            ("LabStage", 2, "type,method"),
            ("Turntable.Stop", 1, "method,asset"),
        ];

        using GraphStore store = fixture.Open();
        var transcript = new StringBuilder();
        foreach ((string query, int depth, string? kinds) in cases)
        {
            transcript.Append($"$ ug view {(query.Contains('(', StringComparison.Ordinal) ? "'" + query + "'" : query)} --depth {depth.ToString(CultureInfo.InvariantCulture)}{(kinds is null ? "" : " --kind " + kinds)}\n")
                .Append(Dump(Walk(store, query, depth, kinds))).Append('\n');
        }

        Golden.Match(transcript.ToString());
    }

    [Fact]
    public void Page_data_is_json_safe_for_a_script_tag()
    {
        using GraphStore store = fixture.Open();
        ViewGraph graph = Walk(store, "Turntable.Rotate(float)", 1);
        string json = graph.ToJson(indented: true).Replace(graph.Revision, "<revision>", StringComparison.Ordinal);
        Golden.Match(json, "json");
        Assert.DoesNotContain("<", graph.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Going_up_to_a_container_is_free_and_does_not_spread()
    {
        using GraphStore store = fixture.Open();
        ViewGraph graph = Walk(store, "Game.Core.Stage.Open", 1);
        ViewNode root = graph.Nodes.Single(n => n.Root);
        ViewNode type = graph.Nodes[root.Parent!.Value];
        Assert.Equal("Stage", type.Label);
        Assert.True(type.Container);
        Assert.Equal(0, type.Depth);

        // OnClosed is Open's sibling: one hop down from the type, which was never walked.
        Assert.DoesNotContain(graph.Nodes, n => n.Title == "Game.Core.Stage.OnClosed()");
    }

    [Fact]
    public void Going_down_is_a_hop()
    {
        using GraphStore store = fixture.Open();
        ViewGraph graph = Walk(store, "Game.Lab.Turntable", 1);
        ViewNode rotate = graph.Nodes.Single(n => n.Label == "Rotate(float)");
        Assert.Equal(1, rotate.Depth);
        Assert.Equal(0, rotate.Parent);
    }

    [Fact]
    public void Kinds_limit_what_is_walked_and_the_symbol_stays()
    {
        using GraphStore store = fixture.Open();
        ViewGraph graph = Walk(store, "Turntable.Stop", 2, kinds: "type");
        Assert.Contains(graph.Nodes, n => n.Root && n.Kind == NodeKind.Method);
        Assert.All(graph.Nodes.Where(n => !n.Root), n => Assert.Equal(NodeKind.Type, n.Kind));
    }

    [Fact]
    public void Asset_types_are_kinds_too()
    {
        using GraphStore store = fixture.Open();
        ViewGraph graph = Walk(store, "Game.Lab.Turntable", 2, kinds: "script,prefab");
        Assert.All(graph.Nodes.Where(n => !n.Root), n => Assert.Contains(n.What, (string[])["script", "prefab"]));
        Assert.Contains(graph.Nodes, n => n.What == "script");
    }

    [Fact]
    public void Depth_bakes_one_hop_more()
    {
        using GraphStore store = fixture.Open();
        ViewGraph one = Walk(store, "Turntable.Rotate(float)", 1);
        Assert.Equal(2, one.BakedDepth);
        Assert.Contains(one.Nodes, n => n.Depth == 2);
        Assert.DoesNotContain(one.Nodes, n => n.Depth > 2);
        Assert.Equal("dagre", one.Layout);
        Assert.Equal("fcose", Walk(store, "Turntable.Rotate(float)", 4).Layout);
    }

    [Fact]
    public void Edges_to_nodes_outside_the_graph_are_counted_not_drawn()
    {
        using GraphStore store = fixture.Open();
        ViewGraph graph = Walk(store, "Game.Lab.LabStage", 1);
        Assert.True(graph.Unresolved > 0);
        Assert.All(graph.Edges, e => Assert.InRange(e.Dst, 0, graph.Nodes.Count - 1));
    }

    [Fact]
    public void Revision_changes_with_a_file_and_only_then()
    {
        string root = Path.Combine(Path.GetTempPath(), "ug-view-rev-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        try
        {
            using GraphStore store = GraphStore.Open(root, StoreMode.ReadWrite);
            string empty = store.Revision();
            Assert.Matches("^[0-9a-f]{12}$", empty);
            var file = new FileRecord("Assets/A.cs", 1, 1, new string('a', 64), Indexer.Code);
            store.ReplaceFile(file, [], []);
            string one = store.Revision();
            Assert.NotEqual(empty, one);
            store.ReplaceFile(file with { MtimeNs = 2 }, [], []);
            Assert.Equal(one, store.Revision());
            store.ReplaceFile(file with { Hash = new string('b', 64) }, [], []);
            Assert.NotEqual(one, store.Revision());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
