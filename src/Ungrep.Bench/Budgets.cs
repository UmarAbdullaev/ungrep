using System.Globalization;
using System.Text;
using System.Text.Json;
using Ungrep.EditorLink;
using Ungrep.Hooks;

namespace Ungrep.Bench;

/// <summary>
/// The overhead budgets a release must hold (SPEC.md Overhead budget; SPEC-bench, Overhead
/// budgets): the `unity` skill's listing line and body, and the compile gate's silence when clean
/// and its cap on errors.
/// </summary>
public static class Budgets
{
    public const int DescriptionTokens = 300;

    public const int BodyTokens = 600;

    public const int GateErrorLines = 20;

    /// <summary>Errors the probe script holds: more than the cap, so the cut is seen.</summary>
    public const int ProbeErrors = 30;

    /// <summary>Checks after the probe is deleted while the editor is reloading, 5 s apart.</summary>
    public const int AfterRetries = 12;

    public const string Description = "skill description";

    public const string Body = "skill body";

    public const string Gate = "compile gate";

    /// <summary>The two skill checks on the plugin's `skills/unity/SKILL.md`.</summary>
    public static IReadOnlyList<BudgetResult> Skill(string pluginRoot)
    {
        string path = Path.Combine(pluginRoot, "skills", "unity", "SKILL.md");
        SkillFile skill;
        try
        {
            skill = SkillFile.Read(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            return [new BudgetResult(Description, false, e.Message), new BudgetResult(Body, false, e.Message)];
        }

        int listing = SkillFile.Tokens(skill.Listing);
        int body = SkillFile.Tokens(skill.Body);
        return
        [
            new BudgetResult(Description, listing <= DescriptionTokens, Invariant($"{listing}/{DescriptionTokens}")),
            new BudgetResult(Body, body <= BodyTokens, Invariant($"{body}/{BodyTokens}")),
        ];
    }

    public static BudgetResult GateNotChecked(string why) => new(Gate, null, "not checked: " + why);

    /// <summary>
    /// The live gate check, through the same dispatcher and handler as the Stop hook: (1) an
    /// untouched script marked edited, Stop prints nothing; (2) a probe script with 30 errors,
    /// Stop blocks with at most 20 error lines; (3) the probe and its `.meta` deleted, the
    /// project compiles clean again. <paramref name="check"/> is the gate's own check (the editor).
    /// </summary>
    public static BudgetResult CheckGate(string projectRoot, string probe, Func<string, TimeSpan, GateAnswer> check, HookDispatcher? dispatcher = null, Action<TimeSpan>? sleep = null)
    {
        dispatcher ??= new HookDispatcher([new CompileGateHandler(check)]);
        string probePath = Path.GetFullPath(probe, projectRoot);
        string relative = Path.GetRelativePath(projectRoot, probePath).Replace('\\', '/');
        if (!relative.StartsWith("Assets/", StringComparison.Ordinal) || !relative.EndsWith(".cs", StringComparison.Ordinal))
        {
            return new BudgetResult(Gate, false, "the probe must be a .cs file under Assets/: " + probe);
        }

        if (File.Exists(probePath))
        {
            return new BudgetResult(Gate, false, "the probe exists already; delete it first: " + relative);
        }

        GateAnswer before = check(projectRoot, CompileGateHandler.Deadline);
        if (before.Result is GateResult.Unreachable or GateResult.Playing)
        {
            return GateNotChecked(before.Result == GateResult.Playing ? "the editor is playing" : "the editor did not answer");
        }

        if (before.Result == GateResult.Errors)
        {
            return GateNotChecked("the project has compile errors already");
        }

        string? script = Directory.EnumerateFiles(Path.Combine(projectRoot, "Assets"), "*.cs", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).FirstOrDefault();
        if (script is null)
        {
            return GateNotChecked("no script under Assets/");
        }

        string session = "ug-bench-" + Guid.NewGuid().ToString("N");
        Hook(dispatcher, projectRoot, session, "PostToolUse", "Edit", script);
        string clean = Hook(dispatcher, projectRoot, session, "Stop");
        if (clean.Length > 0)
        {
            return new BudgetResult(Gate, false, Invariant($"printed {Encoding.UTF8.GetByteCount(clean)} bytes on a clean compile"));
        }

        string blocked;
        try
        {
            File.WriteAllText(probePath, Probe());
            Hook(dispatcher, projectRoot, session, "PostToolUse", "Write", probePath);
            blocked = Hook(dispatcher, projectRoot, session, "Stop");
        }
        finally
        {
            File.Delete(probePath);
            File.Delete(probePath + ".meta");
        }

        // Deleting the probe recompiles and reloads the domain; the bridge does not answer until it is back.
        GateAnswer after = check(projectRoot, CompileGateHandler.Deadline);
        for (int i = 0; i < AfterRetries && after.Result == GateResult.Unreachable; i++)
        {
            (sleep ?? Thread.Sleep)(TimeSpan.FromSeconds(5));
            after = check(projectRoot, CompileGateHandler.Deadline);
        }

        string? reason = Reason(blocked);
        if (reason is null)
        {
            return new BudgetResult(Gate, false, "did not block on " + ProbeErrors + " errors");
        }

        int lines = ErrorLines(reason);
        if (lines > GateErrorLines)
        {
            return new BudgetResult(Gate, false, Invariant($"blocked with {lines} error lines, over {GateErrorLines}"));
        }

        return after.Result == GateResult.Clean
            ? new BudgetResult(Gate, true, Invariant($"0 bytes clean, {lines}/{GateErrorLines} error lines"))
            : new BudgetResult(Gate, false, "the project did not compile clean after the probe was deleted: " + after.Result);
    }

    /// <summary>Lines of a gate reason that name one error (`  file:line:col  CS0000  text`); the head and the "more" line are not errors.</summary>
    internal static int ErrorLines(string reason) =>
        reason.Split('\n').Count(l => l.StartsWith("  ", StringComparison.Ordinal) && !l.StartsWith("  (+", StringComparison.Ordinal));

    internal static string Probe()
    {
        var text = new StringBuilder("// Written by ug bench budgets --gate; deleted when the check ends.\n")
            .Append("internal static class UngrepBenchProbe\n{\n    private static void M()\n    {\n");
        for (int i = 0; i < ProbeErrors; i++)
        {
            text.Append("        int a").Append(i.ToString(CultureInfo.InvariantCulture)).Append(" = \"x\";\n");
        }

        return text.Append("    }\n}\n").ToString();
    }

    private static string Hook(HookDispatcher dispatcher, string projectRoot, string session, string eventName, string? tool = null, string? file = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["session_id"] = session,
            ["cwd"] = projectRoot,
            ["hook_event_name"] = eventName,
        };
        if (tool is not null)
        {
            payload["tool_name"] = tool;
            payload["tool_input"] = new Dictionary<string, string> { ["file_path"] = file! };
        }

        var output = new StringWriter();
        dispatcher.Run(eventName, new StringReader(JsonSerializer.Serialize(payload)), output, projectRoot);
        return output.ToString();
    }

    private static string? Reason(string hookOutput)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(hookOutput);
            return doc.RootElement.TryGetProperty("decision", out JsonElement d) && d.GetString() == "block"
                && doc.RootElement.TryGetProperty("reason", out JsonElement r) ? r.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
