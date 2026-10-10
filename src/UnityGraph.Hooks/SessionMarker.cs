namespace UnityGraph.Hooks;

/// <summary>
/// Where a handler keeps what it remembers for one Claude Code session:
/// `Library/unitygraph/&lt;prefix&gt;-&lt;session id&gt;`, the id reduced to letters, digits, `-` and `_`
/// (`none` when the payload has no id).
/// </summary>
internal static class SessionMarker
{
    public static string PathOf(string projectRoot, string prefix, string? sessionId)
    {
        string id = new((sessionId ?? "none").Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        return Path.Combine(projectRoot, "Library", "unitygraph", prefix + "-" + (id.Length == 0 ? "none" : id));
    }
}
