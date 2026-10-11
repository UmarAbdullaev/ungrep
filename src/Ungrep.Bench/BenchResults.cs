using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ungrep.Bench;

/// <summary>One counted call: its suite label, request and answer bytes, seconds, exit code, and why it was a redo.</summary>
public sealed record StepResult(string Label, int RequestBytes, int AnswerBytes, double Seconds, int Exit, string? Redo = null);

/// <summary>
/// One `claude -p` run of a question on an arm: main-thread turns and context from its transcript,
/// what subagents read, dollars, and how the final answer scored (the answer itself goes to
/// `--answers`: it can quote project code).
/// </summary>
public sealed record AgentRun(
    string SessionId,
    int Turns,
    int ToolCalls,
    long ContextTokens,
    long SubagentTokens,
    double Usd,
    double Seconds,
    bool Correct,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Unwanted,
    bool Redo,
    bool Leaked,
    string? Error = null);

/// <summary>
/// One arm on one question. Script mode fills the step fields; agent mode fills <see cref="Runs"/>.
/// <see cref="Leg"/> names the steps entry when it differs from the arm (shared steps).
/// </summary>
public sealed record ArmResult(
    string Arm,
    bool Ran,
    string? Reason = null,
    string? Leg = null,
    int Found = 0,
    int Of = 0,
    IReadOnlyList<string>? Missing = null,
    IReadOnlyList<StepResult>? Steps = null,
    IReadOnlyList<AgentRun>? Runs = null)
{
    [JsonIgnore]
    public double Calls => Runs is { Count: > 0 } runs ? runs.Average(r => r.ToolCalls) : Steps?.Count ?? 0;

    /// <summary>Script mode: request + answer bytes / 4. Agent mode: mean main-thread context.</summary>
    [JsonIgnore]
    public double Tokens => Runs is { Count: > 0 } runs ? runs.Average(r => r.ContextTokens) : (Steps?.Sum(s => (long)s.RequestBytes + s.AnswerBytes) ?? 0) / 4.0;

    [JsonIgnore]
    public double Seconds => Runs is { Count: > 0 } runs ? runs.Average(r => r.Seconds) : Steps?.Sum(s => s.Seconds) ?? 0;

    /// <summary>1 when every key item was found (agent mode: the share of correct runs).</summary>
    [JsonIgnore]
    public double Correct => Runs is { Count: > 0 } runs ? runs.Count(r => r.Correct) / (double)runs.Count : Ran && Found == Of ? 1 : 0;

    /// <summary>Script mode: calls marked redo. Agent mode: the share of runs that searched again after the arm's own call.</summary>
    [JsonIgnore]
    public double Redo => Runs is { Count: > 0 } runs ? runs.Count(r => r.Redo) / (double)runs.Count : Steps?.Count(s => s.Redo is not null) ?? 0;
}

/// <summary>Every arm's result on one question.</summary>
public sealed record QuestionResult(string Id, string Kind, IReadOnlyList<ArmResult> Arms);

/// <summary>What an arm puts in context besides its calls: skill listing and body, MCP tool names and the schemas it used.</summary>
public sealed record IdleRow(string Arm, int AlwaysBytes, int OnUseBytes, string Detail);

/// <summary>One overhead-budget check (SPEC.md Overhead budget). <see cref="Ok"/> is null when it was not checked.</summary>
public sealed record BudgetResult(string Name, bool? Ok, string Detail);

/// <summary>
/// A suite run as `ug bench` writes it to `bench/results/` and reads it back as a baseline. No
/// project code and no absolute paths: steps are named by their suite labels.
/// </summary>
public sealed record BenchRun(
    int Version,
    string Suite,
    string Mode,
    string Date,
    string? Commit,
    string? Head,
    IReadOnlyList<string> Arms,
    IReadOnlyList<QuestionResult> Questions,
    IReadOnlyList<IdleRow> Idle,
    IReadOnlyList<BudgetResult> Budgets,
    string? Model = null)
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Indented JSON with '\n' line endings on every OS.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, _json).ReplaceLineEndings("\n") + "\n";

    /// <exception cref="FormatException">Not a bench results file, or a version this build can't read.</exception>
    public static BenchRun FromJson(string json)
    {
        BenchRun? run;
        try
        {
            run = JsonSerializer.Deserialize<BenchRun>(json, _json);
        }
        catch (JsonException e)
        {
            throw new FormatException("not a ug bench results file: " + e.Message, e);
        }

        if (run?.Questions is null || run.Suite is null || run.Arms is null)
        {
            throw new FormatException("not a ug bench results file");
        }

        return run.Version == CurrentVersion
            ? run
            : throw new FormatException($"results version {run.Version} is not supported (expected {CurrentVersion})");
    }

    /// <summary>Questions every listed arm ran: the ones totals are taken over.</summary>
    public IReadOnlyList<QuestionResult> Common(IReadOnlyList<string> arms) =>
        Questions.Where(q => arms.All(a => q.Arms.Any(r => r.Arm == a && r.Ran))).ToList();
}
