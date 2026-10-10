using System.Text.Json;
using System.Text.RegularExpressions;

namespace UnityGraph.Hooks;

/// <summary>One replacement of an `Edit` or `MultiEdit` call, line endings as `\n`.</summary>
internal sealed record Replacement(string Old, string New, bool All);

/// <summary>A replacement and the line ranges (1-based, inclusive) it covers in the file as it is now.</summary>
internal sealed record LocatedReplacement(Replacement Edit, IReadOnlyList<(int Start, int End)> Lines);

/// <summary>
/// An `Edit`, `MultiEdit`, `Write` or `NotebookEdit` call: the file, and either the replacements or
/// the whole new content (`Write`). Text is compared with `\n` line endings, as the tools match it.
/// </summary>
internal sealed class EditCall
{
    private EditCall(string filePath, IReadOnlyList<Replacement> edits, string? content)
    {
        FilePath = filePath;
        Edits = edits;
        Content = content;
    }

    public string FilePath { get; }

    public IReadOnlyList<Replacement> Edits { get; }

    /// <summary>The new file content of a `Write`; null for the other tools.</summary>
    public string? Content { get; }

    /// <summary>The call, or null when its tool is not an edit tool or a field it needs is missing.</summary>
    public static EditCall? From(HookInput input)
    {
        JsonElement args = input.ToolInput;
        if (args.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? path = String(args, input.ToolName == "NotebookEdit" ? "notebook_path" : "file_path");
        if (path is not { Length: > 0 })
        {
            return null;
        }

        switch (input.ToolName)
        {
            case "Edit":
                return ReplacementOf(args) is { } edit ? new EditCall(path, [edit], null) : null;
            case "MultiEdit":
                if (!args.TryGetProperty("edits", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var edits = new List<Replacement>();
                foreach (JsonElement item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || ReplacementOf(item) is not { } one)
                    {
                        return null;
                    }

                    edits.Add(one);
                }

                return new EditCall(path, edits, null);
            case "Write":
                return String(args, "content") is { } content ? new EditCall(path, [], Lf(content)) : null;
            case "NotebookEdit":
                return new EditCall(path, [], null);
            default:
                return null;
        }
    }

    public static string Lf(string text) => text.ReplaceLineEndings("\n");

    /// <summary>`\bname\b`: the name as a whole identifier.</summary>
    public static Regex Identifier(string name) => new(@"(?<![\w@])" + Regex.Escape(name) + @"(?!\w)", RegexOptions.CultureInvariant);

    /// <summary>The file after the call: <see cref="Content"/>, or the replacements applied in order; null when the call would fail.</summary>
    public string? Apply(string text)
    {
        if (Content is not null)
        {
            return Content;
        }

        string running = text;
        foreach (Replacement edit in Edits)
        {
            int count = Occurrences(running, edit.Old).Count;
            if (edit.Old.Length == 0 || count == 0 || count > 1 && !edit.All)
            {
                return null;
            }

            int first = running.IndexOf(edit.Old, StringComparison.Ordinal);
            running = edit.All ? running.Replace(edit.Old, edit.New, StringComparison.Ordinal) : running[..first] + edit.New + running[(first + edit.Old.Length)..];
        }

        return running;
    }

    /// <summary>
    /// Where each replacement sits in <paramref name="text"/> (the file before the call). Null when
    /// the call would fail: an old text not found, or found twice without `replace_all`, when the
    /// replacements are applied in order. A replacement whose old text only appears after an
    /// earlier one ran covers no lines.
    /// </summary>
    public IReadOnlyList<LocatedReplacement>? Locate(string text)
    {
        if (Content is not null || Apply(text) is null)
        {
            return null;
        }

        var located = new List<LocatedReplacement>();
        foreach (Replacement edit in Edits)
        {
            List<int> original = Occurrences(text, edit.Old);
            IEnumerable<int> at = edit.All ? original : original.Count == 1 ? original : [];
            located.Add(new LocatedReplacement(edit, [.. at.Select(i => (LineOf(text, i), LineOf(text, i + edit.Old.Length - 1)))]));
        }

        return located;
    }

    private static Replacement? ReplacementOf(JsonElement args) =>
        String(args, "old_string") is { } old && String(args, "new_string") is { } replacement
            ? new Replacement(Lf(old), Lf(replacement), args.TryGetProperty("replace_all", out JsonElement all) && all.ValueKind == JsonValueKind.True)
            : null;

    private static string? String(JsonElement args, string name) =>
        args.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static List<int> Occurrences(string text, string part)
    {
        var at = new List<int>();
        for (int i = part.Length == 0 ? -1 : text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
        {
            at.Add(i);
        }

        return at;
    }

    private static int LineOf(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }
}
