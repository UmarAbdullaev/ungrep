using System.Globalization;
using System.Text;

namespace UnityGraph.EditorLink;

/// <summary>
/// The `ug compile` answer: a count line, then errors (and warnings when asked) sorted by file,
/// line and column, each once even when several assemblies report it, at most <see cref="MaxLines"/>.
/// </summary>
public static class CompileText
{
    public const int MaxLines = 20;

    /// <summary>The answer for a compile the bridge waited for; <see cref="CompileOutcome.Problem"/> must be null.</summary>
    public static string Format(CompileOutcome outcome, bool warnings)
    {
        IReadOnlyList<CompileMessage> messages = outcome.Result?.Messages ?? [];
        string head = outcome.Changed
            ? "compiled in " + outcome.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s: "
            : "no script changes: ";
        return head + Counts(messages) + "\n" + Lines(messages, warnings);
    }

    /// <summary>
    /// The answer when no bridge answers: the newer of `compile.json` and the project's Editor.log,
    /// headed by the reason and the file's time so an old answer is never taken for a fresh compile.
    /// </summary>
    public static string Fallback(string projectRoot, string reason, string? editorLogPath, bool warnings)
    {
        string head = "Unity not reachable (" + reason + ")";
        string compileJson = Path.Combine(projectRoot, "Library", "unitygraph", "compile.json");
        DateTime? jsonTime = File.Exists(compileJson) ? File.GetLastWriteTime(compileJson) : null;
        DateTime? logTime = editorLogPath != null && File.Exists(editorLogPath) ? File.GetLastWriteTime(editorLogPath) : null;

        if (jsonTime != null && (logTime == null || jsonTime >= logTime))
        {
            string? json = ReadShared(compileJson);
            CompileResult? result = null;
            try
            {
                result = json == null ? null : BridgeJson.Parse<CompileResult>(json);
            }
            catch (BridgeUnavailableException)
            {
            }

            if (result != null)
            {
                return head + "; from compile.json of " + Time(jsonTime.Value) + ": " + Counts(result.Messages) + "\n" + Lines(result.Messages, warnings);
            }
        }

        if (logTime == null)
        {
            return head + "; no Editor.log or compile.json to read\n";
        }

        string? text = ReadShared(editorLogPath!);
        if (text == null)
        {
            return head + "; Editor.log unreadable\n";
        }

        EditorLogAnswer log = EditorLog.Read(text, projectRoot);
        if (!log.IsThisProject)
        {
            return head + "; Editor.log is " + (log.Project == null ? "of no known project" : "another project's (" + log.Project + ")") + "\n";
        }

        if (log.LastCompile == null)
        {
            return head + "; Editor.log of " + Time(logTime.Value) + " shows no finished compile\n";
        }

        return head + "; from Editor.log of " + Time(logTime.Value) + ": " + Counts(log.LastCompile.Messages) + "\n" + Lines(log.LastCompile.Messages, warnings);
    }

    /// <summary>`2 errors, 1 warning`: each message once, warnings only when there are some.</summary>
    public static string Counts(IReadOnlyList<CompileMessage> messages)
    {
        List<CompileMessage> distinct = Distinct(messages);
        int errors = distinct.Count(m => m.IsError);
        int warnings = distinct.Count - errors;
        string text = Plural(errors, "error");
        return warnings > 0 ? text + ", " + Plural(warnings, "warning") : text;
    }

    internal static string Lines(IReadOnlyList<CompileMessage> messages, bool warnings)
    {
        List<CompileMessage> shown = Distinct(messages)
            .Where(m => warnings || m.IsError)
            .OrderBy(m => m.File ?? "", StringComparer.Ordinal)
            .ThenBy(m => m.Line)
            .ThenBy(m => m.Column)
            .ThenBy(m => m.Code, StringComparer.Ordinal)
            .ToList();
        var text = new StringBuilder();
        foreach (CompileMessage m in shown.Take(MaxLines))
        {
            text.Append("  ")
                .Append(m.File == null ? "(no file)" : m.File + ":" + m.Line.ToString(CultureInfo.InvariantCulture) + ":" + m.Column.ToString(CultureInfo.InvariantCulture))
                .Append("  ")
                .Append(m.IsError ? "" : "warning ")
                .Append(m.Code ?? "-")
                .Append("  ")
                .Append(m.Text)
                .Append('\n');
        }

        if (shown.Count > MaxLines)
        {
            string what = warnings ? "messages" : "errors";
            text.Append("  (+").Append((shown.Count - MaxLines).ToString(CultureInfo.InvariantCulture)).Append(" more ").Append(what).Append(")\n");
        }

        return text.ToString();
    }

    private static List<CompileMessage> Distinct(IReadOnlyList<CompileMessage> messages) =>
        messages.DistinctBy(m => (m.File, m.Line, m.Column, m.Severity, m.Code, m.Text)).ToList();

    private static string Plural(int count, string word) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + word + (count == 1 ? "" : "s");

    private static string Time(DateTime local) => local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    // Unity keeps Editor.log open for writing.
    private static string? ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
