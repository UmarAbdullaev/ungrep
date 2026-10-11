using System.CommandLine;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityGraph.Core;
using UnityGraph.Core.Graph;
using UnityGraph.Viewer;

namespace UnityGraph.Cli;

/// <summary>
/// `ug view` (SPEC-viewer): the neighbourhood of one symbol or asset as a self-contained HTML page
/// for people. Claude sees one line, the file and what is in it.
/// </summary>
internal static class ViewCommand
{
    public static Command Create()
    {
        var symbol = new Argument<string>("symbol") { Description = "Symbol, asset path or node key." };
        var depth = new Option<int>("--depth") { Description = $"Hops from the symbol, {Neighbourhood.MinDepth} to {Neighbourhood.MaxDepth}.", DefaultValueFactory = _ => 2 };
        var kind = new Option<string?>("--kind") { Description = "Only these kinds, comma separated: node kinds (type, method, game_object, ...) or asset types (scene, prefab, ...)." };
        var all = new Option<bool>("--all") { Description = string.Create(CultureInfo.InvariantCulture, $"Bake every node; without it the page stops at {Neighbourhood.MaxBaked:N0}.") };
        var open = new Option<bool>("--open") { Description = "Open the page in the default browser." };
        var output = new Option<string?>("--out") { Description = "Write the page here. Default: Library/unitygraph/view.html." };
        var project = new Option<string>("--project") { Description = "Folder inside the Unity project. Default: current folder." };
        var graphDir = new Option<string>("--graph-dir") { Hidden = true };
        var command = new Command("view", "Draw a symbol's neighbourhood as an HTML page for people (one line to Claude).")
        {
            symbol, depth, kind, all, open, output, project, graphDir,
        };
        command.Validators.Add(result =>
        {
            // A command validator sees no default for an absent option, so only a given --depth is checked.
            if (result.GetResult(depth) is not null && result.GetValue(depth) is < Neighbourhood.MinDepth or > Neighbourhood.MaxDepth)
            {
                result.AddError($"--depth must be {Neighbourhood.MinDepth} to {Neighbourhood.MaxDepth}");
            }
            else if (Kinds(result.GetValue(kind)) is { } kinds && kinds.FirstOrDefault(k => !Neighbourhood.KindChoices.Contains(k, StringComparer.Ordinal)) is { } unknown)
            {
                result.AddError($"--kind: unknown kind '{unknown}'; kinds: {string.Join(", ", Neighbourhood.KindChoices)}");
            }
        });
        command.SetAction(result =>
        {
            string start = result.GetValue(project) ?? Directory.GetCurrentDirectory();
            string query = result.GetValue(symbol)!;
            var options = new ViewOptions(result.GetValue(depth), Kinds(result.GetValue(kind)), result.GetValue(all), DateTimeOffset.Now);
            string? graphDirectory = result.GetValue(graphDir);
            bool failed = false;
            int exitCode = QueryCommands.Run(start, null, graphDirectory, (store, engine, budget) =>
            {
                IReadOnlyList<Node>? roots = engine.Resolve("view", query, budget, out string answer);
                if (roots is null)
                {
                    return answer;
                }

                string root = UnityProject.Find(start)!.Root;
                string file = Path.GetFullPath(result.GetValue(output) ?? Path.Combine(GraphStore.GraphDirectory(root, graphDirectory), "view.html"));
                ViewGraph graph = Neighbourhood.Walk(store, roots, query, options);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllText(file, ViewPage.Render(graph), new UTF8Encoding(false));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    failed = true;
                    return $"could not write {file}: {e.Message}\n";
                }

                string line = string.Create(CultureInfo.InvariantCulture,
                    $"{Shown(root, file)}  {graph.Nodes.Count} nodes ({graph.FirstOpen()} drawn), {graph.Edges.Count} edges  generated {graph.Generated} from graph {graph.Revision}");
                if (graph.Cut)
                {
                    line += string.Create(CultureInfo.InvariantCulture, $"; cut at {Neighbourhood.MaxBaked} nodes: {graph.AllCommand}");
                }

                if (result.GetValue(open) && !Open(file))
                {
                    line += " (could not open a browser)";
                }

                return line + "\n";
            }, out string text, result.GetValue(all), module: "viewer");
            result.InvocationConfiguration.Output.Write(text.ReplaceLineEndings());
            return failed ? 1 : exitCode;
        });
        return command;
    }

    private static HashSet<string>? Kinds(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? null
            : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);

    // The project-relative path when the page is inside the project, else the full one.
    private static string Shown(string root, string file)
    {
        string relative = Path.GetRelativePath(root, file);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? file : relative.Replace('\\', '/');
    }

    private static bool Open(string file)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            return true;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or PlatformNotSupportedException or FileNotFoundException)
        {
            return false;
        }
    }
}
