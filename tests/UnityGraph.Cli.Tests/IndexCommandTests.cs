using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace UnityGraph.Cli.Tests;

/// <summary>`ug index` end to end on a temp copy of `fixtures/CodeProject`.</summary>
public sealed partial class IndexCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-index-" + Guid.NewGuid().ToString("N")[..8]);

    public IndexCommandTests() => Copy(Path.Combine(RepoRoot(), "fixtures", "CodeProject"), _root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private (int ExitCode, string Output) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exitCode = CliApp.Run(["index", "--project", _root, .. args], output, error);
        Assert.Equal("", error.ToString());
        return (exitCode, Seconds().Replace(output.ToString().ReplaceLineEndings("\n"), "<t> s"));
    }

    [GeneratedRegex(@"\d+\.\d s")]
    private static partial Regex Seconds();

    [Fact]
    public void Builds_then_reports_up_to_date_then_reindexes_an_edit()
    {
        var (exitCode, first) = Run();
        Assert.Equal(0, exitCode);
        Assert.StartsWith("indexed 9 files (8 own, 1 vendored): ", first, StringComparison.Ordinal);
        Assert.Contains("  warning: missing reference Library/ScriptAssemblies/Missing.dll\n", first, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, "Library", "unitygraph", "graph.db")));

        Assert.StartsWith("up to date\n", Run().Output, StringComparison.Ordinal);

        string turntable = Path.Combine(_root, "Assets", "Scripts", "Lab", "Turntable.cs");
        File.AppendAllText(turntable, "\n// edited\n");
        File.SetLastWriteTimeUtc(turntable, File.GetLastWriteTimeUtc(turntable).AddSeconds(5));
        Assert.StartsWith("reindexed 1 files, removed 0 in <t> s\n", Run().Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Full_rebuilds_into_another_graph_folder()
    {
        string graph = Path.Combine(_root, "elsewhere");

        var (exitCode, output) = Run("--full", "--graph-dir", graph);

        Assert.Equal(0, exitCode);
        Assert.StartsWith("indexed 9 files", output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(graph, "graph.db")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Library", "unitygraph")));
    }

    [Fact]
    public void No_project_files_asks_for_them()
    {
        foreach (string csproj in Directory.EnumerateFiles(_root, "*.csproj"))
        {
            File.Delete(csproj);
        }

        Assert.Equal((0, "no .csproj files: in Unity, Preferences > External Tools > Regenerate project files\n"), Run());
    }

    [Fact]
    public void Module_off_does_nothing()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".claude"));
        File.WriteAllText(Path.Combine(_root, ".claude", "unitygraph.json"), """{ "modules": { "code-index": false } }""");

        Assert.Equal((0, "code-index is off in .claude/unitygraph.json\n"), Run());
    }

    [Fact]
    public void Not_a_unity_project()
    {
        string plain = Path.Combine(_root, "Assets");
        Directory.Delete(Path.Combine(_root, "ProjectSettings"), recursive: true);

        var output = new StringWriter();
        int exitCode = CliApp.Run(["index", "--project", plain], output, new StringWriter());

        Assert.Equal(1, exitCode);
        Assert.StartsWith("no Unity project at or above ", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Warnings_past_ten_are_counted()
    {
        var summary = new UnityGraph.Core.Graph.IndexSummary(1, 0, 2, 3, 0, TimeSpan.FromSeconds(1.25), [.. Enumerable.Range(1, 12).Select(i => "p" + i)]);

        string text = IndexCommand.Render(summary, rebuilt: true);

        Assert.StartsWith("indexed 1 files (1 own, 0 vendored): 2 nodes, 3 edges in 1.3 s\n  warning: p1\n", text, StringComparison.Ordinal);
        Assert.EndsWith("  warning: p10\n  ... and 2 more warnings\n", text, StringComparison.Ordinal);
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }

        foreach (string dir in Directory.EnumerateDirectories(from))
        {
            Copy(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "unitygraph.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("unitygraph.sln not found above " + AppContext.BaseDirectory);
    }
}
