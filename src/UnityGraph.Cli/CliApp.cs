using System.CommandLine;

namespace UnityGraph.Cli;

/// <summary>
/// Builds the `ug` command tree and runs it. Output writers are passed in so golden tests can
/// capture exactly what Claude would see.
/// </summary>
internal static class CliApp
{
    // claudeConfigDir: Claude Code's config folder; null means CLAUDE_CONFIG_DIR, then ~/.claude.
    // runtime: what `ug doctor` reports as the runtime; null means the one running ug.
    // Both are set by tests only.
    public static int Run(string[] args, TextWriter output, TextWriter error, string? claudeConfigDir = null, string? runtime = null)
    {
        var root = new RootCommand("unitygraph: Unity project graph for Claude Code.")
        {
            DoctorCommand.Create(runtime),
            IndexCommand.Create(),
            StatsCommand.Create(claudeConfigDir),
        };
        foreach (Command command in QueryCommands.Create())
        {
            root.Subcommands.Add(command);
        }

        return root.Parse(args).Invoke(new InvocationConfiguration { Output = output, Error = error });
    }
}
