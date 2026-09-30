using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text;
using UnityGraph.EditorLink;

namespace UnityGraph.Cli;

/// <summary>
/// `ug batch`: several editor-ops steps in one call, over one connection to the bridge (SPEC-editor-ops,
/// Batch). Every step is checked before the first runs; a failing step stops the rest.
/// </summary>
internal static partial class EditorCommands
{
    private static Command Batch()
    {
        var steps = new Argument<string[]>("step") { Description = "One editor-ops command per argument, quoted: \"object create Crate\" \"set Crate speed 3\".", Arity = ArgumentArity.ZeroOrMore };
        var file = new Option<string?>("--file") { Description = "Read steps from this file, one per line (# starts a comment); - reads standard input." };
        var project = new Option<string>("--project") { Description = "Folder inside the Unity project. Default: current folder." };
        var batch = new Command("batch", "Run several editor-ops commands in one call, stopping at the first that fails. Each step is its own undo group.");
        batch.Arguments.Add(steps);
        batch.Options.Add(file);
        batch.Options.Add(project);
        batch.SetAction(result =>
        {
            (int exitCode, string text) = RunBatch(
                result.GetValue(project) ?? Directory.GetCurrentDirectory(),
                result.GetValue(steps) ?? [],
                result.GetValue(file));
            result.InvocationConfiguration.Output.Write(text.ReplaceLineEndings());
            return exitCode;
        });
        return batch;
    }

    private static (int ExitCode, string Text) RunBatch(string start, string[] arguments, string? file)
    {
        List<string> lines;
        try
        {
            lines = [.. arguments];
            if (file != null)
            {
                string content = file == "-" ? Console.In.ReadToEnd() : File.ReadAllText(file);
                lines.AddRange(content.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (1, "cannot read " + file + ": " + e.Message + "\n");
        }

        if (lines.Count == 0)
        {
            return (1, "give steps as arguments or with --file: ug batch \"object create Crate\" \"set Crate speed 3\"\n");
        }

        var editorRoot = new RootCommand();
        foreach (Command command in OpsCommands())
        {
            editorRoot.Subcommands.Add(command);
        }

        var parsed = new List<string[]>();
        var problems = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            string[] words = [.. CommandLineParser.SplitCommandLine(lines[i])];
            if (words.Length > 0 && words[0] == "ug")
            {
                words = words[1..];
            }

            string number = "step " + (i + 1).ToString(CultureInfo.InvariantCulture) + " (" + lines[i] + "): ";
            if (words.Length == 0 || editorRoot.Subcommands.All(c => c.Name != words[0]))
            {
                problems.Append(number).Append(words.Length == 0 ? "empty" : "not an editor-ops command; a batch runs " + string.Join(", ", editorRoot.Subcommands.Select(c => c.Name))).Append('\n');
            }
            else if (editorRoot.Parse(words).Errors is { Count: > 0 } errors)
            {
                problems.Append(number).Append(errors[0].Message).Append('\n');
            }
            else
            {
                parsed.Add(words);
            }
        }

        if (problems.Length > 0)
        {
            return (1, problems.Append("nothing ran\n").ToString());
        }

        return Start(start, (root, _) => RunSteps(root, lines, parsed, editorRoot));
    }

    private static (int ExitCode, string Text) RunSteps(string root, List<string> lines, List<string[]> parsed, RootCommand editorRoot)
    {
        BridgeConnection connection = BridgeClient.Connect(root, ProcessCheck);
        if (connection.Client == null)
        {
            return (0, "Unity not reachable (" + connection.Problem + "); ug batch needs the bridge\n" + InstallHint(root));
        }

        var text = new StringBuilder();
        using BridgeClient client = connection.Client;
        _session = client;
        try
        {
            for (int i = 0; i < parsed.Count; i++)
            {
                text.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append('/').Append(parsed.Count.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(lines[i]).Append('\n');
                var output = new StringWriter();
                var error = new StringWriter();
                _opFailed = false;
                int exit = editorRoot.Parse([.. parsed[i], "--project", root]).Invoke(new InvocationConfiguration { Output = output, Error = error });
                text.Append(output).Append(error);
                if (exit != 0 || _opFailed || error.GetStringBuilder().Length > 0)
                {
                    int left = parsed.Count - i - 1;
                    string rest = left == 0 ? "it was the last step" : left.ToString(CultureInfo.InvariantCulture) + (left == 1 ? " step" : " steps") + " not run";
                    return (0, text.Append("stopped at step ").Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(": ").Append(rest)
                        .Append(". Earlier steps stay done; ug undo reverts them one by one.\n").ToString());
                }
            }
        }
        finally
        {
            _session = null;
        }

        return (0, text.Append(parsed.Count.ToString(CultureInfo.InvariantCulture)).Append(parsed.Count == 1 ? " step" : " steps")
            .Append(" done. Each step is its own undo group (ug undo reverts the last).\n").ToString());
    }
}
