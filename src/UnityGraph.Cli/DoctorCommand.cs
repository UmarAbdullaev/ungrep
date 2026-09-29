using System.CommandLine;
using System.Runtime.InteropServices;
using System.Text;
using UnityGraph.Core;

namespace UnityGraph.Cli;

/// <summary>
/// `ug doctor`: checks what the plugin needs (runtime, Unity project, text assets, config, graph)
/// and says what to fix. Reads only; never writes into the project.
/// </summary>
internal static class DoctorCommand
{
    internal enum Status
    {
        Ok,
        Warn,
        Fail,
    }

    internal sealed record Check(Status Status, string Name, string Detail);

    public static Command Create(string? runtimeOverride)
    {
        var project = new Option<string>("--project")
        {
            Description = "Folder inside the Unity project to check. Default: current folder.",
        };
        var command = new Command("doctor", "Check the runtime, Unity project, config and graph.") { project };

        command.SetAction(result =>
        {
            string start = result.GetValue(project) ?? Directory.GetCurrentDirectory();
            IReadOnlyList<Check> checks = Run(start, runtimeOverride ?? DescribeRuntime());
            result.InvocationConfiguration.Output.Write(Render(checks).ReplaceLineEndings());
            return checks.Any(c => c.Status == Status.Fail) ? 1 : 0;
        });

        return command;
    }

    public static IReadOnlyList<Check> Run(string start, string runtime)
    {
        var checks = new List<Check> { new(Status.Ok, "runtime", runtime) };

        UnityProject? unity = UnityProject.Find(start);
        if (unity is null)
        {
            checks.Add(new(Status.Fail, "unity", $"no Unity project at or above {Path.GetFullPath(start)} (looked for Assets/ and ProjectSettings/ProjectVersion.txt)"));
            return checks;
        }

        checks.Add(unity.IsSupportedVersion
            ? new(Status.Ok, "unity", $"{unity.Root} (Unity {unity.EditorVersion})")
            : new(Status.Fail, "unity", $"{unity.Root}: Unity {unity.EditorVersion ?? "version unknown"} is not supported; needs {UnityProject.MinimumVersion} or newer"));

        checks.Add(unity.Serialization switch
        {
            AssetSerialization.ForceText => new(Status.Ok, "assets", "Force Text serialization"),
            null => new(Status.Warn, "assets", "serialization mode unknown (ProjectSettings/EditorSettings.asset unreadable)"),
            AssetSerialization mode => new(Status.Warn, "assets", $"{mode} serialization: binary assets are reported, not indexed; set Force Text in Project Settings > Editor"),
        });

        checks.Add(CheckConfig(unity.Root));

        checks.Add(File.Exists(unity.GraphPath)
            ? new(Status.Ok, "graph", "Library/unitygraph/graph.db")
            : new(Status.Warn, "graph", "not built yet (Library/unitygraph/graph.db)"));

        return checks;
    }

    private static Check CheckConfig(string root)
    {
        if (!File.Exists(Path.Combine(root, UnityGraphConfig.RelativePath)))
        {
            return new(Status.Ok, "config", $"defaults ({UnityGraphConfig.RelativePath} not present)");
        }

        try
        {
            UnityGraphConfig config = UnityGraphConfig.Load(root);
            var detail = new StringBuilder(UnityGraphConfig.RelativePath)
                .Append(": answer budget ").Append(config.AnswerBudget);
            foreach (string module in config.Overrides)
            {
                detail.Append(config.IsEnabled(module) ? "; on: " : "; off: ").Append(module);
            }

            if (!config.VendoredIsDefault)
            {
                detail.Append("; vendored: ").Append(string.Join(", ", config.Vendored.Prefixes));
            }

            return new(Status.Ok, "config", detail.ToString());
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
        {
            return new(Status.Fail, "config", $"{UnityGraphConfig.RelativePath}: {e.Message}");
        }
    }

    public static string Render(IReadOnlyList<Check> checks)
    {
        var text = new StringBuilder();
        foreach (Check check in checks)
        {
            string status = check.Status switch
            {
                Status.Ok => "ok",
                Status.Warn => "warn",
                _ => "FAIL",
            };
            text.Append(status.PadRight(6)).Append(check.Name.PadRight(9)).Append(check.Detail).Append('\n');
        }

        int failures = checks.Count(c => c.Status == Status.Fail);
        int warnings = checks.Count(c => c.Status == Status.Warn);
        text.Append("doctor: ")
            .Append(failures).Append(failures == 1 ? " failure, " : " failures, ")
            .Append(warnings).Append(warnings == 1 ? " warning\n" : " warnings\n");
        return text.ToString();
    }

    /// <summary>".NET 8.0.21 (C:\Program Files\dotnet)": the version running ug and its install root.</summary>
    private static string DescribeRuntime()
    {
        // <root>/shared/Microsoft.NETCore.App/<version>/
        var runtimeDir = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        string? root = runtimeDir.Parent?.Parent?.Parent?.FullName;
        return $".NET {Environment.Version}" + (root is null ? "" : $" ({root})");
    }
}
