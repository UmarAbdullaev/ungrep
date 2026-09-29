namespace UnityGraph.Core.Graph;

/// <summary>What one file adds to the graph.</summary>
public sealed record FileContribution(FileRecord File, IReadOnlyList<Node> Nodes, IReadOnlyList<Edge> Edges);

/// <summary>What an index run did. <see cref="Files"/> counts the files written, vendored ones included.</summary>
public sealed record IndexSummary(int Files, int Vendored, int Nodes, int Edges, int Removed, TimeSpan Elapsed, IReadOnlyList<string> Problems)
{
    public static IndexSummary Empty { get; } = new(0, 0, 0, 0, 0, TimeSpan.Zero, []);

    /// <summary>Both runs as one: counts added, problems in order.</summary>
    public IndexSummary Plus(IndexSummary other) => new(
        Files + other.Files,
        Vendored + other.Vendored,
        Nodes + other.Nodes,
        Edges + other.Edges,
        Removed + other.Removed,
        Elapsed + other.Elapsed,
        [.. Problems, .. other.Problems]);
}
