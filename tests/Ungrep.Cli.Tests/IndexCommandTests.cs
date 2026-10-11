using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Ungrep.Cli.Tests;

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
        Assert.StartsWith("indexed 28 files (26 own, 2 vendored): ", first, StringComparison.Ordinal);
        Assert.Contains("  warning: missing reference Library/ScriptAssemblies/Missing.dll\n", first, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, "Library", "ungrep", "graph.db")));

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
        Assert.StartsWith("indexed 28 files", output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(graph, "graph.db")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Library", "ungrep")));
    }

    [Fact]
    public void No_project_files_asks_for_them()
    {
        foreach (string csproj in Directory.EnumerateFiles(_root, "*.csproj"))
        {
            File.Delete(csproj);
        }

        (int exitCode, string output) = Run();
        Assert.Equal(0, exitCode);
        Assert.StartsWith("indexed 19 files (18 own, 1 vendored): ", output, StringComparison.Ordinal);
        Assert.Contains("  warning: no .csproj files: in Unity, Preferences > External Tools > Regenerate project files\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Modules_off_do_nothing()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".claude"));
        File.WriteAllText(Path.Combine(_root, ".claude", "ungrep.json"), """{ "modules": { "code-index": false, "asset-index": false } }""");

        Assert.Equal((0, "code-index and asset-index are off in .claude/ungrep.json\n"), Run());
    }

    [Fact]
    public void Code_index_off_still_indexes_assets()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".claude"));
        File.WriteAllText(Path.Combine(_root, ".claude", "ungrep.json"), """{ "modules": { "code-index": false } }""");

        (int exitCode, string output) = Run();
        Assert.Equal(0, exitCode);
        Assert.StartsWith("indexed 19 files (18 own, 1 vendored): ", output, StringComparison.Ordinal);
        Assert.DoesNotContain(".csproj", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Asset_edits_are_reindexed_with_their_meta()
    {
        Assert.Equal(0, Run().ExitCode);
        string meta = Path.Combine(_root, "Assets", "Materials", "Lab.mat.meta");
        File.WriteAllText(meta, File.ReadAllText(meta).Replace("d4000000000000000000000000000004", "d4000000000000000000000000000044", StringComparison.Ordinal));

        Assert.StartsWith("reindexed 2 files, removed 0 in <t> s\n", Run().Output, StringComparison.Ordinal);
        File.Delete(Path.Combine(_root, "Assets", "Scenes", "New.unity"));
        Assert.StartsWith("reindexed 0 files, removed 1 in <t> s\n", Run().Output, StringComparison.Ordinal);
        Assert.StartsWith("up to date\n", Run().Output, StringComparison.Ordinal);
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
        var summary = new Ungrep.Core.Graph.IndexSummary(1, 0, 2, 3, 0, TimeSpan.FromSeconds(1.25), [.. Enumerable.Range(1, 12).Select(i => "p" + i)]);

        string text = IndexCommand.Render(summary, rebuilt: true);

        Assert.StartsWith("indexed 1 files (1 own, 0 vendored): 2 nodes, 3 edges in 1.3 s\n  warning: p1\n", text, StringComparison.Ordinal);
        Assert.EndsWith("  warning: p10\n  ... and 2 more warnings: Library/ungrep/index-warnings.txt\n", text, StringComparison.Ordinal);
        Assert.EndsWith("  ... and 2 more warnings\n", IndexCommand.Render(summary, rebuilt: true, warnings: null), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_index_rewrites_the_warnings_file_with_all_its_warnings()
    {
        string file = Path.Combine(_root, "Library", "ungrep", "index-warnings.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "stale\n");

        var output = new StringWriter();
        Assert.Equal(0, CliApp.Run(["index", "--project", _root, "--full"], output, new StringWriter()));

        string[] shown = [.. output.ToString().ReplaceLineEndings("\n").Split('\n').Where(l => l.StartsWith("  warning: ", StringComparison.Ordinal)).Select(l => l["  warning: ".Length..])];
        Assert.Equal(shown, File.ReadAllText(file).Split('\n', StringSplitOptions.RemoveEmptyEntries));
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
            if (File.Exists(Path.Combine(dir.FullName, "ungrep.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("ungrep.sln not found above " + AppContext.BaseDirectory);
    }
}
