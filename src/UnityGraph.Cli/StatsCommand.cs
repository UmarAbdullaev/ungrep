using System.CommandLine;
using System.Globalization;
using UnityGraph.Stats;

namespace UnityGraph.Cli;

/// <summary>
/// `ug stats`: where a project's Claude Code context tokens went, from its local transcripts.
/// Nothing leaves the machine; `--json` output holds aggregates only.
/// </summary>
internal static class StatsCommand
{
    public static Command Create(string? claudeConfigDir)
    {
        var project = new Option<string>("--project")
        {
            Description = "Unity project folder whose transcripts to read. Default: current folder.",
        };
        var since = new Option<string>("--since")
        {
            Description = "Only entries at or after this UTC time (2026-09-01 or 2026-09-01T12:00:00Z).",
        };
        var until = new Option<string>("--until")
        {
            Description = "Only entries at or before this UTC time; a date alone means the end of that day.",
        };
        var json = new Option<bool>("--json") { Description = "Write the report as JSON." };
        var baseline = new Option<string>("--baseline")
        {
            Description = "A saved --json report to compare against.",
        };

        var command = new Command("stats", "Where this project's Claude Code context tokens went. Each model turn's context is charged to the tool calls whose results it read, split evenly.")
        {
            project, since, until, json, baseline,
        };

        command.SetAction(result =>
        {
            TextWriter output = result.InvocationConfiguration.Output;
            TextWriter error = result.InvocationConfiguration.Error;

            if (!TryParseTime(result.GetValue(since), endOfDay: false, out DateTimeOffset? sinceTime)
                || !TryParseTime(result.GetValue(until), endOfDay: true, out DateTimeOffset? untilTime))
            {
                error.WriteLine("ug stats: --since/--until take a UTC date or time, e.g. 2026-09-01 or 2026-09-01T12:00:00Z");
                return 2;
            }

            StatsReport? before = null;
            if (result.GetValue(baseline) is { } baselinePath)
            {
                try
                {
                    before = StatsReport.FromJson(File.ReadAllText(baselinePath));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
                {
                    error.WriteLine($"ug stats: can't read baseline {baselinePath}: {e.Message}");
                    return 1;
                }
            }

            string projectPath = Path.GetFullPath(result.GetValue(project) ?? Directory.GetCurrentDirectory());
            string transcripts = ClaudeProjects.TranscriptDirectory(projectPath, claudeConfigDir);
            if (!Directory.Exists(transcripts))
            {
                error.WriteLine($"ug stats: no Claude Code transcripts for {projectPath} (looked in {transcripts})");
                return 1;
            }

            var window = new TimeWindow(sinceTime, untilTime);
            UsageStats stats = TranscriptAnalyzer.AnalyzeDirectory(transcripts, window);
            string name = Path.GetFileName(projectPath.TrimEnd('\\', '/'));
            var report = StatsReport.From(name, window, stats);

            string text = result.GetValue(json) ? report.ToJson() + "\n" : StatsText.Render(report, before);
            output.Write(text.ReplaceLineEndings());
            return 0;
        });

        return command;
    }

    private static bool TryParseTime(string? value, bool endOfDay, out DateTimeOffset? time)
    {
        time = null;
        if (value is null)
        {
            return true;
        }

        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
        {
            var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            time = endOfDay ? start.AddDays(1).AddTicks(-1) : start;
            return true;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
        {
            time = parsed.ToUniversalTime();
            return true;
        }

        return false;
    }
}
