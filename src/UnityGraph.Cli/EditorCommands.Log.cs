using System.CommandLine;
using System.Globalization;
using UnityGraph.Core;
using UnityGraph.EditorLink;
using UnityGraph.Query;

namespace UnityGraph.Cli;

/// <summary>
/// `ug log` and `ug play --for` (SPEC-unity-loop, Commands): what the last play session did, in the
/// shape of `ug console`: errors and warnings folded, logs only counted.
/// </summary>
internal static partial class EditorCommands
{
    private const int LogPage = 500;

    /// <summary>Replaces the wait of `ug play --for`; tests only.</summary>
    internal static Action<TimeSpan> PlaySleep { get; set; } = Thread.Sleep;

    private static IEnumerable<Command> LogCommands()
    {
        Option<int?> budget = BudgetOption();
        yield return Command(
            "log",
            "What the last play session did: how long, pauses, and its errors and warnings folded, logs counted.",
            [budget],
            (root, config, result) => LogDigest(root, config, result.GetValue(budget)));
    }

    public static (int ExitCode, string Text) LogDigest(string root, UnityGraphConfig config, int? budget)
    {
        if (!config.IsEnabled("unity-loop"))
        {
            return LoopOff();
        }

        BridgeConnection connection = BridgeClient.Connect(root, ProcessCheck);
        if (connection.Client == null)
        {
            return Unreachable(root, "log", connection.Problem);
        }

        using BridgeClient client = connection.Client;
        if (!client.HasPlayLog)
        {
            return (0, "bridge " + client.Info.Bridge + " is too old for ug log: run ug bridge install again\n");
        }

        try
        {
            PlaySession? session = client.PlayLog().Session;
            if (session == null)
            {
                return (0, "no play session since the editor opened\n");
            }

            ConsolePage problems = client.Console(session.After, LogPage, "error,warning");
            ConsolePage logs = client.Console(session.After, LogPage, "log");
            return (0, LogAnswer(session, problems, logs, BudgetOf(config, budget)));
        }
        catch (BridgeUnavailableException e)
        {
            return NoAnswer(e);
        }
    }

    private static string LogAnswer(PlaySession session, ConsolePage problems, ConsolePage logs, int budget)
    {
        bool playing = session.End == null;
        string seconds = ((session.End ?? DateTimeOffset.UtcNow) - session.Start).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        string events = string.Join(", ", session.Events.Select(e => e.What + " " + e.At.ToString("0.0", CultureInfo.InvariantCulture) + " s"));
        var answer = new Answer(budget);
        answer.Add((playing ? "playing " + seconds + " s so far" : "play " + seconds + " s, stopped " + session.End!.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture))
            + (events.Length == 0 ? "" : "  (" + events + ")"));

        // An id above `until` belongs to what happened after the session ended.
        IEnumerable<ConsoleEntry> inSession(ConsolePage page) => page.Entries.Where(e => session.Until is not { } until || e.Id <= until);
        (string counts, List<string> rows) = FoldConsole(inSession(problems), logRows: false);
        int logCount = inSession(logs).Sum(e => e.Count);
        List<string> parts = counts.Length == 0 ? [] : [counts];
        if (logCount > 0)
        {
            parts.Add(logCount.ToString(CultureInfo.InvariantCulture) + (logs.Entries.Count >= LogPage ? "+" : "") + (logCount == 1 ? " log" : " logs"));
        }

        long last = session.Until ?? problems.Last;
        answer.Add("  " + (parts.Count == 0 ? "nothing logged" : string.Join(", ", parts)) + "  (last #" + last.ToString(CultureInfo.InvariantCulture) + ")");
        answer.AddRange(rows);
        return answer.Finish("ug log");
    }

    // `ug play`; with --for the editor plays that many seconds, stops, and the log digest follows; with --step a paused editor advances frames.
    private static (int ExitCode, string Text) Play(string root, UnityGraphConfig config, int timeout, int? seconds, int? step = null)
    {
        if (step != null && seconds != null)
        {
            return (1, "--step and --for cannot be used together\n");
        }

        if (step is < 1 or > 600)
        {
            return (1, "--step must be 1 to 600 frames\n");
        }

        if (step is { } frames)
        {
            return Input(root, config, "play --step", Query("/input/step", ("frames", frames.ToString(CultureInfo.InvariantCulture))), frames * 0.2);
        }

        if (seconds is < 1 or > 600)
        {
            return (1, "--for must be 1 to 600 seconds\n");
        }

        (int exitCode, string text) = Op(root, config, "play", new OpRequest("/play") { WaitTimeout = TimeSpan.FromSeconds(Math.Max(1, timeout)) });
        if (seconds is not { } playFor || _opFailed)
        {
            return (exitCode, text);
        }

        PlaySleep(TimeSpan.FromSeconds(playFor));
        (_, string stopped) = Op(root, config, "stop", new OpRequest("/stop") { WaitTimeout = TimeSpan.FromSeconds(Math.Max(1, timeout)) });
        return (0, (_opFailed ? stopped : "") + LogDigest(root, config, null).Text);
    }
}
