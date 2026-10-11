using System.CommandLine;
using System.Globalization;
using System.Text;
using UnityGraph.Core;
using UnityGraph.EditorLink;
using UnityGraph.Query;

namespace UnityGraph.Cli;

/// <summary>
/// `ug test` (SPEC-unity-loop, Commands): a test run in the open editor, failures only with
/// `file:line`, one line when green. From bridge 0.9.0 `--failures` lists every failure of the
/// last run and `--failure` shows one whole (Nothing out of reach).
/// </summary>
internal static partial class EditorCommands
{
    private const int MaxFailureLines = 20;

    private static IEnumerable<Command> TestCommands()
    {
        var mode = new Option<string>("--mode") { Description = "EditMode or PlayMode. Default: EditMode.", DefaultValueFactory = _ => "EditMode" };
        var filter = new Option<string?>("--filter") { Description = "Regex over the full test name. Default: every test." };
        var timeout = new Option<int>("--timeout") { Description = "Seconds to wait for the run. Default: 300.", DefaultValueFactory = _ => 300 };
        var failures = new Option<bool>("--failures") { Description = "Every failure of the last run, no new run (bridge 0.9.0)." };
        var failure = new Option<string?>("--failure") { Description = "One failure of the last run whole: its name, whole message and stack; the full test name or a unique part (bridge 0.9.0)." };
        Option<int?> budget = BudgetOption();
        yield return Command(
            "test",
            "Run EditMode or PlayMode tests in the open editor and wait; failures only, with file:line, one line when green.",
            [mode, filter, timeout, failures, failure, budget],
            (root, config, result) => result.GetValue(failures) || result.GetValue(failure) != null
                ? LastFailures(root, config, result.GetValue(failure), result.GetValue(budget))
                : Tests(root, config, result.GetValue(mode)!, result.GetValue(filter), TimeSpan.FromSeconds(Math.Max(1, result.GetValue(timeout)))));
    }

    /// <summary>`ug test --failures` and `ug test --failure &lt;name&gt;`: the last run's failures, read again.</summary>
    internal static (int ExitCode, string Text) LastFailures(string root, UnityGraphConfig config, string? name, int? budget)
    {
        if (!config.IsEnabled("unity-loop"))
        {
            return LoopOff();
        }

        BridgeConnection connection = BridgeClient.Connect(root, ProcessCheck);
        if (connection.Client == null)
        {
            return Unreachable(root, "test", connection.Problem);
        }

        using BridgeClient client = connection.Client;
        if (!client.HasFull)
        {
            return (0, "bridge " + client.Info.Bridge + " is too old for ug test " + (name == null ? "--failures" : "--failure") + ": run ug bridge install again\n");
        }

        TestRunResult result;
        try
        {
            result = client.TestResult("");
        }
        catch (BridgeUnavailableException e)
        {
            return NoAnswer(e);
        }

        if (result.State == "none")
        {
            return (0, "no test run since the editor opened\n");
        }

        if (result.IsRunning)
        {
            return (0, "the last test run is still running\n");
        }

        if (result.IsStopped)
        {
            return (0, "last run: " + result.Reason + "\n");
        }

        var answer = new Answer(BudgetOf(config, budget));
        IReadOnlyList<TestFailure> failed = result.Failures ?? [];
        if (name == null)
        {
            answer.Add("last run: " + TestCounts(result));
            answer.AddRange(failed.Select(FailureRow));
            return (0, answer.Finish("ug test --failures"));
        }

        List<TestFailure> matches = [.. failed.Where(f => f.Name == name)];
        if (matches.Count == 0)
        {
            matches = [.. failed.Where(f => f.Name.Contains(name, StringComparison.OrdinalIgnoreCase))];
        }

        if (matches.Count == 0)
        {
            return (0, "no failure matching " + name + " in the last run\n");
        }

        if (matches.Count > 1)
        {
            answer.Add(matches.Count.ToString(CultureInfo.InvariantCulture) + " failures match " + name + ": give more of the name");
            answer.AddRange(matches.Select(f => "  " + f.Name));
            return (0, answer.Finish("ug test --failure " + name));
        }

        TestFailure one = matches[0];
        answer.Add(one.Name + "  " + (one.Frame ?? "(no file)"));
        if (one.Message is { Length: > 0 } message)
        {
            answer.AddRange(Lines(message));
        }

        if (one.Stack is { Length: > 0 } stack)
        {
            answer.Add("stack:");
            answer.AddRange(Lines(stack));
        }

        return (0, answer.Finish("ug test --failure " + one.Name));
    }

    private static string FailureRow(TestFailure failure)
    {
        string message = FirstLine(failure.Message);
        return "  " + (failure.Frame ?? "(no file)") + "  " + failure.Name + (message.Length == 0 ? "" : "  " + message);
    }

    // `3 failed, 120 passed in 4.2 s`, or `no tests ran`.
    private static string TestCounts(TestRunResult result)
    {
        var parts = new List<string>();
        AddPart(parts, result.Failed, "failed");
        AddPart(parts, result.Passed, "passed");
        AddPart(parts, result.Skipped, "skipped");
        AddPart(parts, result.Inconclusive, "inconclusive");
        return parts.Count == 0 ? "no tests ran" : string.Join(", ", parts) + " in " + result.Seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

        static void AddPart(List<string> list, int n, string what)
        {
            if (n > 0)
            {
                list.Add(n.ToString(CultureInfo.InvariantCulture) + " " + what);
            }
        }
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

        return (0, TestAnswer(platform, outcome.Result, client.HasFull));
    }

    /// <summary>
    /// The run's counts and up to 20 failure rows, or why a stopped run has none.
    /// <paramref name="full"/>: the bridge keeps every failure whole, so the answer names
    /// `--failures` for the rest and `--failure` for a cut row.
    /// </summary>
    internal static string TestAnswer(string platform, TestRunResult result, bool full = false)
    {
        if (result.IsStopped)
        {
            return platform + ": " + result.Reason + "\n";
        }

        var text = new StringBuilder(platform).Append(": ").Append(TestCounts(result)).Append('\n');
        IReadOnlyList<TestFailure> failures = result.Failures ?? [];
        foreach (TestFailure failure in failures.Take(MaxFailureLines))
        {
            text.Append(FailureRow(failure)).Append('\n');
        }

        // A bridge before 0.9.0 sends at most 50 failures, so a longer list than that is counted from the totals.
        int more = Math.Max(result.Failed, failures.Count) - MaxFailureLines;
        if (more > 0)
        {
            text.Append("  (+").Append(more.ToString(CultureInfo.InvariantCulture)).Append(" more failures").Append(full ? ": ug test --failures" : "").Append(")\n");
        }

        // NUnit's message goes on past its first line (`But was:`), and the stack past the frame.
        if (full && failures.Take(MaxFailureLines).Any(f => (f.Message ?? "").Trim() != FirstLine(f.Message) || Lines(f.Stack).Count > 1))
        {
            text.Append("  more  whole: ug test --failure <name>\n");
        }

        return text.ToString();
    }
}
