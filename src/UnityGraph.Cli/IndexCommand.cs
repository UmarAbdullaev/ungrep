using System.CommandLine;
using System.Globalization;
using UnityGraph.Core;
using UnityGraph.Core.Graph;

namespace UnityGraph.Cli;

/// <summary>
/// `ug index`: builds or updates the graph of code and assets (SPEC-code-index, CLI;
/// SPEC-asset-index, Running with code-index). Writes only the graph
/// under `Library/unitygraph/` (or `--graph-dir`); reads the project.
/// </summary>
internal static class IndexCommand
{
    private const int MaxWarnings = 10;
    private const string WarningsFile = "index-warnings.txt";

    public static Command Create()
    {
        var project = new Option<string>("--project")
        {
            Description = "Folder inside the Unity project to index. Default: current folder.",
        };
        var full = new Option<bool>("--full")
        {
            Description = "Rebuild the whole graph instead of updating what changed.",
        };
        var graphDir = new Option<string>("--graph-dir")
        {
            Description = "Keep the graph in this folder instead of Library/unitygraph/.",
            Hidden = true,
        };
        var command = new Command("index", "Build or update the code and asset graph.") { project, full, graphDir };

        command.SetAction(result =>
        {
            TextWriter output = result.InvocationConfiguration.Output;
            string start = result.GetValue(project) ?? Directory.GetCurrentDirectory();
            int exitCode = Run(start, result.GetValue(full), result.GetValue(graphDir), out string text);
            output.Write(text.ReplaceLineEndings());
            return exitCode;
        });

        return command;
    }

    public static int Run(string start, bool full, string? graphDirectory, out string text)
    {
        UnityProject? unity = UnityProject.Find(start);
        if (unity is null)
        {
            text = $"no Unity project at or above {Path.GetFullPath(start)}\n";
            return 1;
        }

        UnityGraphConfig config;
        try
        {
            config = UnityGraphConfig.Load(unity.Root);
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
        {
            text = $"{UnityGraphConfig.RelativePath}: {e.Message}\n";
            return 1;
        }

        GraphUpdate update = GraphUpdate.Load(unity.Root, config);
        if (update.IsEmpty)
        {
            text = update.Warnings.Count > 0
                ? update.Warnings[0] + "\n"
                : $"code-index and asset-index are off in {UnityGraphConfig.RelativePath}\n";
            return 0;
        }

        IndexSummary summary;
        bool rebuilt;
        try
        {
            if (full)
            {
                using GraphStore staging = GraphStore.OpenStaging(unity.Root, graphDirectory: graphDirectory);
                using (BulkWriter writer = staging.BeginBulk())
                {
                    summary = update.Full(writer);
                    writer.Commit();
                }

                staging.Publish();
                rebuilt = true;
            }
            else
            {
                using GraphStore store = GraphStore.Open(unity.Root, StoreMode.ReadWrite, graphDirectory: graphDirectory);
                rebuilt = store.Created;
                summary = update.Update(store);
            }
        }
        catch (GraphLockedException e)
        {
            text = e.Message + "\n";
            return 1;
        }

        text = Render(summary, rebuilt, Warnings(unity.Root, graphDirectory, summary.Problems));
        return 0;
    }

    // Every warning of this index, one per line, in the graph directory; null when it cannot be written.
    private static string? Warnings(string root, string? graphDirectory, IReadOnlyList<string> problems)
    {
        string file = Path.Combine(GraphStore.GraphDirectory(root, graphDirectory), WarningsFile);
        try
        {
            File.WriteAllText(file, string.Concat(problems.Select(p => p + "\n")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        string relative = Path.GetRelativePath(root, file);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? file : relative.Replace('\\', '/');
    }

    internal static string Render(IndexSummary summary, bool rebuilt, string? warnings = "Library/unitygraph/" + WarningsFile)
    {
        var text = new System.Text.StringBuilder();
        string seconds = summary.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        if (rebuilt)
        {
            text.Append(CultureInfo.InvariantCulture, $"indexed {summary.Files:N0} files ({summary.Files - summary.Vendored:N0} own, {summary.Vendored:N0} vendored): ")
                .Append(CultureInfo.InvariantCulture, $"{summary.Nodes:N0} nodes, {summary.Edges:N0} edges in {seconds} s\n");
        }
        else if (summary.Files == 0 && summary.Removed == 0)
        {
            text.Append("up to date\n");
        }
        else
        {
            text.Append(CultureInfo.InvariantCulture, $"reindexed {summary.Files:N0} files, removed {summary.Removed:N0} in {seconds} s\n");
        }

        foreach (string problem in summary.Problems.Take(MaxWarnings))
        {
            text.Append("  warning: ").Append(problem).Append('\n');
        }

        if (summary.Problems.Count > MaxWarnings)
        {
            text.Append(CultureInfo.InvariantCulture, $"  ... and {summary.Problems.Count - MaxWarnings} more warnings{(warnings is null ? "" : ": " + warnings)}\n");
        }

        return text.ToString();
    }
}
