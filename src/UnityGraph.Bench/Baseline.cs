using System.Globalization;

namespace UnityGraph.Bench;

/// <summary>A baseline comparison: lines to print, and whether the guarded arm regressed.</summary>
public sealed record Comparison(IReadOnlyList<string> Lines, bool Regressed);

/// <summary>
/// Compares a run with a committed baseline (SPEC-bench, Baseline). Only the unitygraph arm can
/// fail: a question no longer correct, more calls, or tokens up by more than 10% and 50. Other
/// arms' changes are printed; their tools change outside this repo.
/// </summary>
public static class Baseline
{
    public const string GuardedArm = "unitygraph";

    public const double TokenShare = 0.10;

    public const double TokenFloor = 50;

    public static Comparison Compare(BenchRun now, BenchRun before)
    {
        var lines = new List<string>();
        bool regressed = false;
        if (now.Suite != before.Suite || now.Mode != before.Mode)
        {
            lines.Add($"baseline is {before.Suite} {before.Mode}, this run {now.Suite} {now.Mode}: not compared");
            return new Comparison(lines, false);
        }

        if (before.Commit != now.Commit)
        {
            lines.Add($"keys changed commit: baseline {before.Commit}, now {now.Commit}");
        }

        var old = before.Questions.ToDictionary(q => q.Id, StringComparer.Ordinal);
        foreach (QuestionResult question in now.Questions)
        {
            if (!old.TryGetValue(question.Id, out QuestionResult? was))
            {
                lines.Add(question.Id + ": not in the baseline");
                continue;
            }

            foreach (ArmResult arm in question.Arms.Where(a => a.Ran))
            {
                ArmResult? prior = was.Arms.FirstOrDefault(a => a.Arm == arm.Arm);
                bool guarded = arm.Arm == GuardedArm;
                if (prior is null || !prior.Ran)
                {
                    if (guarded)
                    {
                        lines.Add($"{question.Id} {arm.Arm}: did not run in the baseline");
                    }

                    continue;
                }

                var changes = new List<string>();
                bool worse = false;
                if (prior.Correct >= 1 && arm.Correct < 1)
                {
                    changes.Add("correct in the baseline, not now" + (arm.Missing is { Count: > 0 } m ? " (missing " + string.Join(", ", m) + ")" : ""));
                    worse = true;
                }
                else if (prior.Correct < 1 && arm.Correct >= 1)
                {
                    changes.Add("correct now, not in the baseline");
                }

                if (Math.Abs(arm.Calls - prior.Calls) > 0.001)
                {
                    changes.Add(Invariant($"calls {prior.Calls:0.#} -> {arm.Calls:0.#}"));
                    worse |= arm.Calls > prior.Calls;
                }

                double delta = arm.Tokens - prior.Tokens;
                if (Math.Abs(delta) > TokenFloor && Math.Abs(delta) > prior.Tokens * TokenShare)
                {
                    changes.Add(Invariant($"tokens {prior.Tokens:#,0} -> {arm.Tokens:#,0} ({delta / Math.Max(prior.Tokens, 1):+0%;-0%})"));
                    worse |= delta > 0;
                }

                if (changes.Count > 0)
                {
                    bool fails = guarded && worse;
                    regressed |= fails;
                    lines.Add($"{question.Id} {arm.Arm}: {string.Join("; ", changes)}{(fails ? "  REGRESSION" : "")}");
                }
            }
        }

        foreach (string id in old.Keys.Where(id => now.Questions.All(q => q.Id != id)).Order(StringComparer.Ordinal))
        {
            lines.Add(id + ": only in the baseline");
        }

        if (lines.Count == 0)
        {
            lines.Add("no change");
        }
        else if (!regressed)
        {
            lines.Add("no regression");
        }

        return new Comparison(lines, regressed);
    }

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
