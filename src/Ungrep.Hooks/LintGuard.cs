using System.Text.RegularExpressions;
using Ungrep.Core;

namespace Ungrep.Hooks;

/// <summary>
/// Case 5 (`lint`): added lines of a script that match the user's `guards.lint.patterns`. No
/// graph; silent until the user adds patterns.
/// </summary>
internal sealed class LintGuard : IGuard
{
    private const int MaxLine = 80;

    public string Case => "lint";

    public IEnumerable<GuardNote> Check(GuardContext context)
    {
        IReadOnlyList<LintPattern> patterns = context.Config.Guards.LintPatterns;
        if (patterns.Count == 0 || EditCall.From(context.Input) is not { } call || context.OwnScript(call.FilePath) is not { } path
            || !path.StartsWith("Assets/", StringComparison.Ordinal))
        {
            yield break;
        }

        var hits = new List<(string Line, string Message)>();
        foreach (string line in AddedLines(call, context.FullPath(path)))
        {
            if (patterns.FirstOrDefault(p => Matches(p.Pattern, line)) is { } pattern && !hits.Any(h => h.Line == line))
            {
                hits.Add((line, pattern.Message));
            }
        }

        if (hits.Count == 0)
        {
            yield break;
        }

        // At most three hits; with more, two and a count, so a deny's extra line keeps it within five.
        int shown = hits.Count <= 3 ? hits.Count : 2;
        int width = hits.Take(shown).Max(h => Clip(h.Line).Length);
        var text = new List<string> { $"ungrep lint: {path}, added lines match your guards.lint.patterns:" };
        text.AddRange(hits.Take(shown).Select(h => ("  " + Clip(h.Line).PadRight(width) + "   " + h.Message).TrimEnd()));
        if (hits.Count > shown)
        {
            text.Add($"  (+{hits.Count - shown} more)");
        }

        yield return new GuardNote(Case, path + " " + string.Join(" ", hits.Select(h => h.Line)), string.Join("\n", text));
    }

    // Trimmed lines of the new text that were not already a whole line of the old text; comments skipped.
    private static IEnumerable<string> AddedLines(EditCall call, string fullPath)
    {
        IEnumerable<(string Old, string New)> pairs = call.Content is { } content
            ? [(File.Exists(fullPath) ? EditCall.Lf(File.ReadAllText(fullPath)) : "", content)]
            : call.Edits.Select(e => (e.Old, e.New));
        foreach ((string old, string replacement) in pairs)
        {
            var before = new HashSet<string>(old.Split('\n').Select(l => l.Trim()), StringComparer.Ordinal);
            foreach (string line in replacement.Split('\n').Select(l => l.Trim()))
            {
                if (line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal) && !before.Contains(line))
                {
                    yield return line;
                }
            }
        }
    }

    private static bool Matches(Regex pattern, string line)
    {
        try
        {
            return pattern.IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string Clip(string line) => line.Length <= MaxLine ? line : line[..(MaxLine - 3)] + "...";
}
