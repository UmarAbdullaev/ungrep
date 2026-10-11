using System.Text.Json;

namespace UnityGraph.Stats.Tests;

/// <summary>
/// Worker delegations (SPEC-worker-agent, Measuring) counted from a transcript in the shapes Claude
/// Code records: a foreground result with `totalToolUseCount`, a background run's
/// `&lt;task-notification&gt;` (notified twice after a resume), a hand-back report, a subagent's own
/// entries, another agent type, and a run with no result yet. The workers' own transcripts sit
/// beside the session, under `delegations/subagents/`.
/// </summary>
public sealed class WorkerDelegationsTests
{
    private static string WorkerDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "worker");

    private static SessionStats Analyze(TimeWindow? window = null) =>
        TranscriptAnalyzer.AnalyzeSession(
            "delegations", File.ReadLines(Path.Combine(WorkerDir, "delegations.jsonl")), window ?? TimeWindow.All, WorkerTranscript)
        ?? throw new InvalidOperationException("no turns");

    private static IEnumerable<string>? WorkerTranscript(string agentId)
    {
        string path = Path.Combine(WorkerDir, "delegations", "subagents", "agent-" + agentId + ".jsonl");
        return File.Exists(path) ? File.ReadLines(path) : null;
    }

    [Fact]
    public void Counts_worker_delegations_short_ones_and_verdicts_over_budget()
    {
        WorkerTotals worker = Analyze().Worker;

        // toolu_w1..w5; the Explore agent and the subagent's own Agent call are not counted.
        Assert.Equal(5, worker.Delegations);
        Assert.Equal(1, worker.Short);
        Assert.Equal(1, worker.OverBudget);
        Assert.Equal(1, worker.NoResult);
        Assert.Equal(4, worker.Finished);
        // 400, 80, 3300 (the larger of the two notifications) and the 1000-byte hand-back, bytes / 4 rounded up.
        Assert.Equal(100 + 20 + 825 + 250, worker.VerdictTokens);
    }

    [Fact]
    public void Sums_what_the_workers_read_from_their_own_transcripts()
    {
        WorkerTotals worker = Analyze().Worker;

        // w1 10k + 12k + 14k (a turn written on two lines counts once); w3 20k + 22k and 30k after its
        // resume; w4 15k + 16k; w5 18k while still running. w2's transcript is gone; the Explore
        // agent's 50k is not the worker's.
        Assert.Equal(36_000 + 72_000 + 31_000 + 18_000, worker.ContextTokens);
    }

    [Fact]
    public void The_verdict_is_the_workers_message_not_the_frame_around_it()
    {
        // Claude Code 2.1.295 hands a foreground result back framed and indented; toolUseResult
        // keeps the message as the worker wrote it.
        const string verdict = "verdict: done\nleft: nothing";
        const string framed = "[Subagent hand-back] The text below is the final report of a subagent. The report follows:\n  verdict: done\n  left: nothing";
        string[] lines =
        [
            JsonSerializer.Serialize(new
            {
                type = "assistant",
                message = new
                {
                    id = "m1",
                    role = "assistant",
                    content = new object[] { new { type = "tool_use", id = "toolu_f", name = "Agent", input = new { subagent_type = "unitygraph:unity-worker", prompt = "Chore" } } },
                    usage = new { input_tokens = 0, cache_read_input_tokens = 30000, cache_creation_input_tokens = 0, output_tokens = 10 },
                },
                timestamp = "2026-10-11T08:00:00.000Z",
            }),
            JsonSerializer.Serialize(new
            {
                type = "user",
                message = new { role = "user", content = new object[] { new { tool_use_id = "toolu_f", type = "tool_result", content = new object[] { new { type = "text", text = framed } } } } },
                toolUseResult = new { status = "completed", agentId = "af", totalToolUseCount = 5, content = new object[] { new { type = "text", text = verdict } } },
                timestamp = "2026-10-11T08:00:05.000Z",
            }),
        ];

        WorkerTotals worker = TranscriptAnalyzer.AnalyzeSession("framed", lines, TimeWindow.All)!.Worker;

        Assert.Equal((verdict.Length + 3) / 4, worker.VerdictTokens);
    }

    [Fact]
    public void Worker_context_is_summed_like_the_main_threads()
    {
        long context = TranscriptAnalyzer.WorkerContext(File.ReadLines(Path.Combine(WorkerDir, "delegations", "subagents", "agent-atoolu_w1.jsonl")));

        Assert.Equal(36_000, context);
    }

    [Fact]
    public void Without_worker_transcripts_the_worker_context_is_zero()
    {
        SessionStats session = TranscriptAnalyzer.AnalyzeSession("delegations", File.ReadLines(Path.Combine(WorkerDir, "delegations.jsonl")), TimeWindow.All)!;

        Assert.Equal(0, session.Worker.ContextTokens);
        Assert.Equal(5, session.Worker.Delegations);
    }

    [Fact]
    public void Delegations_still_count_as_subagent_calls()
    {
        SessionStats session = Analyze();

        Assert.Equal(6, session.Categories["subagent"].Calls);
    }

    [Fact]
    public void Only_delegations_started_inside_the_window_count()
    {
        var window = new TimeWindow(Until: new DateTimeOffset(2026, 10, 11, 8, 0, 4, TimeSpan.Zero));

        SessionStats session = Analyze(window);

        Assert.Equal(new WorkerTotals(2, 1, 0, 0, 120, 36_000), session.Worker);
    }

    [Fact]
    public void Directory_totals_sum_the_sessions_workers()
    {
        UsageStats stats = TranscriptAnalyzer.AnalyzeDirectory(WorkerDir, TimeWindow.All);

        Assert.Equal(Analyze().Worker, stats.Worker);
    }

    [Fact]
    public void A_session_without_delegations_has_none()
    {
        SessionStats session = TranscriptAnalyzer.AnalyzeSession(
            "attribution", File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "attribution.jsonl")), TimeWindow.All)!;

        Assert.Equal(WorkerTotals.None, session.Worker);
    }

    [Theory]
    [InlineData("Agent", "unity-worker", true)]
    [InlineData("Agent", "unitygraph:unity-worker", true)]
    [InlineData("Task", "unitygraph:unity-worker", true)]
    [InlineData("Agent", "Explore", false)]
    [InlineData("Agent", "my-unity-worker", false)]
    [InlineData("Agent", "unity-worker-2", false)]
    [InlineData("Bash", "unity-worker", false)]
    public void Recognises_the_worker_by_its_agent_type(string tool, string type, bool expected)
    {
        using JsonDocument input = JsonDocument.Parse(JsonSerializer.Serialize(new { subagent_type = type, prompt = "x" }));

        Assert.Equal(expected, WorkerDelegations.IsWorker(tool, input.RootElement));
    }

    [Fact]
    public void An_agent_call_without_a_type_is_not_the_worker()
    {
        using JsonDocument input = JsonDocument.Parse("""{"prompt":"x"}""");

        Assert.False(WorkerDelegations.IsWorker("Agent", input.RootElement));
    }

    [Fact]
    public void Text_report_with_worker_delegations()
    {
        StatsReport report = StatsReport.From("Game", TimeWindow.All, TranscriptAnalyzer.AnalyzeDirectory(WorkerDir, TimeWindow.All));

        Golden.Match(StatsText.Render(report));
    }

    [Fact]
    public void Text_report_against_a_baseline_written_before_workers_were_counted()
    {
        StatsReport report = StatsReport.From("Game", TimeWindow.All, TranscriptAnalyzer.AnalyzeDirectory(WorkerDir, TimeWindow.All));
        StatsReport old = StatsReport.FromJson("""{"version":1,"project":"Game","sessions":1,"turns":2,"contextTokens":40000,"compactions":0,"firstTurnMedian":20000,"categories":[]}""");

        string text = StatsText.Render(report, old);

        Assert.Null(old.Worker);
        Assert.Contains("\nunity-worker 5 delegations | 1 short (under 3 tool calls) | worker context 157.0k | verdict avg 298 tokens, 1 over 800 | 1 without a result\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Baseline_delegations_show_even_when_there_are_none_now()
    {
        StatsReport now = StatsReport.From("Game", TimeWindow.All, TranscriptAnalyzer.AnalyzeDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures"), TimeWindow.All));
        StatsReport before = StatsReport.From("Game", TimeWindow.All, TranscriptAnalyzer.AnalyzeDirectory(WorkerDir, TimeWindow.All));

        string text = StatsText.Render(now, before);

        Assert.Contains("\nunity-worker 0 delegations (-5) | 0 short (under 3 tool calls) | worker context 0 (-100.0%)\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Worker_totals_round_trip_through_json()
    {
        StatsReport report = StatsReport.From("Game", TimeWindow.All, TranscriptAnalyzer.AnalyzeDirectory(WorkerDir, TimeWindow.All));

        StatsReport back = StatsReport.FromJson(report.ToJson());

        Assert.Equal(report.Worker, back.Worker);
        Assert.Contains("\"worker\": {\n    \"delegations\": 5,", report.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain("finished", report.ToJson(), StringComparison.OrdinalIgnoreCase);
    }
}
