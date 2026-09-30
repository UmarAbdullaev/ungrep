using System.CommandLine;
using System.Globalization;
using System.Text;
using UnityGraph.Core;
using UnityGraph.EditorLink;

namespace UnityGraph.Cli;

/// <summary>
/// `ug status`, `ug compile`, `ug run` and `ug bridge install|remove` (SPEC-editor-link, Commands):
/// the running editor through its bridge, or Editor.log when no bridge answers.
/// </summary>
internal static class EditorCommands
{
    public const string PackageName = "com.unitygraph.bridge";

    /// <summary>Where `ug bridge install --copy` puts the package: a vendored folder, so the graph skips it.</summary>
    public static readonly string CopyFolder = Path.Combine("Assets", "Plugins", "UnityGraphBridge");

    /// <summary>Replaces the check that the editor process in `bridge.json` is alive; tests only.</summary>
    internal static Func<int, bool>? ProcessCheck { get; set; }

    // editorLog: the Editor.log to fall back on; null means Unity's default place. Tests only.
    public static IEnumerable<Command> Create(string? editorLog)
    {
        yield return Command("status", "The editor's state and last compile, through the bridge.", [], (root, _) => Status(root));

        var warnings = new Option<bool>("--warnings") { Description = "List warnings too, not only their count." };
        var timeout = new Option<int>("--timeout") { Description = "Seconds to wait for Unity. Default: 60.", DefaultValueFactory = _ => 60 };
        yield return Command("compile", "Refresh, wait for the script compile, and list its errors.", [warnings, timeout], (root, result) =>
            Compile(root, result.GetValue(warnings), TimeSpan.FromSeconds(Math.Max(1, result.GetValue(timeout))), editorLog ?? EditorLog.DefaultPath()));

        var target = new Argument<string>("target") { Description = "Namespace.Type.Method or Type.Method (static, no parameters), or a menu path like Tools/Build." };
        var runTimeout = new Option<int>("--timeout") { Description = "Seconds to wait for the method. Default: 60.", DefaultValueFactory = _ => 60 };
        yield return Command("run", "Run a static editor method or a menu item in the open editor.", [target, runTimeout], (root, result) =>
            Run(root, result.GetValue(target)!, TimeSpan.FromSeconds(Math.Max(1, result.GetValue(runTimeout)))));

        var copy = new Option<bool>("--copy") { Description = "Copy the package into " + CopyFolder.Replace('\\', '/') + "/ instead of printing how to add it." };
        var bridge = new Command("bridge", "Add the editor bridge to the project, or remove it.");
        bridge.Subcommands.Add(Command("install", "Print how to add the bridge package, or copy it with --copy.", [copy], (root, result) =>
            Install(root, result.GetValue(copy), FindPackage())));
        bridge.Subcommands.Add(Command("remove", "Delete the copy made by `ug bridge install --copy`.", [], (root, _) => Remove(root)));
        yield return bridge;
    }

    public static (int ExitCode, string Text) Status(string root)
    {
        BridgeConnection connection = BridgeClient.Connect(root, ProcessCheck);
        if (connection.Client == null)
        {
            return (0, "Unity not reachable (" + connection.Problem + ")\n" + InstallHint(root));
        }

        using BridgeClient client = connection.Client;
        BridgeStatus status = connection.Status!;
        var text = new StringBuilder()
            .Append("Unity ").Append(status.Unity).Append("  ").Append(status.State).Append("  bridge ").Append(status.Bridge).Append('\n');
        if (status.Compile == 0)
        {
            return (0, text.Append("  last compile  none since the editor opened\n").ToString());
        }

        try
        {
            CompileResult last = client.LastCompile();
            text.Append("  last compile  ").Append(CompileText.Counts(last.Messages));
            if (last.Finished is { } finished)
            {
                text.Append("  ").Append(finished.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                if (last.Started is { } started)
                {
                    text.Append(" (").Append((finished - started).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)).Append(" s)");
                }
            }

            text.Append('\n');
        }
        catch (BridgeUnavailableException e)
        {
            text.Append("  last compile  unknown (").Append(e.Message).Append(")\n");
        }

        return (0, text.ToString());
    }

    public static (int ExitCode, string Text) Compile(string root, bool warnings, TimeSpan timeout, string? editorLog)
    {
        BridgeConnection connection = BridgeClient.Connect(root, ProcessCheck);
        if (connection.Client == null)
        {
            return (0, CompileText.Fallback(root, connection.Problem!, editorLog, warnings));
        }

        using BridgeClient client = connection.Client;
        CompileOutcome outcome = client.Compile(timeout);
        return (0, outcome.Problem == null
            ? CompileText.Format(outcome, warnings)
            : CompileText.Fallback(root, outcome.Problem, editorLog, warnings));
    }

    public static (int ExitCode, string Text) Run(string root, string target, TimeSpan timeout)
    {
        BridgeConnection connection = BridgeClient.Connect(root, ProcessCheck);
        if (connection.Client == null)
        {
            return (0, "Unity not reachable (" + connection.Problem + "); ug run needs the bridge\n" + InstallHint(root));
        }

        using BridgeClient client = connection.Client;
        RunOutcome outcome;
        try
        {
            outcome = client.Run(target, timeout);
        }
        catch (BridgeUnavailableException e)
        {
            return (0, "no answer from Unity (" + e.Message + ")\n");
        }

        string ms = outcome.Ms.ToString("0", CultureInfo.InvariantCulture);
        if (!outcome.Ran)
        {
            return (0, "did not run " + target + ": " + outcome.Error + "\n");
        }

        if (outcome.Error != null)
        {
            return (0, target + " threw after " + ms + " ms: " + outcome.Error + "\n" + (outcome.Frame == null ? "" : "  at " + outcome.Frame + "\n"));
        }

        var text = new StringBuilder("ran ").Append(target).Append(" in ").Append(ms).Append(" ms\n");
        if (outcome.Result != null)
        {
            text.Append("  result: ").Append(outcome.Result.ReplaceLineEndings("\n    ")).Append('\n');
        }

        return (0, text.ToString());
    }

    public static (int ExitCode, string Text) Install(string root, bool copy, string? package)
    {
        if (package == null)
        {
            return (0, "the bridge package is missing from this unitygraph install (unity/" + PackageName + ")\n");
        }

        string? installed = Installed(root);
        if (!copy)
        {
            var text = new StringBuilder();
            if (installed != null)
            {
                text.Append("the bridge is already in this project (").Append(installed).Append(")\n");
            }

            return (0, text
                .Append("The bridge is a Unity package in the plugin: ").Append(Path.Combine(package, "package.json")).Append('\n')
                .Append("Add it one of two ways; both write into the project:\n")
                .Append("  1. In Unity: Window > Package Manager > + > Install package from disk..., and pick that file.\n")
                .Append("     Unity records it in Packages/manifest.json; ug never edits that file.\n")
                .Append("  2. ug bridge install --copy: copies it to ").Append(CopyFolder.Replace('\\', '/')).Append("/ (vendored, so not indexed).\n")
                .Append("Once Unity has compiled it, ug status shows the editor.\n")
                .ToString());
        }

        if (installed == "Packages/manifest.json")
        {
            return (0, "the bridge is already added through Package Manager (Packages/manifest.json); a copy would clash with it\n");
        }

        string destination = Path.Combine(root, CopyFolder);
        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, recursive: true);
        }

        int files = CopyPackage(package, destination);
        return (0, "copied the bridge (" + files.ToString(CultureInfo.InvariantCulture) + " files) to " + CopyFolder.Replace('\\', '/')
            + "/; Unity compiles it on its next refresh, then ug status shows the editor\n");
    }

    public static (int ExitCode, string Text) Remove(string root)
    {
        string destination = Path.Combine(root, CopyFolder);
        if (!Directory.Exists(destination))
        {
            return (0, Installed(root) == "Packages/manifest.json"
                ? "the bridge was added through Package Manager: remove it there (Window > Package Manager)\n"
                : "no bridge copy in " + CopyFolder.Replace('\\', '/') + "/\n");
        }

        Directory.Delete(destination, recursive: true);
        File.Delete(destination + ".meta");
        return (0, "removed " + CopyFolder.Replace('\\', '/') + "/\n");
    }

    /// <summary>The package folder shipped with the plugin: `unity/com.unitygraph.bridge` above ug's own folder.</summary>
    public static string? FindPackage()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "unity", PackageName);
            if (File.Exists(Path.Combine(candidate, "package.json")))
            {
                return candidate;
            }
        }

        return null;
    }

    // Where the project has the bridge: its manifest, the --copy folder, or nowhere.
    private static string? Installed(string root)
    {
        string manifest = Path.Combine(root, "Packages", "manifest.json");
        if (File.Exists(manifest) && File.ReadAllText(manifest).Contains("\"" + PackageName + "\"", StringComparison.Ordinal))
        {
            return "Packages/manifest.json";
        }

        return File.Exists(Path.Combine(root, CopyFolder, "package.json")) ? CopyFolder.Replace('\\', '/') + "/" : null;
    }

    private static string InstallHint(string root) =>
        Installed(root) == null && !File.Exists(BridgeClient.DiscoveryPath(root))
            ? "  the bridge is not in this project; `ug bridge install` shows how to add it\n"
            : "";

    // The package without its own tests: in Assets/ they would compile into the user's test assemblies.
    private static int CopyPackage(string package, string destination)
    {
        int count = 0;
        foreach (string file in Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(package, file);
            string top = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (top is "Tests" or "Tests.meta")
            {
                continue;
            }

            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            count++;
        }

        return count;
    }

    private static Command Command(string name, string description, Symbol[] symbols, Func<string, ParseResult, (int, string)> run)
    {
        var project = new Option<string>("--project") { Description = "Folder inside the Unity project. Default: current folder." };
        var command = new Command(name, description);
        foreach (Symbol symbol in symbols)
        {
            if (symbol is Argument argument)
            {
                command.Arguments.Add(argument);
            }
            else
            {
                command.Options.Add((Option)symbol);
            }
        }

        command.Options.Add(project);
        command.SetAction(result =>
        {
            (int exitCode, string text) = Start(result.GetValue(project) ?? Directory.GetCurrentDirectory(), root => run(root, result));
            result.InvocationConfiguration.Output.Write(text.ReplaceLineEndings());
            return exitCode;
        });
        return command;
    }

    private static (int, string) Start(string start, Func<string, (int, string)> run)
    {
        UnityProject? unity = UnityProject.Find(start);
        if (unity is null)
        {
            return (1, $"no Unity project at or above {Path.GetFullPath(start)}\n");
        }

        UnityGraphConfig config;
        try
        {
            config = UnityGraphConfig.Load(unity.Root);
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
        {
            return (1, $"{UnityGraphConfig.RelativePath}: {e.Message}\n");
        }

        return config.IsEnabled("editor-link")
            ? run(unity.Root)
            : (0, "editor-link is off in " + UnityGraphConfig.RelativePath + "\n");
    }
}
