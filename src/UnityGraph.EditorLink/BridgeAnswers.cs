using System.Text.Json;

namespace UnityGraph.EditorLink;

/// <summary>`Library/unitygraph/bridge.json`: how to reach the bridge of a running editor.</summary>
public sealed record BridgeInfo(string Bridge, int Port, string Token, int Pid, string Unity);

/// <summary>`GET /status`.</summary>
public sealed record BridgeStatus(string State, string Unity, string Bridge, int Compile, int Errors, int Warnings, long Console);

/// <summary>`GET /refresh`: whether a compile started, and the compile counter before it.</summary>
public sealed record RefreshAnswer(bool Compiling, int Compile);

/// <summary>One compiler message; <see cref="File"/> is project-relative with `/`.</summary>
public sealed record CompileMessage(string? Assembly, string? File, int Line, int Column, string Severity, string? Code, string Text)
{
    public bool IsError => Severity == "error";
}

/// <summary>`GET /compile` and `compile.json`: the last compile (counter 0 when none was seen).</summary>
public sealed record CompileResult(int Compile, DateTimeOffset? Started, DateTimeOffset? Finished, IReadOnlyList<CompileMessage> Messages);

/// <summary>One console entry; <see cref="Count"/> counts consecutive repeats; <see cref="Stack"/> is Unity's whole stack trace (bridge 0.9.0).</summary>
public sealed record ConsoleEntry(long Id, string Type, string Text, string? Frame, int Count, string? Stack = null);

/// <summary>`GET /console`: entries after the asked id, the newest id, and (bridge 0.9.0) the oldest id held.</summary>
public sealed record ConsolePage(long Last, IReadOnlyList<ConsoleEntry> Entries, long Oldest = 0);

/// <summary>A pause or resume in a play session, <see cref="At"/> seconds after it started.</summary>
public sealed record PlayEvent(double At, string What);

/// <summary>The last play session: console ids around it (<see cref="Until"/> is null while playing).</summary>
public sealed record PlaySession(DateTimeOffset Start, DateTimeOffset? End, long After, long? Until, IReadOnlyList<PlayEvent> Events);

/// <summary>`GET /playlog`: the last play session, or none since the editor opened.</summary>
public sealed record PlayLogPage(PlaySession? Session);

/// <summary>A failed test in a run's answer; <see cref="Stack"/> is its whole stack trace (bridge 0.9.0).</summary>
public sealed record TestFailure(string Name, string? Message, string? Frame, string? Stack = null);

/// <summary>
/// `GET /tests/result`: `running`, the finished run's totals, or (bridge 0.14.0) `stopped` with the
/// <see cref="Reason"/> a canceled or failed run ended without results.
/// </summary>
public sealed record TestRunResult(string State, int Passed, int Failed, int Skipped, int Inconclusive, double Seconds, IReadOnlyList<TestFailure>? Failures, string? Reason = null)
{
    public bool IsRunning => State == "running";

    public bool IsStopped => State == "stopped";
}

/// <summary>
/// An editor-ops answer (SPEC-editor-ops, Endpoints): lines to print as they are, or a refusal;
/// <see cref="Wait"/> names the editor state (`playing`, `idle`) to wait for before printing.
/// A documentation answer (`ug api`, `ug docs`) has <see cref="Local"/>, the offline page's file,
/// or an <see cref="Error"/> with the <see cref="Online"/> addresses that might have the page.
/// </summary>
public sealed record OpAnswer(IReadOnlyList<string>? Lines, string? Error, string? Wait, string? Local = null, IReadOnlyList<string>? Online = null);

/// <summary>`GET /eval/refs`: the files the editor's loaded assemblies came from, its script defines and C# version.</summary>
public sealed record EvalRefs(IReadOnlyList<string> Refs, IReadOnlyList<string> Defines, string Lang);

/// <summary>How a wait for an editor state ended; <see cref="Problem"/> is null when it was reached.</summary>
public sealed record WaitOutcome(string? Problem, TimeSpan Elapsed)
{
    public bool Reached => Problem == null;
}

internal static class BridgeJson
{
    private static readonly JsonSerializerOptions _options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Parses a bridge answer; a malformed one is a <see cref="BridgeUnavailableException"/>.</summary>
    public static T Parse<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, _options) ?? throw new BridgeUnavailableException("empty answer from the bridge");
        }
        catch (JsonException e)
        {
            throw new BridgeUnavailableException("unreadable answer from the bridge: " + e.Message);
        }
    }
}

/// <summary>The bridge could not answer; <see cref="Exception.Message"/> is the reason to show.</summary>
public sealed class BridgeUnavailableException(string reason) : Exception(reason);
