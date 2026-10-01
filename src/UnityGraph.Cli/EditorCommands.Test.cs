using System.CommandLine;
using System.Globalization;
using System.Text;
using UnityGraph.Core;
using UnityGraph.EditorLink;

namespace UnityGraph.Cli;

/// <summary>
/// `ug test` (SPEC-unity-loop, Commands): a test run in the open editor, failures only with
/// `file:line`, one line when green.
/// </summary>
internal static partial class EditorCommands
{
    private const int MaxFailureLines = 20;

    private static IEnumerable<Command> TestCommands()
    {
        var mode = new Option<string>("--mode") { Description = "EditMode or PlayMode. Default: EditMode.", DefaultValueFactory = _ => "EditMode" };
        var filter = new Option<string?>("--filter") { Description = "Regex over the full test name. Default: every test." };
        var timeout = new Option<int>("--timeout") { Description = "Seconds to wait for the run. Default: 300.", DefaultValueFactory = _ => 300 };
        yield return Command(
            "test",
            "Run EditMode or PlayMode tests in the open editor and wait; failures only, with file:line, one line when green.",
            [mode, filter, timeout],
            (root, config, result) => Tests(root, config, result.GetValue(mode)!, result.GetValue(filter), TimeSpan.FromSeconds(Math.Max(1, result.GetValue(timeout)))));
    }

    public static (int ExitCode, string Text) Tests(string root, UnityGraphConfig config, string mode, string? filter, TimeSpan timeout)
    {
        if (!config.IsEnabled("unity-loop"))
        {
            return LoopOff();
        }

        string? platform = mode.ToLowerInvariant() switch { "editmode" => "EditMode", "playmode" => "PlayMode", _ => null };
        if (platform == null)
        {
            return (1, "--mode must be EditMode or PlayMode\n");
        }

        BridgeConnection connection = BridgeClient.Connect(root, ProcessCheck);
        if (connection.Client == null)
        {
            return Unreachable(root, "test", connection.Problem);
        }

        using BridgeClient client = connection.Client;
        if (connection.Status!.State is "playing" or "paused")
        {
            return (0, "not run: Unity is in play mode: ug stop first\n");
        }

        // The run would start on stale or broken scripts otherwise.
        CompileOutcome compiled = client.Compile(TimeSpan.FromSeconds(60));
        if (compiled.Problem != null)
        {
            return (0, "not run: " + compiled.Problem + "\n");
        }

        IReadOnlyList<CompileMessage> messages = compiled.Result?.Messages ?? [];
        if (messages.Any(m => m.IsError))
        {
            return (0, "not run: the scripts do not compile (" + CompileText.Counts(messages) + ")\n" + CompileText.Lines(messages, warnings: false));
        }

        TestOutcome outcome = client.RunTests(platform, filter, timeout);
        if (outcome.Result == null)
        {
            string problem = outcome.Problem!;
            return (0, (outcome.TimedOut ? "" : "not run: ") + (problem.StartsWith("bridge answered ", StringComparison.Ordinal) ? problem[(problem.IndexOf(": ", StringComparison.Ordinal) + 2)..] : problem) + "\n");
        }

        return (0, TestAnswer(platform, outcome.Result));
    }

    internal static string TestAnswer(string platform, TestRunResult result)
    {
        var parts = new List<string>();
        AddPart(parts, result.Failed, "failed");
        AddPart(parts, result.Passed, "passed");
        AddPart(parts, result.Skipped, "skipped");
        AddPart(parts, result.Inconclusive, "inconclusive");
        var text = new StringBuilder(platform).Append(": ");
        text.Append(parts.Count == 0 ? "no tests ran" : string.Join(", ", parts) + " in " + result.Seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s").Append('\n');
        IReadOnlyList<TestFailure> failures = result.Failures ?? [];
        foreach (TestFailure failure in failures.Take(MaxFailureLines))
        {
            string message = FirstLine(failure.Message);
            text.Append("  ").Append(failure.Frame ?? "(no file)").Append("  ").Append(failure.Name).Append(message.Length == 0 ? "" : "  " + message).Append('\n');
        }

        // The bridge sends at most 50 failures, so a longer list than that is counted from the totals.
        int more = Math.Max(result.Failed, failures.Count) - MaxFailureLines;
        if (more > 0)
        {
            text.Append("  (+").Append(more.ToString(CultureInfo.InvariantCulture)).Append(" more failures)\n");
        }

        return text.ToString();

        static void AddPart(List<string> list, int n, string what)
        {
            if (n > 0)
            {
                list.Add(n.ToString(CultureInfo.InvariantCulture) + " " + what);
            }
        }
    }
}
