using System.Text;

namespace Ungrep.Hooks;

/// <summary>What a move, copy or remove command does to which paths (SPEC-guards, Parsing a shell command).</summary>
internal enum FileOpKind
{
    Move,
    Copy,
    Remove,

    /// <summary>`cd` and its kin: later relative paths resolve against <see cref="FileOp.Destination"/>, unknown when null.</summary>
    ChangeDirectory,
}

/// <summary>
/// One `mv`, `cp` or `rm` (or their PowerShell and git forms) with literal paths only: the verb as
/// typed, the sources, the destination of a move or copy, and whether a copy is recursive.
/// </summary>
internal sealed record FileOp(FileOpKind Kind, string Verb, IReadOnlyList<string> Sources, string? Destination, bool Recursive);

/// <summary>
/// A small reader of Bash and PowerShell command lines, enough to find file moves, copies and
/// removes. It splits simple commands (`;` `&amp;&amp;` `||` `|` newline), honours quotes and skips
/// here-document bodies. A command whose paths it cannot know without running the shell (a variable,
/// a glob, `~`) is not reported; a `cd` it cannot follow is reported with an unknown destination.
/// </summary>
internal static class ShellCommand
{
    private const string Dynamic = "$`*?[~{";

    // PowerShell parameters that take a value, so the value is not read as a path.
    private static readonly HashSet<string> _valueParameters =
    [
        "erroraction", "ea", "warningaction", "wa", "informationaction", "infa", "errorvariable", "ev",
        "outvariable", "ov", "filter", "include", "exclude", "credential", "stream", "pipelinevariable", "pv",
    ];

    /// <summary>The file operations in <paramref name="text"/>, in order.</summary>
    public static IReadOnlyList<FileOp> Operations(string text, bool powerShell)
    {
        var ops = new List<FileOp>();
        foreach (List<string> words in Split(text, powerShell))
        {
            if (Operation(words, powerShell) is { } op)
            {
                ops.Add(op);
            }
        }

        return ops;
    }

    /// <summary>Simple commands as word lists.</summary>
    internal static List<List<string>> Split(string text, bool powerShell)
    {
        var commands = new List<List<string>>();
        var words = new List<string>();
        var word = new StringBuilder();
        bool inWord = false;
        var heredocs = new List<string>();
        char escape = powerShell ? '`' : '\\';

        void EndWord()
        {
            if (inWord)
            {
                words.Add(word.ToString());
                word.Clear();
                inWord = false;
            }
        }

        void EndCommand()
        {
            EndWord();
            if (words.Count > 0)
            {
                commands.Add(words);
                words = [];
            }
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is ' ' or '\t' or '\r')
            {
                EndWord();
            }
            else if (c == '\n')
            {
                EndCommand();
                i = SkipHeredocs(text, i, heredocs);
            }
            else if (c is ';' or '|' or '&' or '(' or ')')
            {
                EndCommand();
            }
            else if (c == '#' && !inWord)
            {
                while (i + 1 < text.Length && text[i + 1] != '\n')
                {
                    i++;
                }
            }
            else if (!powerShell && c == '<' && i + 1 < text.Length && text[i + 1] == '<' && !(i + 2 < text.Length && text[i + 2] == '<'))
            {
                i = ReadHeredocWord(text, i + 2, heredocs);
            }
            else if (c == '\'')
            {
                inWord = true;
                int end = text.IndexOf('\'', i + 1);
                end = end < 0 ? text.Length : end;
                word.Append(text, i + 1, end - i - 1);
                i = end;
            }
            else if (c == '"')
            {
                inWord = true;
                for (i++; i < text.Length && text[i] != '"'; i++)
                {
                    if (text[i] == escape && i + 1 < text.Length)
                    {
                        i++;
                    }

                    word.Append(text[i]);
                }
            }
            else if (c == escape && i + 1 < text.Length && text[i + 1] is '\n' or '\r')
            {
                // A line continuation: the command goes on.
                i += text[i + 1] == '\r' && i + 2 < text.Length && text[i + 2] == '\n' ? 2 : 1;
                EndWord();
            }
            else if (c == escape && i + 1 < text.Length)
            {
                inWord = true;
                word.Append(text[++i]);
            }
            else
            {
                inWord = true;
                word.Append(c);
            }
        }

        EndCommand();
        return commands;
    }

    // `<<WORD`, `<<-WORD`, `<<'WORD'`: remember WORD; the body starts at the next line. Returns the index of the last char read.
    private static int ReadHeredocWord(string text, int start, List<string> heredocs)
    {
        int i = start;
        if (i < text.Length && text[i] == '-')
        {
            i++;
        }

        while (i < text.Length && text[i] is ' ' or '\t')
        {
            i++;
        }

        var delimiter = new StringBuilder();
        for (; i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not (';' or '|' or '&' or '(' or ')'); i++)
        {
            if (text[i] is not ('\'' or '"' or '\\'))
            {
                delimiter.Append(text[i]);
            }
        }

        if (delimiter.Length > 0)
        {
            heredocs.Add(delimiter.ToString());
        }

        return i - 1;
    }

    // At the newline ending the line with `<<WORD`: skip lines up to and including the one that is WORD.
    private static int SkipHeredocs(string text, int newline, List<string> heredocs)
    {
        int i = newline;
        foreach (string delimiter in heredocs)
        {
            while (i + 1 < text.Length)
            {
                int end = text.IndexOf('\n', i + 1);
                end = end < 0 ? text.Length : end;
                string line = text[(i + 1)..end].Trim();
                i = end;
                if (line == delimiter)
                {
                    break;
                }
            }
        }

        heredocs.Clear();
        return i;
    }

    private static FileOp? Operation(List<string> words, bool powerShell)
    {
        int start = 0;
        while (start < words.Count && (words[start] is "sudo" or "command" or "env" || words[start].Contains('=', StringComparison.Ordinal) && !words[start].StartsWith('-')))
        {
            start++;
        }

        if (start >= words.Count)
        {
            return null;
        }

        string verb = words[start].ToLowerInvariant();
        int first = start + 1;
        if (verb == "git" && start + 1 < words.Count && words[start + 1] is "mv" or "rm")
        {
            verb = words[start + 1];
            first = start + 2;
        }

        string typed = first == start + 2 ? words[start] + " " + words[start + 1] : words[start];
        List<string> args = words.Skip(first).ToList();
        if (verb is "cd" or "chdir" or "pushd" or "popd" or "set-location" or "sl" or "push-location" or "pop-location")
        {
            return ChangeDirectory(verb, typed, args, powerShell);
        }

        bool rename = verb is "rename-item" or "rni" or "ren";
        FileOpKind? kind = verb switch
        {
            "mv" or "move-item" or "mi" or "move" or "rename-item" or "rni" or "ren" => FileOpKind.Move,
            "cp" or "copy-item" or "cpi" or "copy" => FileOpKind.Copy,
            "rm" or "remove-item" or "ri" or "del" or "rd" or "rmdir" or "erase" => FileOpKind.Remove,
            _ => null,
        };
        if (kind is null || Arguments(kind.Value, typed, args, powerShell) is not { } op)
        {
            return null;
        }

        if (!rename)
        {
            return op;
        }

        // `Rename-Item a.cs b.cs` names the new file in a's folder.
        if (op.Sources.Count != 1 || op.Destination is not { } name || name.IndexOfAny(['/', '\\']) >= 0)
        {
            return null;
        }

        string source = op.Sources[0].TrimEnd('/', '\\');
        int slash = source.LastIndexOfAny(['/', '\\']);
        return op with { Destination = slash < 0 ? name : source[..(slash + 1)] + name };
    }

    // The folder a `cd` goes to; null when it can't be known (no argument, `-`, a variable, `popd`).
    private static FileOp ChangeDirectory(string verb, string typed, List<string> args, bool powerShell)
    {
        string? target = null;
        for (int i = 0; i < args.Count && verb is not ("popd" or "pop-location"); i++)
        {
            string name = args[i].TrimStart('-').ToLowerInvariant();
            if (powerShell && args[i].StartsWith('-') && name is "path" or "literalpath" && i + 1 < args.Count)
            {
                target = args[++i];
            }
            else if (!args[i].StartsWith('-') && target is null)
            {
                target = args[i];
            }
        }

        bool known = target is { Length: > 0 } && target.IndexOfAny(Dynamic.ToCharArray()) < 0;
        return new FileOp(FileOpKind.ChangeDirectory, typed, [], known ? target : null, false);
    }

    private static FileOp? Arguments(FileOpKind kind, string verb, List<string> args, bool powerShell)
    {
        var positional = new List<string>();
        string? destination = null;
        bool recursive = false;
        bool options = true;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (options && arg == "--")
            {
                options = false;
                continue;
            }

            if (options && (arg is "--target-directory" or "-t" || arg.StartsWith("--target-directory=", StringComparison.Ordinal)))
            {
                return null;
            }

            if (options && arg.Length > 1 && arg[0] == '-' && !char.IsAsciiDigit(arg[1]))
            {
                string name = arg.TrimStart('-').ToLowerInvariant();
                if (powerShell && name is "path" or "literalpath" && i + 1 < args.Count)
                {
                    positional.AddRange(args[++i].Split(','));
                }
                else if (powerShell && name is "destination" or "newname" && i + 1 < args.Count)
                {
                    destination = args[++i];
                }
                else if (powerShell && _valueParameters.Contains(name))
                {
                    i++;
                }
                else if (name is "recurse" or "recursive" || !powerShell && !arg.StartsWith("--", StringComparison.Ordinal) && name.IndexOfAny(['r', 'a']) >= 0)
                {
                    recursive = true;
                }

                continue;
            }

            // A redirect (`> log`, `2>> log`, `>log`) names no path of the command; its file follows unless attached.
            if (arg.TrimStart('1', '2') is ['>', ..] redirect)
            {
                i += redirect.TrimStart('>').Length == 0 ? 1 : 0;
                continue;
            }

            positional.AddRange(powerShell ? arg.Split(',') : [arg]);
        }

        if (positional.Concat(destination is null ? [] : [destination]).Any(p => p.Length == 0 || p.IndexOfAny(Dynamic.ToCharArray()) >= 0))
        {
            return null;
        }

        if (kind == FileOpKind.Remove)
        {
            return positional.Count == 0 ? null : new FileOp(kind, verb, positional, null, recursive);
        }

        if (destination is null)
        {
            if (positional.Count < 2)
            {
                return null;
            }

            destination = positional[^1];
            positional.RemoveAt(positional.Count - 1);
        }

        return positional.Count == 0 ? null : new FileOp(kind, verb, positional, destination, recursive);
    }
}
