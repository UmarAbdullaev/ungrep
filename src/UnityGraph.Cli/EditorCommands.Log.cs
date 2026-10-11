using System.CommandLine;
using System.Globalization;
using UnityGraph.Core;
using UnityGraph.EditorLink;
using UnityGraph.Query;

namespace UnityGraph.Cli;

/// <summary>
/// `ug log` and `ug play --for` (SPEC-unity-loop, Commands): what the last play session did, in the
/// shape of `ug console`: errors and warnings folded, logs only counted. `--whole` (bridge 0.9.0)
/// prints the rows whole, as `ug console --whole` does.
/// </summary>
internal static partial class EditorCommands
{
    private const int LogPage = 500;

    private static readonly TimeSpan _pausePoll = TimeSpan.FromMilliseconds(250);

    /// <summary>Replaces the wait of `ug play --for`; tests only.</summary>
    internal static Action<TimeSpan> PlaySleep { get; set; } = Thread.Sleep;

    private static IEnumerable<Command> LogCommands()
    {
        Option<int?> budget = BudgetOption();
        var whole = new Option<bool>("--whole") { Description = "Each row with its id, its whole message and its whole stack (bridge 0.9.0)." };
        yield return Command(
            "log",
            "What the last play session did: how long, pauses, and its errors and warnings folded, logs counted.",
            [budget, whole],
            (root, config, result) => LogDigest(root, config, result.GetValue(budget), result.GetValue(whole)));
    }

    public static (int ExitCode, string Text) LogDigest(string root, UnityGraphConfig config, int? budget, bool whole = false)
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

        if (whole && !client.HasFull)
        {
            return (0, "bridge " + client.Info.Bridge + " is too old for ug log --whole: run ug bridge install again\n");
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
            return (0, LogAnswer(session, problems, logs, BudgetOf(config, budget), whole, client.HasFull));
        }
        catch (BridgeUnavailableException e)
        {
            return NoAnswer(e);
        }
    }

    private static string LogAnswer(PlaySession session, ConsolePage problems, ConsolePage logs, int budget, bool whole, bool full)
    {
        bool playing = session.End == null;
        string seconds = ((session.End ?? DateTimeOffset.UtcNow) - session.Start).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        string events = string.Join(", ", session.Events.Select(e => e.What + " " + e.At.ToString("0.0", CultureInfo.InvariantCulture) + " s"));
        var answer = new Answer(budget);
        answer.Add((playing ? "playing " + seconds + " s so far" : "play " + seconds + " s, stopped " + session.End!.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture))
            + (events.Length == 0 ? "" : "  (" + events + ")"));

        // An id above `until` belongs to what happened after the session ended.
        IEnumerable<ConsoleEntry> inSession(ConsolePage page) => page.Entries.Where(e => session.Until is not { } until || e.Id <= until);
        (string counts, List<string> rows, bool rowCut) = FoldConsole(inSession(problems), logRows: false, whole);
        int logCount = inSession(logs).Sum(e => e.Count);

        // A page of 500 entries is all the digest reads; the rest of the session is named by its console ids.
        var more = new List<string>();
        if (full && rowCut && !whole)
        {
            more.Add("whole: ug log --whole");
        }

        string after = session.After.ToString(CultureInfo.InvariantCulture);
        if (full && problems.Entries.Count >= LogPage)
        {
            more.Add("earlier problems: ug console --after " + after + " --before " + problems.Entries.Min(e => e.Id).ToString(CultureInfo.InvariantCulture));
        }

        if (full && logs.Entries.Count >= LogPage)
        {
            more.Add("earlier logs: ug console --after " + after + " --before " + logs.Entries.Min(e => e.Id).ToString(CultureInfo.InvariantCulture) + " --level log");
        }

        if (more.Count > 0)
        {
            answer.Footer("  more  " + string.Join("; ", more));
        }
        List<string> parts = counts.Length == 0 ? [] : [counts];
        if (logCount > 0)
        {
            parts.Add(logCount.ToString(CultureInfo.InvariantCulture) + (logs.Entries.Count >= LogPage ? "+" : "") + (logCount == 1 ? " log" : " logs"));
        }

        long last = session.Until ?? problems.Last;
        answer.Add("  " + (parts.Count == 0 ? "nothing logged" : string.Join(", ", parts)) + "  (last #" + last.ToString(CultureInfo.InvariantCulture) + ")");
        answer.AddRange(rows);
        return answer.Finish(whole ? "ug log --whole" : "ug log");
    }

    // `ug play`; with --for the editor plays that many seconds, stops, and the log digest follows; with --step a paused editor advances frames.
    // --paused starts on frame 1 and --pause-on-error stops at the first error (SPEC-editor-ops, Play-mode debugging): both print /play/state.
    private static (int ExitCode, string Text) Play(string root, UnityGraphConfig config, int timeout, int? seconds, int? step = null, bool whole = false, bool paused = false, bool onError = false)
    {
        if (whole && seconds == null)
        {
            return (1, "--whole goes with --for\n");
        }

        if (step != null && seconds != null)
        {
            return (1, "--step and --for cannot be used together\n");
        }

        if (paused && seconds != null)
        {
            return (1, "--paused and --for cannot be used together\n");
        }

        if (step != null && (paused || onError))
        {
            return (1, "--step cannot be used with --paused or --pause-on-error\n");
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

        bool debug = paused || onError;
        var play = new OpRequest(Query("/play", ("paused", paused ? "true" : null), ("onerror", onError ? "true" : null)))
        {
            WaitTimeout = TimeSpan.FromSeconds(Math.Max(1, timeout)),
            PlayDebug = debug,
            Option = paused ? "--paused" : onError ? "--pause-on-error" : null,
            Then = debug && seconds == null ? "/play/state" : null,
        };
        (int exitCode, string text) = Op(root, config, "play", play);
        if (seconds is not { } playFor || _opFailed)
        {
            return (exitCode, text);
        }

        if (!onError)
        {
            PlaySleep(TimeSpan.FromSeconds(playFor));
        }
        else if (PausedWithin(root, playFor))
        {
            return Op(root, config, "play", new OpRequest("/play/state") { PlayDebug = true, Option = "--pause-on-error" });
        }

        (_, string stopped) = Op(root, config, "stop", new OpRequest("/stop") { WaitTimeout = TimeSpan.FromSeconds(Math.Max(1, timeout)) });
        return (0, (_opFailed ? stopped : "") + LogDigest(root, config, null, whole).Text);
    }

    // `ug play --for <s> --pause-on-error`: polls the status every 250 ms for up to that long; true when the editor paused.
    private static bool PausedWithin(string root, int seconds)
    {
        BridgeClient? client = _session;
        BridgeConnection? connection = null;
        if (client == null)
        {
            connection = BridgeClient.Connect(root, ProcessCheck);
            client = connection.Client;
            if (client == null)
            {
                return false;
            }
        }

        try
        {
            for (double waited = 0; waited < seconds; waited += _pausePoll.TotalSeconds)
            {
                string state = client.Status().State;
                if (state == "paused")
                {
                    return true;
                }

                if (state == "idle")
                {
                    return false;
                }

                PlaySleep(_pausePoll);
            }

            return false;
        }
        catch (BridgeUnavailableException)
        {
            return false;
        }
        finally
        {
            connection?.Client?.Dispose();
        }
    }
}
