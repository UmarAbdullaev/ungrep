using System.Buffers.Binary;
using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityGraph.Code;
using UnityGraph.EditorLink;

namespace UnityGraph.Cli;

/// <summary>
/// `ug eval` (SPEC-editor-link, Running code): C# compiled here with Roslyn against what the editor
/// has loaded, then loaded from memory and run in the open editor by the bridge.
/// </summary>
internal static partial class EditorCommands
{
    /// <summary>Where all of the compile errors go when more than <see cref="MaxErrors"/> are shown.</summary>
    public const string ErrorsFile = "Library/unitygraph/eval/errors.txt";

    private const int MaxErrors = 10;
    private const int MaxErrorText = 300;
    private const int MaxBody = 16 * 1024 * 1024;

    /// <summary>What `ug eval` was given: the code or a file, the entry's arguments and the options.</summary>
    public sealed record EvalInput(string? Code, IReadOnlyList<string> Args, string? File, string? Entry, bool Check, int Timeout);

    private static Command EvalCommand()
    {
        var code = new Argument<string?>("code")
        {
            Description = "C#: an expression (Selection.count), statements (a last expression or return x is the result), or types with a static Run. With --file: the entry's first argument.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var args = new Argument<string[]>("args")
        {
            Description = "Arguments to a file's entry method in ug run's grammar (3, Hard, 0,1,0, name=value); statements take none.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var file = new Option<string?>("--file") { Description = "Read the code from a .cs file; - reads standard input." };
        var entry = new Option<string?>("--entry") { Description = "Type.Method a file of types starts from. Default: its one static Run or Main." };
        var check = new Option<bool>("--check") { Description = "Compile only and print the errors; works with Unity closed." };
        var timeout = new Option<int>("--timeout")
        {
            Description = "Seconds a loop in the code may keep the editor waiting, and to wait for a returned Task or IEnumerator. Default: 60.",
            DefaultValueFactory = _ => 60,
        };
        return Command(
            "eval",
            "Run C# in the open editor, edit or play mode: compiled by ug, loaded from memory, no domain reload. Prints its result, logs and what it modified.",
            [code, args, file, entry, check, timeout],
            (root, r) => Eval(root, new EvalInput(r.GetValue(code), r.GetValue(args) ?? [], r.GetValue(file), r.GetValue(entry), r.GetValue(check), r.GetValue(timeout))));
    }

    public static (int ExitCode, string Text) Eval(string root, EvalInput input)
    {
        _opFailed = false;
        List<string> args = [.. input.Args];
        string label = "snippet";
        string code;
        if (input.File != null)
        {
            if (input.Code != null)
            {
                args.Insert(0, input.Code);
            }

            label = input.File == "-" ? "stdin" : input.File.Replace('\\', '/');
            try
            {
                code = input.File == "-" ? Console.In.ReadToEnd() : File.ReadAllText(input.File);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Failed("cannot read " + input.File + ": " + e.Message + "\n");
            }
        }
        else if (input.Code != null)
        {
            code = input.Code;
        }
        else
        {
            return Failed("give the code: ug eval \"Selection.count\", or ug eval --file x.cs\n");
        }

        BridgeClient? client = _session;
        string? problem = null;
        if (client == null)
        {
            BridgeConnection connection = BridgeClient.Connect(root, ProcessCheck);
            client = connection.Client;
            problem = connection.Problem;
        }

        try
        {
            if (!input.Check && client == null)
            {
                return Failed("Unity not reachable (" + problem + "); ug eval needs the bridge (ug eval --check compiles without it)\n" + InstallHint(root));
            }

            if (!input.Check && !client!.HasCode)
            {
                return Failed("bridge " + client.Info.Bridge + " is too old for ug eval: run ug bridge install again\n");
            }

            int seconds = Math.Max(1, input.Timeout);
            var clock = Stopwatch.StartNew();
            SnippetSettings settings = EvalSettings(root, client, out bool noEval) with { GuardSeconds = seconds, Entry = input.Entry };
            if (noEval && !input.Check)
            {
                return Failed("bridge " + client!.Info.Bridge + " is too old for ug eval: run ug bridge install again\n");
            }

            // Code run before with the same settings and references is not compiled again.
            byte[] key = SnippetCache.Key(code, label, settings);
            SnippetBuild? cached = SnippetCache.Load(root, key);
            SnippetBuild build = cached ?? SnippetCompiler.Compile(code, label, settings, emit: !input.Check);
            if (cached == null && !input.Check)
            {
                SnippetCache.Save(root, key, build);
            }

            if (build.Errors.Count > 0)
            {
                return Failed(CompileErrors(root, label, build.Errors, input.Check));
            }

            if (build.Problem != null)
            {
                return Failed((input.Check ? label + ": " : "did not run " + label + ": ") + build.Problem + "\n");
            }

            if (input.Check)
            {
                return (0, "compiled: no errors (" + clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)\n");
            }

            if (build.Statements && args.Count > 0)
            {
                return Failed("did not run " + label + ": its statements take no arguments; arguments go to a file's entry method (--file x.cs --entry Type.Method)\n");
            }

            return Send(client!, label, build, args, seconds);
        }
        finally
        {
            if (client != _session)
            {
                client?.Dispose();
            }
        }
    }

    /// <summary>The bridge's list of what the editor has loaded, else the project's and the editor's folders.</summary>
    private static SnippetSettings EvalSettings(string root, BridgeClient? client, out bool noEval)
    {
        noEval = false;
        if (client is { HasCode: true })
        {
            try
            {
                EvalRefs refs = client.EvalRefs();
                return new SnippetSettings(refs.Refs, refs.Defines, refs.Lang);
            }
            catch (BridgeUnavailableException e)
            {
                // Compile against the folders instead; a 0.5.0 bridge from before ug eval has no /eval/refs.
                noEval = e.Message.StartsWith("bridge answered 404", StringComparison.Ordinal);
            }
        }

        string? executable = null;
        if (client != null)
        {
            try
            {
                using var process = Process.GetProcessById(client.Info.Pid);
                executable = process.MainModule?.FileName;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Not ours to read, or gone: the Hub's copy of the project's version.
            }
        }

        string? data = SnippetReferences.EditorData(root, executable, SnippetReferences.HubFolders());
        return new SnippetSettings(
            SnippetReferences.Fallback(root, data),
            SnippetReferences.Defines(root),
            SnippetCompiler.LanguageFor(SnippetReferences.UnityVersion(root) ?? ""));
    }

    private static (int ExitCode, string Text) Send(BridgeClient client, string label, SnippetBuild build, List<string> args, int seconds)
    {
        byte[] assembly = build.Assembly!;
        byte[] pdb = build.Pdb ?? [];
        var body = new byte[4 + assembly.Length + pdb.Length];
        BinaryPrimitives.WriteInt32LittleEndian(body, assembly.Length);
        assembly.CopyTo(body, 4);
        pdb.CopyTo(body, 4 + assembly.Length);
        if (body.Length > MaxBody)
        {
            return Failed("did not run " + label + ": the compiled code is " + (body.Length / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB, over the bridge's 16 MB\n");
        }

        var parameters = new List<(string, string?)>
        {
            ("source", label),
            ("what", build.Statements ? label : null),
            ("entry", build.EntryType + "." + build.EntryMethod),
            ("n", args.Count.ToString(CultureInfo.InvariantCulture)),
        };
        parameters.AddRange(args.Select((arg, i) => ("a" + i.ToString(CultureInfo.InvariantCulture), (string?)arg)));
        parameters.Add(("timeout", seconds.ToString(CultureInfo.InvariantCulture)));

        OpAnswer answer;
        try
        {
            answer = client.Eval(Query("/eval", [.. parameters]), body, TimeSpan.FromSeconds(seconds + 5));
        }
        catch (BridgeUnavailableException e) when (e.Message.StartsWith("bridge answered 4", StringComparison.Ordinal))
        {
            return Failed("did not run " + label + ": " + e.Message + "\n");
        }
        catch (BridgeUnavailableException e)
        {
            return Failed("no answer from Unity (" + e.Message + "); the code may still be running in the editor, and nothing stops it from outside\n");
        }

        return answer.Error != null ? Failed(answer.Error + "\n") : (0, string.Concat(answer.Lines!.Select(line => line + "\n")));
    }

    // `did not run snippet: 2 compile errors` and at most MaxErrors of them; all of them in ErrorsFile when cut.
    private static string CompileErrors(string root, string label, IReadOnlyList<SnippetError> errors, bool check)
    {
        string count = errors.Count.ToString(CultureInfo.InvariantCulture) + (errors.Count == 1 ? " compile error" : " compile errors");
        var text = new StringBuilder(check ? label + ": " + count : "did not run " + label + ": " + count).Append('\n');
        foreach (SnippetError error in errors.Take(MaxErrors))
        {
            string line = ErrorLine(label, error);
            text.Append("  ").Append(line.Length > MaxErrorText ? line[..(MaxErrorText - 3)] + "..." : line).Append('\n');
        }

        if (errors.Count > MaxErrors)
        {
            try
            {
                string path = Path.Combine(root, ErrorsFile);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, string.Concat(errors.Select(e => ErrorLine(label, e) + "\n")));
                text.Append("  (+").Append((errors.Count - MaxErrors).ToString(CultureInfo.InvariantCulture)).Append(" more: ").Append(ErrorsFile).Append(")\n");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                text.Append("  (+").Append((errors.Count - MaxErrors).ToString(CultureInfo.InvariantCulture)).Append(" more; ").Append(ErrorsFile).Append(" not written: ").Append(e.Message).Append(")\n");
            }
        }

        return text.ToString();
    }

    private static string ErrorLine(string label, SnippetError error) =>
        (error.Line > 0 ? label + ":" + error.Line.ToString(CultureInfo.InvariantCulture) + ":" + error.Column.ToString(CultureInfo.InvariantCulture) : label)
        + " " + error.Code + " " + error.Message;

    /// <summary>
    /// An `eval` step of `ug batch`: the rest of the line after its options is the code as written,
    /// one pair of `"` around all of it dropped (`\"` inside read as `"`). Null for other steps and `eval --file`, which split as usual.
    /// </summary>
    internal static string[]? EvalStep(string line)
    {
        string text = line.Trim();
        if (text.StartsWith("ug ", StringComparison.Ordinal))
        {
            text = text[3..].TrimStart();
        }

        if (!text.StartsWith("eval ", StringComparison.Ordinal))
        {
            return null;
        }

        var words = new List<string> { "eval" };
        string rest = text[5..].TrimStart();
        while (true)
        {
            int space = rest.IndexOf(' ', StringComparison.Ordinal);
            string word = space < 0 ? rest : rest[..space];
            string option = word.Split('=')[0];
            if (option == "--file")
            {
                return null;
            }

            if (option is not ("--check" or "--entry" or "--timeout"))
            {
                break;
            }

            words.Add(word);
            rest = space < 0 ? "" : rest[(space + 1)..].TrimStart();
            if (option != "--check" && !word.Contains('=', StringComparison.Ordinal))
            {
                space = rest.IndexOf(' ', StringComparison.Ordinal);
                words.Add(space < 0 ? rest : rest[..space]);
                rest = space < 0 ? "" : rest[(space + 1)..].TrimStart();
            }
        }

        if (Quoted(rest) is { } inner)
        {
            rest = inner;
        }

        words.Add("--");
        words.Add(rest);
        return [.. words];
    }

    // `"code"` with no other quote in it but `\"`: the code, `\"` read as `"`; null when not one quoted string (`"x" + "y"`).
    private static string? Quoted(string text)
    {
        if (text.Length < 2 || text[0] != '"' || text[^1] != '"')
        {
            return null;
        }

        string inner = text[1..^1];
        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '"' && (i == 0 || inner[i - 1] != '\\'))
            {
                return null;
            }
        }

        return inner.Replace("\\\"", "\"", StringComparison.Ordinal);
    }
}
