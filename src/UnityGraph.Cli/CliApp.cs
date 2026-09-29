using System.CommandLine;

namespace UnityGraph.Cli;

/// <summary>
/// Builds the `ug` command tree and runs it. Output writers are passed in so golden tests can
/// capture exactly what Claude would see.
/// </summary>
internal static class CliApp
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var root = new RootCommand("unitygraph: Unity project graph for Claude Code.");

        return root.Parse(args).Invoke(new InvocationConfiguration { Output = output, Error = error });
    }
}
