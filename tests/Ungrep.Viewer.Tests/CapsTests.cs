using Microsoft.Data.Sqlite;
using Ungrep.Core.Graph;

namespace Ungrep.Viewer.Tests;

/// <summary>The first-open and baked cuts on made-up graphs (SPEC-viewer, Caps).</summary>
public sealed class CapsTests : IDisposable
{
    private static readonly DateTimeOffset _clock = new(2026, 10, 11, 14, 3, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-view-caps-" + Guid.NewGuid().ToString("N")[..8]);

    public CapsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Assets"));
        Directory.CreateDirectory(Path.Combine(_root, "ProjectSettings"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    // A method `Hub` and `callers` methods calling it; each of those with `fanIn` callers of its own.
    private GraphStore Calls(int callers, int fanIn = 0)
    {
        var nodes = new List<Node> { Method("Hub") };
        var edges = new List<Edge>();
        for (int i = 0; i < callers; i++)
        {
            string caller = "C" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
            nodes.Add(Method(caller));
            edges.Add(Call(caller, "Hub"));
            for (int j = 0; j < fanIn; j++)
            {
                string outer = caller + "_" + j.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
                nodes.Add(Method(outer));
                edges.Add(Call(outer, caller));
            }
        }

        GraphStore store = GraphStore.Open(_root, StoreMode.ReadWrite);
        store.ReplaceFile(new FileRecord("Assets/G.cs", 1, 1, new string('a', 64), Indexer.Code), nodes, edges);
        return store;
    }

    private static Node Method(string name) => new("cs:M:G." + name, NodeKind.Method, name, "Assets/G.cs", 1, 1);

    private static Edge Call(string from, string to) => new("cs:M:G." + from, EdgeKind.Calls, "cs:M:G." + to, Confidence.Exact, "Assets/G.cs", 1);

    private static ViewGraph Walk(GraphStore store, int depth, bool all = false) =>
        Neighbourhood.Walk(store, [store.GetNode("cs:M:G.Hub")!], "Hub", new ViewOptions(depth, null, all, _clock));

    [Fact]
    public void Twelve_per_group_then_more()
    {
        using GraphStore store = Calls(20);
        ViewGraph graph = Walk(store, 1);
        Assert.Equal(13, graph.FirstOpen());
        ViewMore more = Assert.Single(graph.More);
        Assert.Equal("+8 more callers", more.Label);
        Assert.Equal("8 method", more.Detail);
        Assert.Equal(("calls", "in", 0, 1), (more.Family, more.Direction, more.Of, more.Depth));
        Assert.Equal(8, more.Members.Count);
        Assert.All(more.Members, m => Assert.False(graph.Nodes[m].Drawn));

        // The page's data escapes `+`; JSON.parse gives it back.
        Assert.Contains("\"label\":\"\\u002B8 more callers\",\"detail\":\"8 method\"", ViewPage.Render(graph), StringComparison.Ordinal);
    }

    [Fact]
    public void At_most_150_drawn_on_first_open()
    {
        using GraphStore store = Calls(12, fanIn: 13);
        ViewGraph graph = Walk(store, 2);
        Assert.Equal(1 + 12 + (12 * 13), graph.Nodes.Count);
        Assert.Equal(Neighbourhood.MaxDrawn, graph.FirstOpen());

        // 12 groups of 13 callers: 12 drawn in each of the first 11, 5 in the last, then the 150 runs out.
        Assert.Equal(12, graph.More.Count);
        Assert.Equal("+1 more callers", graph.More[0].Label);
        Assert.Equal(8, graph.More[^1].Members.Count);
    }

    [Fact]
    public void Baked_cut_names_all_and_all_lifts_it()
    {
        using GraphStore store = Calls(Neighbourhood.MaxBaked + 20);
        ViewGraph graph = Walk(store, 1);
        Assert.Equal(Neighbourhood.MaxBaked, graph.Nodes.Count);
        Assert.True(graph.Cut);
        Assert.Equal("ug view Hub --depth 1 --all", graph.AllCommand);
        ViewMore more = Assert.Single(graph.More);
        Assert.Equal(21, more.Missing);
        Assert.Equal("+1508 more callers", more.Label);
        Assert.Equal("1508 method; 21 not in this page", more.Detail);
        Assert.Contains("\"cut\":\"ug view Hub --depth 1 --all\"", ViewPage.Render(graph), StringComparison.Ordinal);

        ViewGraph all = Walk(store, 1, all: true);
        Assert.False(all.Cut);
        Assert.Equal(Neighbourhood.MaxBaked + 21, all.Nodes.Count);
    }
}
