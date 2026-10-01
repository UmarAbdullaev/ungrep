namespace UnityGraph.EditorLink;

/// <summary>How a compile gate check ended.</summary>
public enum GateResult
{
    /// <summary>No bridge, an old one, a timeout or a lost answer: the gate says nothing (fail open).</summary>
    Unreachable,

    /// <summary>The editor is playing or paused: a refresh could end the session, so nothing was checked.</summary>
    Playing,

    /// <summary>The scripts compile.</summary>
    Clean,

    /// <summary>The scripts have errors; <see cref="GateAnswer.Text"/> lists them.</summary>
    Errors,
}

/// <summary>The gate's verdict; <see cref="Text"/> is the `ug compile` answer when there are errors.</summary>
public sealed record GateAnswer(GateResult Result, string Text = "");

/// <summary>
/// The check behind the Stop hook (SPEC-unity-loop, Compile gate): refresh, wait for Unity's
/// compile within a deadline, and answer whether the scripts have errors.
/// </summary>
public static class CompileGate
{
    // isAlive: replaces the editor process check; tests only.
    public static GateAnswer Check(string projectRoot, TimeSpan deadline, Func<int, bool>? isAlive = null)
    {
        BridgeConnection connection = BridgeClient.Connect(projectRoot, isAlive);
        if (connection.Client == null)
        {
            return new GateAnswer(GateResult.Unreachable);
        }

        using BridgeClient client = connection.Client;
        if (connection.Status!.State is "playing" or "paused")
        {
            return new GateAnswer(GateResult.Playing);
        }

        CompileOutcome outcome = client.Compile(deadline);
        if (outcome.Problem != null || outcome.Result == null)
        {
            return new GateAnswer(GateResult.Unreachable);
        }

        return outcome.Result.Messages.Any(m => m.IsError)
            ? new GateAnswer(GateResult.Errors, CompileText.Format(outcome, warnings: false).TrimEnd('\n'))
            : new GateAnswer(GateResult.Clean);
    }
}
