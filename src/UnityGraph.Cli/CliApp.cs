using System.CommandLine;

namespace UnityGraph.Cli;

/// <summary>
/// Builds the `ug` command tree and runs it. Output writers are passed in so golden tests can
/// capture exactly what Claude would see.
/// </summary>
internal static class CliApp
{
    // claudeConfigDir: Claude Code's config folder; null means CLAUDE_CONFIG_DIR, then ~/.claude.
    // Tests point it at fixtures.
    public static int Run(string[] args, TextWriter output, TextWriter error, string? claudeConfigDir = null)
    {
        var root = new RootCommand("unitygraph: Unity project graph for Claude Code.")
        {
            StatsCommand.Create(claudeConfigDir),
        };

        return root.Parse(args).Invoke(new InvocationConfiguration { Output = output, Error = error });
    }
}
