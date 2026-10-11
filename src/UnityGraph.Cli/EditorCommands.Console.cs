using System.CommandLine;
using System.Globalization;
using UnityGraph.Core;
using UnityGraph.EditorLink;
using UnityGraph.Query;

namespace UnityGraph.Cli;

/// <summary>
/// `ug console` (SPEC-unity-loop, Commands): the editor console as a short digest, errors first,
/// repeats folded. The folding and the row format are shared with `ug log`. From bridge 0.9.0
/// `--id`, `--whole` and `--before` show what the digest leaves out (Nothing out of reach).
/// </summary>
internal static partial class EditorCommands
{
    private const int ConsoleTextLimit = 160;

    private static IEnumerable<Command> ConsoleCommands()
    {
        var level = new Option<string>("--level") { Description = "Lowest level shown: error, warning or log. Default: warning (errors and warnings).", DefaultValueFactory = _ => "warning" };
        var count = new Option<int>("--count") { Description = "How many of the newest entries to look at, 1 to 500. Default: 200.", DefaultValueFactory = _ => 200 };
        var after = new Option<long?>("--after") { Description = "Only entries newer than this console id (the #id a previous answer ended with)." };
        var before = new Option<long?>("--before") { Description = "Only entries older than this console id, to page back (bridge 0.9.0)." };
        var whole = new Option<bool>("--whole") { Description = "Each row with its id, its whole message and its whole stack (bridge 0.9.0)." };
        var id = new Option<long?>("--id") { Description = "One entry whole: every line of its message and its stack (bridge 0.9.0)." };
        Option<int?> budget = BudgetOption();
        yield return Command(
            "console",
            "The editor console, errors first, repeats folded (x12), within the answer budget; ends with a `more` line when cut.",
            [level, count, after, before, whole, id, budget],
            (root, config, result) => ConsoleDigest(root, config, new ConsoleAsk(result.GetValue(level)!, result.GetValue(count), result.GetValue(after), result.GetValue(budget))
            {
                Before = result.GetValue(before),
                Whole = result.GetValue(whole),
                Id = result.GetValue(id),
            }));
    }

    /// <summary>What `ug console` was asked: the digest's options and the C1 ones.</summary>
    internal sealed record ConsoleAsk(string Level, int Count, long? After, int? Budget)
    {
        public long? Before { get; init; }

        public bool Whole { get; init; }

        public long? Id { get; init; }

        // The C1 option given, for the refusal on an older bridge.
        public string? FullOption => Id != null ? "--id" : Whole ? "--whole" : Before != null ? "--before" : null;
    }

    private static Option<int?> BudgetOption() =>
        new("--budget") { Description = $"Answer budget in tokens, {Answer.MinBudget} to {Answer.MaxBudget}. Default: answerBudget in .claude/unitygraph.json." };

    public static (int ExitCode, string Text) ConsoleDigest(string root, UnityGraphConfig config, string level, int count, long? after, int? budget) =>
        ConsoleDigest(root, config, new ConsoleAsk(level, count, after, budget));

    internal static (int ExitCode, string Text) ConsoleDigest(string root, UnityGraphConfig config, ConsoleAsk ask)
    {
        if (!config.IsEnabled("unity-loop"))
        {
            return LoopOff();
        }

        if (ask.Level is not ("error" or "warning" or "log"))
        {
            return (1, "--level must be error, warning or log\n");
        }

        if (ask.Id != null && (ask.Whole || ask.After != null || ask.Before != null))
        {
            return (1, "--id takes no other flag but --budget\n");
        }

        int count = Math.Clamp(ask.Count, 1, 500);
        BridgeConnection connection = BridgeClient.Connect(root, ProcessCheck);
        if (connection.Client == null)
        {
            return Unreachable(root, "console", connection.Problem);
        }

        using BridgeClient client = connection.Client;
        if (ask.FullOption is { } option && !client.HasFull)
        {
            return (0, "bridge " + client.Info.Bridge + " is too old for ug console " + option + ": run ug bridge install again\n");
        }

        try
        {
            if (ask.Id is { } id)
            {
                return (0, ConsoleEntryAnswer(client.ConsoleEntry(id), id, BudgetOf(config, ask.Budget)));
            }

            string? types = ask.Level switch { "error" => "error", "warning" => "error,warning", _ => null };
            ConsolePage page = client.Console(ask.After ?? 0, count, types, ask.Before);
            return (0, ConsoleAnswer(page, ask with { Count = count }, client.HasFull, BudgetOf(config, ask.Budget)));
        }
        catch (BridgeUnavailableException e)
        {
            return NoAnswer(e);
        }
    }

    private static string ConsoleAnswer(ConsolePage page, ConsoleAsk ask, bool full, int budget)
    {
        (string counts, List<string> rows, bool rowCut) = FoldConsole(page.Entries, logRows: true, ask.Whole);
        var answer = new Answer(budget);
        string options = (ask.Level == "warning" ? "" : " --level " + ask.Level) + (ask.Count == 200 ? "" : " --count " + ask.Count.ToString(CultureInfo.InvariantCulture))
            + (ask.After is { } after ? " --after " + after.ToString(CultureInfo.InvariantCulture) : "");
        var more = new List<string>();
        if (full && rowCut && !ask.Whole)
        {
            more.Add("whole: ug console --whole" + options + (ask.Before is { } before ? " --before " + before.ToString(CultureInfo.InvariantCulture) : ""));
        }

        // The ring holds entries older than the oldest one looked at: they page back with --before.
        long first = page.Entries.Count == 0 ? 0 : page.Entries.Min(e => e.Id);
        if (full && page.Entries.Count >= ask.Count && page.Oldest > 0 && page.Oldest < first && first > (ask.After ?? 0) + 1)
        {
            more.Add("older: ug console" + (ask.Whole ? " --whole" : "") + options + " --before " + first.ToString(CultureInfo.InvariantCulture));
        }

        if (more.Count > 0)
        {
            answer.Footer("  more  " + string.Join("; ", more));
        }

        string last = "(last #" + page.Last.ToString(CultureInfo.InvariantCulture) + ")";
        answer.Add(counts.Length == 0
            ? "console  nothing at " + ask.Level + (ask.Level == "error" ? " level" : " level or worse") + "  " + last
            : "console  " + counts + "  " + last);
        answer.AddRange(rows);
        return answer.Finish("ug console" + (ask.Whole ? " --whole" : "") + " --level " + ask.Level + " --count " + ask.Count.ToString(CultureInfo.InvariantCulture)
            + (ask.After is { } a ? " --after " + a.ToString(CultureInfo.InvariantCulture) : "")
            + (ask.Before is { } b ? " --before " + b.ToString(CultureInfo.InvariantCulture) : ""));
    }

    // `ug console --id`: one entry whole.
    private static string ConsoleEntryAnswer(ConsolePage page, long id, int budget)
    {
        string number = id.ToString(CultureInfo.InvariantCulture);
        if (page.Entries.Count == 0)
        {
            return "no console entry #" + number + " (the bridge keeps the newest 2000)\n";
        }

        ConsoleEntry entry = page.Entries[0];
        string type = entry.Type is "warning" or "log" ? entry.Type : "error";
        var answer = new Answer(budget);
        answer.Add("#" + number + " " + type + (entry.Count > 1 ? " x" + entry.Count.ToString(CultureInfo.InvariantCulture) : ""));
        answer.AddRange(Lines(entry.Text));
        if (entry.Stack is { Length: > 0 } stack)
        {
            answer.Add("stack:");
            answer.AddRange(Lines(stack));
        }

        return answer.Finish("ug console --id " + number);
    }

    private static int BudgetOf(UnityGraphConfig config, int? budget) =>
        Math.Clamp(budget ?? config.AnswerBudget, Answer.MinBudget, Answer.MaxBudget);

    private static (int, string) LoopOff() => (0, "unity-loop is off in " + UnityGraphConfig.RelativePath + "\n");

    private static (int, string) Unreachable(string root, string command, string? problem) =>
        (0, "Unity not reachable (" + problem + "); ug " + command + " needs the bridge\n" + InstallHint(root));

    private static (int, string) NoAnswer(BridgeUnavailableException e) => (0, "no answer from Unity (" + e.Message + ")\n");

    /// <summary>
    /// Folds entries with the same level, text and frame into one row (`error x12  text  frame`),
    /// errors, then warnings, then logs, the most recently seen first within a level. The counts
    /// text (`2 errors, 1 warning, 212 logs`, empty when nothing) counts every occurrence.
    /// <paramref name="logRows"/>: false keeps log entries out of the rows (they are only counted).
    /// <paramref name="whole"/>: each row carries the id of its newest occurrence and, under it, that
    /// occurrence's whole message (when the row cut it) and whole stack. Cut: a row left out part of
    /// its message or a stack beyond its frame.
    /// </summary>
    internal static (string Counts, List<string> Rows, bool Cut) FoldConsole(IEnumerable<ConsoleEntry> entries, bool logRows, bool whole = false)
    {
        var groups = new Dictionary<(string Type, string Text, string? Frame), (int Count, ConsoleEntry Newest)>();
        var totals = new Dictionary<string, int>(StringComparer.Ordinal) { ["error"] = 0, ["warning"] = 0, ["log"] = 0 };
        foreach (ConsoleEntry entry in entries)
        {
            string type = entry.Type is "warning" or "log" ? entry.Type : "error";
            (string Type, string Text, string? Frame) key = (type, FirstLine(entry.Text), entry.Frame);
            (int count, ConsoleEntry? newest) = groups.GetValueOrDefault(key);
            groups[key] = (count + entry.Count, newest == null || entry.Id > newest.Id ? entry : newest);
            totals[type] += entry.Count;
        }

        bool cut = false;
        var rows = new List<string>();
        foreach (var group in groups
            .Where(g => logRows || g.Key.Type != "log")
            .OrderBy(g => g.Key.Type == "error" ? 0 : g.Key.Type == "warning" ? 1 : 2)
            .ThenByDescending(g => g.Value.Newest.Id))
        {
            ConsoleEntry newest = group.Value.Newest;
            bool textCut = (newest.Text ?? "").Trim() != group.Key.Text;
            List<string> stack = newest.Stack is { Length: > 0 } s ? Lines(s) : [];
            cut |= textCut || stack.Count > 1;
            rows.Add("  " + group.Key.Type + (group.Value.Count > 1 ? " x" + group.Value.Count.ToString(CultureInfo.InvariantCulture) : "")
                + (whole ? "  #" + newest.Id.ToString(CultureInfo.InvariantCulture) : "")
                + "  " + group.Key.Text + (group.Key.Frame == null ? "" : "  " + group.Key.Frame));
            if (whole)
            {
                if (textCut)
                {
                    rows.AddRange(Lines(newest.Text).Select(line => "    " + line));
                }

                if (stack.Count > 0)
                {
                    rows.Add("    stack:");
                    rows.AddRange(stack.Select(line => "      " + line));
                }
            }
        }

        var parts = new List<string>();
        AddCount(parts, totals["error"], "error", "errors");
        AddCount(parts, totals["warning"], "warning", "warnings");
        AddCount(parts, totals["log"], "log", "logs");
        return (string.Join(", ", parts), rows, cut);

        static void AddCount(List<string> list, int n, string one, string many)
        {
            if (n > 0)
            {
                list.Add(n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many));
            }
        }
    }

    private static string FirstLine(string? text)
    {
        string line = (text ?? "").ReplaceLineEndings("\n").Split('\n')[0].Trim();
        return line.Length > ConsoleTextLimit ? line[..(ConsoleTextLimit - 3)] + "..." : line;
    }

    // A text's lines, each trimmed at the end, trailing empty lines dropped.
    private static List<string> Lines(string? text) =>
        [.. (text ?? "").ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').Select(line => line.TrimEnd())];
}
