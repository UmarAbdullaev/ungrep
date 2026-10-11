using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Ungrep.Cli.Tests;

/// <summary>`ug view` end to end on the fixture project (SPEC-viewer, Testing: CLI).</summary>
public sealed partial class ViewCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-view-cli-" + Guid.NewGuid().ToString("N")[..8]);

    public ViewCommandTests() => Copy(Path.Combine(RepoRoot(), "fixtures", "CodeProject"), _root);

    private string Page => Path.Combine(_root, "Library", "ungrep", "view.html");

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
        return (exitCode, Stamp().Replace(Seconds().Replace(output.ToString().ReplaceLineEndings("\n"), "<t> s"), "generated <time> from graph <revision>"));
    }

    private (int ExitCode, string Error) Refused(params string[] args)
    {
        var error = new StringWriter();
        int exitCode = CliApp.Run([.. args, "--project", _root], new StringWriter(), error);
        return (exitCode, error.ToString());
    }

    [GeneratedRegex(@"\d+\.\d s")]
    private static partial Regex Seconds();

    [GeneratedRegex(@"generated \d{4}-\d\d-\d\d \d\d:\d\d from graph [0-9a-f]{12}")]
    private static partial Regex Stamp();

    [Fact]
    public void Writes_the_page_and_answers_one_line()
    {
        var (exitCode, first) = Run("view", "LabStage", "--depth", "1");

        Assert.Equal(0, exitCode);
        Assert.Equal("indexed 28 files in <t> s\nLibrary/ungrep/view.html  48 nodes (20 drawn), 43 edges  generated <time> from graph <revision>\n", first);
        string page = File.ReadAllText(Page);
        Assert.Contains("<title>ug view LabStage</title>", page, StringComparison.Ordinal);
        Assert.Contains("\"depth\":1,\"baked\":2,\"layout\":\"dagre\"", page, StringComparison.Ordinal);

        // Up to date: no index line; the same graph, the same revision.
        string revision = Regex.Match(page, "\"revision\":\"([0-9a-f]{12})\"").Groups[1].Value;
        Assert.Equal((0, "Library/ungrep/view.html  48 nodes (20 drawn), 43 edges  generated <time> from graph <revision>\n"), Run("view", "LabStage", "--depth", "1"));
        Assert.Contains($"\"revision\":\"{revision}\"", File.ReadAllText(Page), StringComparison.Ordinal);
    }

    [Fact]
    public void Kind_and_depth_reach_the_page()
    {
        var (_, output) = Run("view", "LabStage", "--kind", "type,method");

        Assert.Equal("indexed 28 files in <t> s\nLibrary/ungrep/view.html  34 nodes (28 drawn), 32 edges  generated <time> from graph <revision>\n", output);
        string page = File.ReadAllText(Page);
        Assert.Contains("\"depth\":2,\"baked\":3,\"layout\":\"dagre\",", page, StringComparison.Ordinal);
        Assert.Contains("\"kinds\":[\"method\",\"type\"]", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Out_writes_where_asked()
    {
        Run("find", "x");
        string inside = Path.Combine(_root, "Pages", "lab.html");
        string outside = Path.Combine(Path.GetTempPath(), "ug-view-out-" + Guid.NewGuid().ToString("N")[..8] + ".html");
        try
        {
            Assert.StartsWith("Pages/lab.html  48 nodes", Run("view", "LabStage", "--depth", "1", "--out", inside).Output, StringComparison.Ordinal);
            Assert.StartsWith(outside + "  48 nodes", Run("view", "LabStage", "--depth", "1", "--out", outside).Output, StringComparison.Ordinal);
            Assert.True(File.Exists(inside));
            Assert.True(File.Exists(outside));
            Assert.False(File.Exists(Page));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void Unknown_and_ambiguous_symbols_write_no_file()
    {
        Run("find", "x");

        Assert.Equal((0, "not found: Nope\n"), Run("view", "Nope"));
        var (exitCode, ambiguous) = Run("view", "Open");
        Assert.Equal(0, exitCode);
        Assert.StartsWith("ambiguous: Open\n", ambiguous, StringComparison.Ordinal);
        Assert.EndsWith("  more  ug view Game.Core.Door.Open\n", ambiguous, StringComparison.Ordinal);
        Assert.False(File.Exists(Page));
    }

    [Fact]
    public void Viewer_off_writes_nothing()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".claude"));
        File.WriteAllText(Path.Combine(_root, ".claude", "ungrep.json"), """{ "modules": { "viewer": false } }""");

        Assert.Equal((0, "viewer is off in .claude/ungrep.json\n"), Run("view", "LabStage"));
        Assert.False(File.Exists(Page));
    }

    [Fact]
    public void Bad_depth_and_kind_are_refused()
    {
        var (depthExit, depthError) = Refused("view", "LabStage", "--depth", "5");
        Assert.NotEqual(0, depthExit);
        Assert.Contains("--depth must be 1 to 4", depthError, StringComparison.Ordinal);

        var (kindExit, kindError) = Refused("view", "LabStage", "--kind", "type,nope");
        Assert.NotEqual(0, kindExit);
        Assert.Contains("--kind: unknown kind 'nope'; kinds: type, method, field, property, event, asset, game_object, component, prefab_instance, sub_asset, script, scene, prefab, scriptable_object,", kindError, StringComparison.Ordinal);

        Assert.False(File.Exists(Page));
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
