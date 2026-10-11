using System.Globalization;
using System.Text;

namespace Ungrep.Bench;

/// <summary>
/// The table `ug bench run` prints (SPEC-bench, Results): one cell per question and arm, totals
/// over the questions every arm ran, idle cost, budgets and the baseline comparison.
/// </summary>
public static class BenchText
{
    public static string Render(BenchRun run, Comparison? comparison = null, string? baselinePath = null, string? resultsPath = null)
    {
        var text = new StringBuilder();
        text.Append("bench ").Append(run.Suite).Append("  ").Append(run.Mode);
        if (run.Model is not null)
        {
            text.Append("  model ").Append(run.Model);
        }

        text.Append("  commit ").Append(run.Commit ?? "-").Append(" (HEAD ").Append(run.Head ?? "?").Append(")\n");
        if (run.Commit is not null && run.Head is not null && !run.Head.StartsWith(run.Commit, StringComparison.Ordinal) && !run.Commit.StartsWith(run.Head, StringComparison.Ordinal))
        {
            text.Append("warning: the keys hold for ").Append(run.Commit).Append("; the project is at ").Append(run.Head).Append('\n');
        }

        AppendTable(text, run);
        AppendNotRun(text, run);
        AppendTotals(text, run);
        AppendIdle(text, run);

        if (run.Budgets.Count > 0)
        {
            text.Append("budgets  ").Append(string.Join("  ", run.Budgets.Select(b => $"{b.Name} {b.Detail}{(b.Ok switch { true => " ok", false => " BROKEN", null => "" })}"))).Append('\n');
        }

        if (comparison is not null)
        {
            text.Append("baseline ").Append(baselinePath).Append(':');
            text.Append(comparison.Lines.Count == 1 ? " " + comparison.Lines[0] + "\n" : "\n" + string.Concat(comparison.Lines.Select(l => "  " + l + "\n")));
        }

        if (resultsPath is not null)
        {
            text.Append("results  ").Append(resultsPath).Append('\n');
        }

        return text.ToString();
    }

    private static void AppendTable(StringBuilder text, BenchRun run)
    {
        var rows = new List<string[]> { new[] { "question" }.Concat(run.Arms).ToArray() };
        foreach (QuestionResult q in run.Questions)
        {
            rows.Add(new[] { q.Id + " " + q.Kind }.Concat(run.Arms.Select(a => Cell(q.Arms.FirstOrDefault(r => r.Arm == a)))).ToArray());
        }

        AppendColumns(text, rows);
    }

    private static string Cell(ArmResult? arm)
    {
        if (arm is null || !arm.Ran)
        {
            return "-";
        }

        string cell;
        if (arm.Runs is { Count: > 0 } runs)
        {
            int correct = runs.Count(r => r.Correct);
            cell = Invariant($"{arm.Calls:0.#} / {arm.Tokens:#,0} / {arm.Seconds:0} {(correct == runs.Count ? "ok" : $"{correct}/{runs.Count} right")}");
            int redo = runs.Count(r => r.Redo);
            int leaked = runs.Count(r => r.Leaked);
            cell += (redo > 0 ? Invariant($" redo {redo}") : "") + (leaked > 0 ? Invariant($" leaked {leaked}") : "");
        }
        else
        {
            cell = Invariant($"{arm.Calls:0} / {arm.Tokens:#,0} / {arm.Seconds:0.0} {(arm.Found == arm.Of ? "ok" : $"{arm.Found}/{arm.Of}")}");
            cell += arm.Redo > 0 ? Invariant($" redo {arm.Redo:0}") : "";
        }

        return arm.Leg is null ? cell : cell + " *";
    }

    private static void AppendNotRun(StringBuilder text, BenchRun run)
    {
        var groups = run.Questions
            .SelectMany(q => q.Arms.Where(a => !a.Ran).Select(a => (q.Id, a.Arm, Reason: a.Reason ?? "not run")))
            .GroupBy(x => (x.Arm, x.Reason))
            .OrderBy(g => run.Arms.ToList().IndexOf(g.Key.Arm))
            .ThenBy(g => g.Key.Reason, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            text.Append("- ").Append(group.Key.Arm).Append(' ').Append(string.Join(",", group.Select(g => g.Id))).Append(": ").Append(group.Key.Reason).Append('\n');
        }

        if (run.Questions.Any(q => q.Arms.Any(a => a.Leg is not null)))
        {
            text.Append("* shared steps, run once: ")
                .Append(string.Join(", ", run.Questions.SelectMany(q => q.Arms).Where(a => a.Leg is not null).Select(a => a.Leg!).Distinct().Order(StringComparer.Ordinal)))
                .Append('\n');
        }
    }

    private static void AppendTotals(StringBuilder text, BenchRun run)
    {
        // An arm that ran nothing (its index missing, say) would leave no question in common.
        IReadOnlyList<string> ran = run.Arms.Where(arm => run.Questions.Any(q => q.Arms.Any(a => a.Arm == arm && a.Ran))).ToList();
        IReadOnlyList<QuestionResult> common = run.Common(ran);
        text.Append(Invariant($"totals over {common.Count} questions every arm ran"))
            .Append(ran.Count < run.Arms.Count ? " (" + string.Join(", ", run.Arms.Except(ran)) + " ran none)" : "")
            .Append('\n');
        if (common.Count == 0)
        {
            return;
        }

        bool agent = run.Mode == "agent";
        var rows = new List<string[]>
        {
            agent
                ? ["", "calls", "context", "subagents", "usd", "seconds", "correct", "redo", "leaked"]
                : ["", "calls", "tokens", "seconds", "correct", "redo"],
        };
        var totals = new Dictionary<string, (double Calls, double Tokens)>(StringComparer.Ordinal);
        foreach (string armName in ran)
        {
            List<ArmResult> arms = common.Select(q => q.Arms.First(a => a.Arm == armName)).ToList();
            double calls = arms.Sum(a => a.Calls);
            double tokens = arms.Sum(a => a.Tokens);
            totals[armName] = (calls, tokens);
            if (agent)
            {
                List<AgentRun> runs = arms.SelectMany(a => a.Runs ?? []).ToList();
                rows.Add(
                [
                    armName,
                    Invariant($"{calls:0.#}"),
                    Invariant($"{tokens:#,0}"),
                    Invariant($"{arms.Sum(a => a.Runs is { Count: > 0 } r ? r.Average(x => x.SubagentTokens) : 0):#,0}"),
                    Invariant($"{runs.Sum(r => r.Usd):0.00}"),
                    Invariant($"{arms.Sum(a => a.Seconds):0}"),
                    Invariant($"{runs.Count(r => r.Correct)}/{runs.Count}"),
                    Invariant($"{runs.Count(r => r.Redo)}"),
                    Invariant($"{runs.Count(r => r.Leaked)}"),
                ]);
            }
            else
            {
                rows.Add(
                [
                    armName,
                    Invariant($"{calls:0}"),
                    Invariant($"{tokens:#,0}"),
                    Invariant($"{arms.Sum(a => a.Seconds):0.0}"),
                    Invariant($"{arms.Count(a => a.Correct >= 1)}/{arms.Count}"),
                    Invariant($"{arms.Sum(a => a.Redo):0}"),
                ]);
            }
        }

        AppendColumns(text, rows, rightAlign: true);

        // Each arm against the first (the grep baseline), on the same questions.
        string first = ran[0];
        foreach (string armName in ran.Skip(1))
        {
            text.Append(armName).Append(" vs ").Append(first).Append(": calls ").Append(Percent(totals[armName].Calls, totals[first].Calls))
                .Append(", ").Append(agent ? "context " : "tokens ").Append(Percent(totals[armName].Tokens, totals[first].Tokens)).Append('\n');
        }
    }

    private static void AppendIdle(StringBuilder text, BenchRun run)
    {
        if (run.Idle.Count == 0)
        {
            return;
        }

        text.Append("idle tokens (always + on use)  ")
            .Append(string.Join("  ", run.Idle.Select(i => Invariant($"{i.Arm} {(i.AlwaysBytes + 3) / 4:#,0} + {(i.OnUseBytes + 3) / 4:#,0}"))))
            .Append('\n');
    }

    private static string Percent(double value, double reference) =>
        reference <= 0 ? "-" : Invariant($"{(value - reference) / reference:+0%;-0%;0%}");

    private static void AppendColumns(StringBuilder text, List<string[]> rows, bool rightAlign = false)
    {
        int columns = rows.Max(r => r.Length);
        var widths = Enumerable.Range(0, columns).Select(c => rows.Max(r => c < r.Length ? r[c].Length : 0)).ToArray();
        foreach (string[] row in rows)
        {
            var line = new StringBuilder();
            for (int c = 0; c < row.Length; c++)
            {
                bool last = c == row.Length - 1;
                if (c > 0)
                {
                    line.Append("  ");
                }

                line.Append(rightAlign && c > 0 ? row[c].PadLeft(widths[c]) : last ? row[c] : row[c].PadRight(widths[c]));
            }

            text.Append(line.ToString().TrimEnd()).Append('\n');
        }
    }

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
