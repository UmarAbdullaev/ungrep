using System.Globalization;
using System.Text.RegularExpressions;

namespace Ungrep.EditorLink;

/// <summary>The last finished compile in an Editor.log: whether it failed, and its messages.</summary>
public sealed record LogCompile(bool Failed, IReadOnlyList<CompileMessage> Messages);

/// <summary>What an Editor.log says: the project it belongs to and its last finished compile.</summary>
public sealed record EditorLogAnswer(string? Project, bool IsThisProject, LogCompile? LastCompile);

/// <summary>
/// Reads Unity's Editor.log when no bridge answers. Markers, recorded from BridgeHost batch runs on
/// 2022.3.62f1 and 6000.3.14f1 (the same in both): a compile ends with `*** Tundra build success`
/// or `*** Tundra build failed`; its messages are the `file(line,col): error CSxxxx: text` lines
/// before that marker, repeated after a failure under `## Script Compilation Error for: ...`.
/// </summary>
public static partial class EditorLog
{
    /// <summary>Where the editor writes its log, or null on a platform Unity does not log to a known place.</summary>
    public static string? DefaultPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unity", "Editor", "Editor.log");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "Unity", "Editor.log");
        }

        if (OperatingSystem.IsLinux())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "unity3d", "Editor.log");
        }

        return null;
    }

    public static EditorLogAnswer Read(string text, string projectRoot)
    {
        string[] lines = text.ReplaceLineEndings("\n").Split('\n');
        string? project = ProjectOf(lines);
        bool isThis = project != null && SamePath(project, projectRoot);
        return new EditorLogAnswer(project, isThis, isThis ? LastCompile(lines) : null);
    }

    // `-projectPath <dir>` in the command line (Hub writes `-projectpath`), else the line Unity
    // prints once it opens the project.
    private static string? ProjectOf(string[] lines)
    {
        for (int i = 0; i + 1 < lines.Length; i++)
        {
            if (lines[i].Trim().Equals("-projectPath", StringComparison.OrdinalIgnoreCase))
            {
                return lines[i + 1].Trim();
            }

            const string changed = "Successfully changed project path to: ";
            if (lines[i].StartsWith(changed, StringComparison.Ordinal))
            {
                return lines[i][changed.Length..].Trim();
            }
        }

        return null;
    }

    private static LogCompile? LastCompile(string[] lines)
    {
        LogCompile? last = null;
        var current = new List<CompileMessage>();
        // Right after a failed build Unity repeats its messages; they belong to that build.
        bool trailing = false;
        foreach (string line in lines)
        {
            if (TundraEnd().Match(line) is { Success: true } end)
            {
                bool failed = end.Groups["result"].Value == "failed";
                // A build that succeeded had no errors, whatever stray lines the log holds.
                last = new LogCompile(failed, Distinct(failed ? current : current.Where(m => !m.IsError)));
                current = [];
                trailing = last.Failed;
                continue;
            }

            CompileMessage? message = Message(line);
            if (trailing && last != null)
            {
                if (message != null)
                {
                    last = last with { Messages = Distinct([.. last.Messages, message]) };
                    continue;
                }

                // `## Script Compilation Error for: ...`, `## CmdLine: ...`, `## Output:` and blank lines.
                if (line.StartsWith("##", StringComparison.Ordinal) || line.Trim().Length == 0)
                {
                    continue;
                }

                trailing = false;
            }

            if (message != null)
            {
                current.Add(message);
            }
        }

        return last;
    }

    internal static CompileMessage? Message(string line)
    {
        Match match = MessageLine().Match(line.TrimEnd());
        if (!match.Success)
        {
            return null;
        }

        return new CompileMessage(
            null,
            match.Groups["file"].Value.Replace('\\', '/'),
            int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["column"].Value, CultureInfo.InvariantCulture),
            match.Groups["severity"].Value,
            match.Groups["code"].Value,
            match.Groups["text"].Value);
    }

    private static List<CompileMessage> Distinct(IEnumerable<CompileMessage> messages) =>
        messages.DistinctBy(m => (m.File, m.Line, m.Column, m.Severity, m.Code, m.Text)).ToList();

    private static bool SamePath(string a, string b) =>
        string.Equals(Normal(a), Normal(b), StringComparison.OrdinalIgnoreCase);

    private static string Normal(string path) => path.Replace('\\', '/').TrimEnd('/');

    [GeneratedRegex(@"^\*\*\* Tundra build (?<result>success|failed)\b")]
    private static partial Regex TundraEnd();

    [GeneratedRegex(@"^(?<file>[^(]+)\((?<line>\d+),(?<column>\d+)\): (?<severity>error|warning) (?<code>[A-Za-z]+\d+): (?<text>.*)$")]
    private static partial Regex MessageLine();
}
