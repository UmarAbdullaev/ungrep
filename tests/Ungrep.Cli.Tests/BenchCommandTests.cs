using System.CommandLine;
using Ungrep.Bench;

namespace Ungrep.Cli.Tests;

/// <summary>
/// `ug bench run` (specs/SPEC-bench.md): a dry run in either mode prints what would run and runs
/// nothing; a run writes its results and exits 1 on a ungrep regression against the
/// baseline; usage and suite errors exit 2.
/// </summary>
public sealed class BenchCommandTests : IDisposable
{
    private const string Suite = """
        {
          "suite": "tiny",
          "arms": {
            "grep": {},
            "ungrep": { "own": "(^|\\s)ug\\s" }
          },
          "questions": [
            {
              "id": "Q1", "kind": "navigation", "prompt": "Who calls Foo?",
              "key": { "items": ["Bar\\.cs:12"], "checked": "by hand" },
              "steps": {
                "grep": [ { "run": "grep -rn Foo Assets" }, { "run": "sed -n 1,20p Assets/Bar.cs", "count": false } ],
                "ungrep": [ { "run": "ug refs Foo" } ]
              }
            }
          ]
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-bench-cli-" + Guid.NewGuid().ToString("N"));
    private readonly Host _host = new();

    public BenchCommandTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "project"));
        File.WriteAllText(Path.Combine(_root, "tiny.json"), Suite);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private (int Exit, string Output, string Error) Bench(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var root = new RootCommand { BenchCommand.Create(null, _ => _host) };
        int exit = root.Parse(["bench", "run", Path.Combine(_root, "tiny.json"), "--project", Path.Combine(_root, "project"), "--answers", Path.Combine(_root, "answers"), .. args])
            .Invoke(new InvocationConfiguration { Output = output, Error = error });
        return (exit, output.ToString().ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_script_dry_run_lists_each_leg_and_runs_nothing()
    {
        var (exit, output, _) = Bench("--dry-run");

        Assert.Equal(0, exit);
        Assert.Equal(
            """
            bench tiny script --dry-run: 1 questions x 2 arms; nothing ran
            Q1
              grep: 1 counted
                grep -rn Foo Assets
                (not counted) sed -n 1,20p Assets/Bar.cs
              ungrep: 1 counted
                ug refs Foo

            """,
            output);
        Assert.Empty(_host.Commands);
    }

    [Fact]
    public void An_agent_dry_run_counts_the_runs_and_runs_nothing()
    {
        var (exit, output, _) = Bench("--mode", "agent", "--repeat", "3", "--dry-run");

        Assert.Equal(0, exit);
        Assert.StartsWith("bench tiny agent --dry-run: 6 runs (1 questions x 2 arms x 3 repeats); nothing ran\nQ1 grep #1: claude -p ", output, StringComparison.Ordinal);
        Assert.Empty(_host.Commands);
    }

    [Fact]
    public void A_run_writes_results_and_a_regression_against_the_baseline_exits_1()
    {
        string first = Path.Combine(_root, "first.json");
        var (exit, output, _) = Bench("--out", first);
        Assert.Equal(0, exit);
        Assert.Contains("Q1 navigation  1 / ", output, StringComparison.Ordinal);
        Assert.Equal(["grep", "ungrep"], BenchRun.FromJson(File.ReadAllText(first)).Arms);

        _host.Answers["ug refs Foo"] = "Foo has no callers\n";
        var (worse, report, _) = Bench("--out", Path.Combine(_root, "second.json"), "--baseline", first);

        Assert.Equal(1, worse);
        Assert.Contains("Q1 ungrep: correct in the baseline, not now (missing Bar\\.cs:12)  REGRESSION", report, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--arm", "nope", "ug bench: suite tiny has no arm nope (arms: grep, ungrep)\n")]
    [InlineData("--mode", "fast", "ug bench: --mode is script or agent, not fast\n")]
    [InlineData("--repeat", "0", "ug bench: --repeat is 1 or more\n")]
    public void Usage_errors_exit_2(string option, string value, string message)
    {
        var (exit, _, error) = Bench(option, value);

        Assert.Equal((2, message), (exit, error));
    }

    private sealed class Host : IBenchHost
    {
        public List<string> Commands { get; } = [];

        public Dictionary<string, string> Answers { get; } = new(StringComparer.Ordinal)
        {
            ["grep -rn Foo Assets"] = "Assets/Bar.cs:12:    Foo();\n",
            ["ug refs Foo"] = "Foo  Assets/Bar.cs:12  Bar.Awake\n",
        };

        public CallAnswer Shell(string command, string cwd, TimeSpan timeout)
        {
            Commands.Add(command);
            return Answers.TryGetValue(command, out string? text) ? new CallAnswer(text, 0, TimeSpan.FromMilliseconds(100)) : new CallAnswer("", 1, TimeSpan.Zero);
        }

        public string? ReadFile(string path) => null;

        public IMcpSession OpenMcp(McpServer server, string cwd) => throw new McpException("no servers here");

        public (int Exit, string Stdout, string Stderr, TimeSpan Elapsed) Exec(string program, IReadOnlyList<string> args, string cwd, TimeSpan timeout, string? stdin = null) =>
            throw new InvalidOperationException("no claude here");
    }
}
