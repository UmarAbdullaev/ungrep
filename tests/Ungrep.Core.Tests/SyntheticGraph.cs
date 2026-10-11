using Ungrep.Core.Graph;

namespace Ungrep.Core.Tests;

/// <summary>
/// A generated graph at purge-squad scale (SPEC-graph-core, Performance targets): ~3,000 files
/// on disk, ~40k nodes, ~200k edges. Deterministic (fixed seed). Edge targets are skewed so a
/// few types and methods are hubs, as in real code.
/// </summary>
internal sealed class SyntheticGraph
{
    public const int CodeFiles = 2600;
    public const int AssetFiles = 400;
    private const int MembersPerType = 12;
    private const int CallsPerType = 40;
    private const int UsesPerType = 10;
    private const int ObjectsPerAsset = 15;
    private const int RefsPerObject = 5;

    private readonly Random _random = new(20260929);
    private readonly List<string> _types = [];
    private readonly List<string> _methods = [];

    public List<(FileRecord File, List<Node> Nodes, List<Edge> Edges)> Files { get; } = [];

    public int NodeCount => Files.Sum(f => f.Nodes.Count);

    public int EdgeCount => Files.Sum(f => f.Edges.Count);

    /// <summary>The most referenced type: its incoming edges are well over 100.</summary>
    public string HubType => _types[0];

    /// <summary>Writes every file under <paramref name="root"/> and builds its rows with real stats and hashes.</summary>
    public static SyntheticGraph Create(string root)
    {
        var graph = new SyntheticGraph();
        for (int i = 0; i < CodeFiles; i++)
        {
            graph._types.Add(NodeKeys.Symbol($"T:Game.Module{i % 40}.Type{i}"));
            for (int m = 0; m < MembersPerType / 2; m++)
            {
                graph._methods.Add(NodeKeys.Symbol($"M:Game.Module{i % 40}.Type{i}.Method{m}"));
            }
        }

        for (int i = 0; i < CodeFiles; i++)
        {
            graph.AddCodeFile(root, i);
        }

        for (int i = 0; i < AssetFiles; i++)
        {
            graph.AddAssetFile(root, i);
        }

        return graph;
    }

    /// <summary>One large C# file for ReplaceFile: ~50 nodes and ~250 edges.</summary>
    public (FileRecord File, List<Node> Nodes, List<Edge> Edges) LargeFile(string path, int version)
    {
        string type = NodeKeys.Symbol("T:Game.Big.BigType");
        var nodes = new List<Node> { new(type, NodeKind.Type, "BigType", path, 1, 900) };
        var edges = new List<Edge>();
        for (int m = 0; m < 49; m++)
        {
            string method = NodeKeys.Symbol($"M:Game.Big.BigType.Step{m}_{version}");
            nodes.Add(new Node(method, NodeKind.Method, $"Step{m}", path, 10 + m * 18, 26 + m * 18));
            edges.Add(new Edge(type, EdgeKind.Contains, method, Confidence.Exact, path, 10 + m * 18));
            for (int c = 0; c < 4; c++)
            {
                edges.Add(new Edge(method, EdgeKind.Calls, Skewed(_methods), Confidence.Exact, path, 12 + m * 18 + c));
            }
        }

        return (new FileRecord(path, 40_000, version, new string('b', 64), Indexer.Code), nodes, edges);
    }

    private void AddCodeFile(string root, int i)
    {
        int module = i % 40;
        string path = $"Assets/Scripts/Module{module}/Type{i}.cs";
        string type = _types[i];
        var nodes = new List<Node> { new(type, NodeKind.Type, $"Type{i}", path, 3, 200, """{"subkind":"class"}""") };
        var edges = new List<Edge>();
        for (int m = 0; m < MembersPerType; m++)
        {
            bool method = m < MembersPerType / 2;
            string member = method
                ? _methods[i * (MembersPerType / 2) + m]
                : NodeKeys.Symbol($"F:Game.Module{module}.Type{i}.field{m}");
            nodes.Add(new Node(member, method ? NodeKind.Method : NodeKind.Field, method ? $"Method{m}" : $"field{m}", path, 10 + m * 15, 20 + m * 15));
            edges.Add(new Edge(type, EdgeKind.Contains, member, Confidence.Exact, path, 10 + m * 15));
        }

        for (int c = 0; c < CallsPerType; c++)
        {
            string caller = _methods[i * (MembersPerType / 2) + c % (MembersPerType / 2)];
            edges.Add(new Edge(caller, EdgeKind.Calls, Skewed(_methods), c % 7 == 0 ? Confidence.Heuristic : Confidence.Exact, path, 11 + c));
        }

        for (int u = 0; u < UsesPerType; u++)
        {
            edges.Add(new Edge(type, EdgeKind.UsesType, Skewed(_types), Confidence.Exact, path, 5 + u));
        }

        // Some calls go to engine methods that are never indexed: unresolved edges.
        edges.Add(new Edge(_methods[i * (MembersPerType / 2)], EdgeKind.Calls, NodeKeys.Symbol("M:UnityEngine.Debug.Log(System.Object)"), Confidence.Exact, path, 30));
        Files.Add((Write(root, path, $"namespace Game.Module{module} {{ class Type{i} {{ }} }}\n" + new string('/', 2000 + i % 500), Indexer.Code), nodes, edges));
    }

    private void AddAssetFile(string root, int i)
    {
        string path = i % 4 == 0 ? $"Assets/Scenes/Level{i}.unity" : $"Assets/Prefabs/Prefab{i}.prefab";
        string guid = i.ToString("x32");
        var nodes = new List<Node>();
        var edges = new List<Edge>();
        for (int o = 0; o < ObjectsPerAsset; o++)
        {
            string key = NodeKeys.Object(guid, 1000 + o);
            bool component = o % 3 != 0;
            nodes.Add(new Node(key, component ? NodeKind.Component : NodeKind.GameObject, component ? $"Type{o * 7 % CodeFiles}" : $"Object{o}", path, 1 + o * 20, 19 + o * 20));
            if (o > 0)
            {
                edges.Add(new Edge(key, EdgeKind.ChildOf, NodeKeys.Object(guid, 1000 + (o - 1) / 3 * 3), Confidence.Exact, path, 1 + o * 20));
            }

            if (component)
            {
                edges.Add(new Edge(key, EdgeKind.InstanceOf, Skewed(_types), Confidence.Exact, path, 2 + o * 20));
            }

            for (int r = 0; r < RefsPerObject; r++)
            {
                edges.Add(new Edge(key, EdgeKind.References, NodeKeys.Object((i + r + 1) % AssetFiles == i ? guid : ((i + r + 1) % AssetFiles).ToString("x32"), 1000 + r), Confidence.Exact, path, 3 + o * 20 + r));
            }
        }

        Files.Add((Write(root, path, "%YAML 1.1\n" + new string('-', 20_000 + i * 10), Indexer.Asset), nodes, edges));
    }

    // Cubing a uniform number puts ~20% of picks in the first 1% of the list: a few hubs.
    private string Skewed(List<string> keys) => keys[(int)(keys.Count * Math.Pow(_random.NextDouble(), 3))];

    private static FileRecord Write(string root, string path, string content, Indexer indexer)
    {
        string full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        FileStat stat = FileStat.Of(root, path)!;
        return new FileRecord(path, stat.Size, stat.MtimeNs, FileHash.Of(full), indexer);
    }
}
