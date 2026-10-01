using System.CommandLine;
using System.Globalization;
using UnityGraph.Core;
using UnityGraph.EditorLink;
using UnityGraph.Query;

namespace UnityGraph.Cli;

/// <summary>
/// `ug console` (SPEC-unity-loop, Commands): the editor console as a short digest, errors first,
/// repeats folded. The folding and the row format are shared with `ug log`.
/// </summary>
internal static partial class EditorCommands
{
    private const int ConsoleTextLimit = 160;

    private static IEnumerable<Command> ConsoleCommands()
    {
        var level = new Option<string>("--level") { Description = "Lowest level shown: error, warning or log. Default: warning (errors and warnings).", DefaultValueFactory = _ => "warning" };
        var count = new Option<int>("--count") { Description = "How many of the newest entries to look at, 1 to 500. Default: 200.", DefaultValueFactory = _ => 200 };
        var after = new Option<long?>("--after") { Description = "Only entries newer than this console id (the #id a previous answer ended with)." };
        Option<int?> budget = BudgetOption();
        yield return Command(
            "console",
            "The editor console, errors first, repeats folded (x12), within the answer budget; ends with a `more` line when cut.",
            [level, count, after, budget],
            (root, config, result) => ConsoleDigest(root, config, result.GetValue(level)!, result.GetValue(count), result.GetValue(after), result.GetValue(budget)));
    }

    private static Option<int?> BudgetOption() =>
        new("--budget") { Description = $"Answer budget in tokens, {Answer.MinBudget} to {Answer.MaxBudget}. Default: answerBudget in .claude/unitygraph.json." };

    public static (int ExitCode, string Text) ConsoleDigest(string root, UnityGraphConfig config, string level, int count, long? after, int? budget)
    {
        if (!config.IsEnabled("unity-loop"))
        {
            return LoopOff();
        }

        if (level is not ("error" or "warning" or "log"))
        {
            return (1, "--level must be error, warning or log\n");
        }

        count = Math.Clamp(count, 1, 500);
        BridgeConnection connection = BridgeClient.Connect(root, ProcessCheck);
        if (connection.Client == null)
        {
            return Unreachable(root, "console", connection.Problem);
        }

        using BridgeClient client = connection.Client;
        ConsolePage page;
        try
        {
            page = client.Console(after ?? 0, count, level switch { "error" => "error", "warning" => "error,warning", _ => null });
        }
        catch (BridgeUnavailableException e)
        {
            return NoAnswer(e);
        }

        (string counts, List<string> rows) = FoldConsole(page.Entries, logRows: true);
        var answer = new Answer(BudgetOf(config, budget));
        string last = "(last #" + page.Last.ToString(CultureInfo.InvariantCulture) + ")";
        answer.Add(counts.Length == 0
            ? "console  nothing at " + level + (level == "error" ? " level" : " level or worse") + "  " + last
            : "console  " + counts + "  " + last);
        answer.AddRange(rows);
        return (0, answer.Finish("ug console --level " + level + " --count " + count.ToString(CultureInfo.InvariantCulture)
            + (after is { } id ? " --after " + id.ToString(CultureInfo.InvariantCulture) : "")));
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
    /// </summary>
    internal static (string Counts, List<string> Rows) FoldConsole(IEnumerable<ConsoleEntry> entries, bool logRows)
    {
        var groups = new Dictionary<(string Type, string Text, string? Frame), (int Count, long Last)>();
        var totals = new Dictionary<string, int>(StringComparer.Ordinal) { ["error"] = 0, ["warning"] = 0, ["log"] = 0 };
        foreach (ConsoleEntry entry in entries)
        {
            string type = entry.Type is "warning" or "log" ? entry.Type : "error";
            (string Type, string Text, string? Frame) key = (type, FirstLine(entry.Text), entry.Frame);
            (int count, long last) = groups.GetValueOrDefault(key);
            groups[key] = (count + entry.Count, Math.Max(last, entry.Id));
            totals[type] += entry.Count;
        }

        List<string> rows = groups
            .Where(g => logRows || g.Key.Type != "log")
            .OrderBy(g => g.Key.Type == "error" ? 0 : g.Key.Type == "warning" ? 1 : 2)
            .ThenByDescending(g => g.Value.Last)
            .Select(g => "  " + g.Key.Type + (g.Value.Count > 1 ? " x" + g.Value.Count.ToString(CultureInfo.InvariantCulture) : "")
                + "  " + g.Key.Text + (g.Key.Frame == null ? "" : "  " + g.Key.Frame))
            .ToList();
        var parts = new List<string>();
        AddCount(parts, totals["error"], "error", "errors");
        AddCount(parts, totals["warning"], "warning", "warnings");
        AddCount(parts, totals["log"], "log", "logs");
        return (string.Join(", ", parts), rows);

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
}
