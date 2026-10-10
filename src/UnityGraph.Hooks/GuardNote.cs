using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using UnityGraph.Core;

namespace UnityGraph.Hooks;

/// <summary>
/// What a guard says about a tool call: its case, the subject it is about (the once-per-session
/// filter's key, with the case) and the text, at most five lines, the first `unitygraph: ...`.
/// </summary>
internal sealed record GuardNote(string Case, string Subject, string Text);

/// <summary>One guard case (SPEC-guards, Cases). Returns notes only when it fires.</summary>
internal interface IGuard
{
    /// <summary>The case's name in `guards` (`field-removed`, `meta`, ...).</summary>
    string Case { get; }

    IEnumerable<GuardNote> Check(GuardContext context);
}

/// <summary>Turns notes into the PreToolUse output (SPEC-guards, Mechanism) and words counts.</summary>
internal static class GuardOutput
{
    private static readonly JsonSerializerOptions _json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Warn notes as `additionalContext`; any deny note makes it a deny with every note as the
    /// reason. Never `allow`: that would skip the user's own permission prompts.
    /// </summary>
    public static string? Render(IReadOnlyList<(GuardNote Note, GuardMode Mode)> notes)
    {
        if (notes.Count == 0)
        {
            return null;
        }

        string text = string.Join("\n", notes.Select(n => n.Mode == GuardMode.Deny ? n.Note.Text + "\n" + Blocked(n.Note.Case) : n.Note.Text));
        object output = notes.Any(n => n.Mode == GuardMode.Deny)
            ? new { hookSpecificOutput = new { hookEventName = "PreToolUse", permissionDecision = "deny", permissionDecisionReason = text } }
            : new { hookSpecificOutput = new { hookEventName = "PreToolUse", additionalContext = text } };
        return JsonSerializer.Serialize(output, _json);
    }

    /// <summary>The line a deny adds: which setting blocked the call.</summary>
    public static string Blocked(string guardCase) =>
        $"  Blocked by guards.{(guardCase == "lint" ? "lint.mode" : guardCase)} = \"deny\" in .claude/unitygraph.json: ask the user before doing this anyway.";

    /// <summary>`1 asset`, `3 assets`.</summary>
    public static string Count(int count, string noun) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + noun + (count == 1 || noun == "other" ? "" : "s");

    /// <summary>`3 assets (1 scene, 2 prefabs)`: asset files counted by kind.</summary>
    public static string Assets(IReadOnlyCollection<string> files)
    {
        int scenes = files.Count(f => f.EndsWith(".unity", StringComparison.OrdinalIgnoreCase));
        int prefabs = files.Count(f => f.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase));
        int other = files.Count - scenes - prefabs;
        var kinds = new List<string>();
        if (scenes > 0)
        {
            kinds.Add(Count(scenes, "scene"));
        }

        if (prefabs > 0)
        {
            kinds.Add(Count(prefabs, "prefab"));
        }

        if (other > 0)
        {
            kinds.Add(Count(other, "other"));
        }

        return Count(files.Count, "asset") + " (" + string.Join(", ", kinds) + ")";
    }

    /// <summary>`Assets/Scripts/Old.cs` → `Old.cs`: the name `ug refs` takes for a file.</summary>
    public static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];
}
