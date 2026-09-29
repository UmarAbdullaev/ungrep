namespace UnityGraph.Cli.Tests;

/// <summary>
/// `ug stats` end to end: a Claude config folder built from the Stats fixtures, captured output.
/// </summary>
public sealed class StatsCommandTests : IDisposable
{
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "ug-stats-" + Guid.NewGuid().ToString("N"));
    private readonly string _project;

    public StatsCommandTests()
    {
        _project = Path.Combine(_configDir, "work", "Game");
        string transcripts = Path.Combine(_configDir, "projects", Stats.ClaudeProjects.DirectoryName(_project));
        Directory.CreateDirectory(transcripts);
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "*.jsonl"))
        {
            File.Copy(file, Path.Combine(transcripts, Path.GetFileName(file)));
        }
    }

    public void Dispose() => Directory.Delete(_configDir, recursive: true);

    private (int ExitCode, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exitCode = CliApp.Run(args, output, error, _configDir);
        return (exitCode, output.ToString(), error.ToString());
    }

    [Fact]
    public void Prints_the_text_report()
    {
        var (exitCode, output, error) = Run("stats", "--project", _project, "--until", "2026-09-22");

        Assert.Equal(0, exitCode);
        Assert.Equal("", error);
        Golden.Match(output);
    }

    [Fact]
    public void Prints_json_and_compares_against_it()
    {
        string baseline = Path.Combine(_configDir, "baseline.json");
        var saved = Run("stats", "--project", _project, "--until", "2026-09-20", "--json");
        File.WriteAllText(baseline, saved.Output);

        var (exitCode, output, error) = Run("stats", "--project", _project, "--baseline", baseline);

        Assert.Equal(0, saved.ExitCode);
        Assert.Equal(0, exitCode);
        Assert.Equal("", error);
        Golden.Match(output);
    }

    [Fact]
    public void Missing_transcripts_fail_with_a_message()
    {
        var (exitCode, output, error) = Run("stats", "--project", Path.Combine(_configDir, "work", "Other"));

        Assert.Equal(1, exitCode);
        Assert.Equal("", output);
        Assert.StartsWith("ug stats: no Claude Code transcripts for ", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--since", "yesterday")]
    [InlineData("--until", "2026-13-01")]
    public void Bad_time_fails_with_usage(string option, string value)
    {
        var (exitCode, _, error) = Run("stats", "--project", _project, option, value);

        Assert.Equal(2, exitCode);
        Assert.Contains("--since/--until", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Unreadable_baseline_fails_with_a_message()
    {
        var (exitCode, _, error) = Run("stats", "--project", _project, "--baseline", Path.Combine(_configDir, "none.json"));

        Assert.Equal(1, exitCode);
        Assert.StartsWith("ug stats: can't read baseline ", error, StringComparison.Ordinal);
    }
}
