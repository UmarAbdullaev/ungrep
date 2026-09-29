using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using UnityGraph.Core.Graph;

namespace UnityGraph.Cli.Tests;

/// <summary>`ug find/refs/explain` end to end: freshness, the busy graph, config and arguments.</summary>
public sealed partial class QueryCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-query-cli-" + Guid.NewGuid().ToString("N")[..8]);

    public QueryCommandTests() => Copy(Path.Combine(RepoRoot(), "fixtures", "CodeProject"), _root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private (int ExitCode, string Output) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exitCode = CliApp.Run([.. args, "--project", _root], output, error);
        Assert.Equal("", error.ToString());
        return (exitCode, Seconds().Replace(output.ToString().ReplaceLineEndings("\n"), "<t> s"));
    }

    [GeneratedRegex(@"\d+\.\d s")]
    private static partial Regex Seconds();

    [Fact]
    public void The_first_query_builds_the_graph_and_later_ones_see_edits()
    {
        var (exitCode, first) = Run("refs", "Turntable.Stop");

        Assert.Equal(0, exitCode);
        Assert.StartsWith("indexed 28 files in <t> s\nGame.Lab.Turntable.Stop()  method", first, StringComparison.Ordinal);
        Assert.EndsWith("3 references\n", first, StringComparison.Ordinal);

        string broken = Path.Combine(_root, "Assets", "Scripts", "Broken.cs");
        File.WriteAllText(broken, File.ReadAllText(broken).Replace("table.Stop();", "", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(broken, File.GetLastWriteTimeUtc(broken).AddSeconds(5));

        var (_, second) = Run("refs", "Turntable.Stop");

        Assert.Equal("""
            Game.Lab.Turntable.Stop()  method  Assets/Scripts/Lab/Turntable.cs:23  [exact]
              called from  Game.Lab.LabStage.OnClosed  Assets/Scripts/Lab/LabStage.cs:51
              unity event  Extra: m_OnDone             Assets/Data/LabSettings.asset:20
            2 references

            """.ReplaceLineEndings("\n"), second);
    }

    [Fact]
    public void A_busy_graph_still_answers_with_a_note()
    {
        Run("find", "Turntable");
        using (GraphStore.Open(_root, StoreMode.ReadWrite))
        {
            var (exitCode, output) = Run("explain", "Turntable._speed");

            Assert.Equal(0, exitCode);
            Assert.StartsWith("Game.Lab.Turntable._speed  field", output, StringComparison.Ordinal);
            Assert.EndsWith("note: graph busy, edits from the last seconds may be missing\n", output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_busy_graph_that_was_never_built_asks_to_retry()
    {
        // A full index in progress holds the lock and has not published graph.db yet.
        using (GraphStore.OpenStaging(_root))
        {
            Assert.Equal((1, "the graph is being built by another ug; try again in a few seconds\n"), Run("find", "Turntable"));
        }
    }

    [Fact]
    public void Budget_comes_from_the_option_then_the_config()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".claude"));
        File.WriteAllText(Path.Combine(_root, ".claude", "unitygraph.json"), """{ "answerBudget": 200 }""");

        Assert.EndsWith("  more  ug explain LabStage --members --budget 400\n", Run("explain", "LabStage", "--members").Output, StringComparison.Ordinal);
        Assert.EndsWith("  more  ug refs LabStage\n", Run("explain", "LabStage", "--members", "--budget", "2000").Output, StringComparison.Ordinal);
        Assert.Equal((1, "--budget must be 200 to 8000\n"), Run("find", "x", "--budget", "100"));
    }

    [Fact]
    public void Query_off_says_so()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".claude"));
        File.WriteAllText(Path.Combine(_root, ".claude", "unitygraph.json"), """{ "modules": { "query": false } }""");

        Assert.Equal((0, "code-index or query is off in .claude/unitygraph.json; nothing to query\n"), Run("find", "x"));
    }

    [Fact]
    public void Bad_config_and_no_project_fail()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".claude"));
        File.WriteAllText(Path.Combine(_root, ".claude", "unitygraph.json"), "{");

        Assert.Equal(1, Run("find", "x").ExitCode);

        var output = new StringWriter();
        Assert.Equal(1, CliApp.Run(["find", "x", "--project", Path.GetTempPath()], output, new StringWriter()));
        Assert.StartsWith("no Unity project at or above ", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void No_project_files_asks_for_them()
    {
        foreach (string csproj in Directory.EnumerateFiles(_root, "*.csproj"))
        {
            File.Delete(csproj);
        }

        Assert.Equal((0, "no .csproj files: in Unity, Preferences > External Tools > Regenerate project files\n"), Run("refs", "LabStage"));
    }

    [Fact]
    public void Kind_accepts_only_symbol_kinds()
    {
        var error = new StringWriter();

        int exitCode = CliApp.Run(["find", "x", "--kind", "component", "--project", _root], new StringWriter(), error);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("component", error.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("Game.Lab.LabStage._turntable  field", Run("find", "turn", "--kind", "field").Output.Split('\n')[1], StringComparison.Ordinal);
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
