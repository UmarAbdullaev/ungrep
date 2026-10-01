using System.Diagnostics;
using Microsoft.Data.Sqlite;
using UnityGraph.Assets;
using UnityGraph.Code;
using UnityGraph.Core;
using UnityGraph.Core.Graph;

namespace UnityGraph.Query.Tests;

/// <summary>
/// SPEC-query `outline` and `impact` timings. Runs when `UG_PURGE_SQUAD` names the project; reads it
/// only, keeps the graph in a temp folder and prints the numbers for the spec's Measured section.
/// </summary>
public sealed class PurgeSquadQueryTests : IDisposable
{
    private readonly string _graph = Path.Combine(Path.GetTempPath(), "ug-ps-query-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_graph))
        {
            Directory.Delete(_graph, recursive: true);
        }
    }

    [Fact]
    public void Outline_and_impact_answer_on_purge_squad_within_the_targets()
    {
        string? root = Environment.GetEnvironmentVariable("UG_PURGE_SQUAD");
        if (root is null)
        {
            Assert.Skip("UG_PURGE_SQUAD not set");
        }

        VendoredFolders vendored = UnityGraphConfig.Load(root).Vendored;
        using GraphStore store = GraphStore.Open(root, StoreMode.ReadWrite, graphDirectory: _graph);
        CodeIndex.Update(CodeWorkspace.Load(root, vendored), store);
        AssetIndex.Update(AssetWorkspace.Scan(root, vendored), store, []);
        var engine = new QueryEngine(store, root);
        var output = TestContext.Current.TestOutputHelper!;

        string bigScript = Largest(root, "*.cs", "Assets/Scripts");
        string bigScene = Largest(root, "*.unity", "Assets");
        string bigPrefab = Largest(root, "*.prefab", "Assets");
        foreach (string file in new[] { bigScript, bigScene, bigPrefab })
        {
            TimeSpan time = Time(() => engine.Outline(file, json: false, 1200), out string answer);
            output.WriteLine($"outline {file} ({new FileInfo(Path.Combine(root, file)).Length / 1024} KiB): {time.TotalMilliseconds:0} ms, {answer.Split('\n').Length - 1} lines");
            Assert.True(time < TimeSpan.FromSeconds(1), $"outline {file} took {time}");
        }

        foreach (string type in store.KeysInFile(bigScript).Select(store.GetNode).OfType<Node>().Where(n => n.Kind == NodeKind.Type).Select(n => n.Key).Take(3))
        {
            TimeSpan time = Time(() => engine.Impact(type, json: false, 1200), out string answer);
            output.WriteLine($"impact {type}: {time.TotalMilliseconds:0} ms, {answer.Split('\n').Length - 1} lines");
            Assert.True(time < TimeSpan.FromSeconds(1), $"impact {type} took {time}");
        }
    }

    private static TimeSpan Time(Func<string> run, out string answer)
    {
        _ = run();
        var clock = Stopwatch.StartNew();
        answer = run();
        return clock.Elapsed;
    }

    private static string Largest(string root, string pattern, string under) =>
        Directory.EnumerateFiles(Path.Combine(root, under), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains("3rd Party", StringComparison.OrdinalIgnoreCase) && !f.Contains("Plugins", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => new FileInfo(f).Length)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .First();
}
