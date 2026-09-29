using UnityGraph.Hooks;

namespace UnityGraph.Cli;

/// <summary>
/// `ug hook &lt;event&gt;`, called from hooks/hooks.json with the payload on stdin. Bypasses the
/// command parser: it runs on every matched tool call, so it starts fast, and a bad command
/// line is silent with exit 0 like every other failure (design rule 9).
/// </summary>
internal static class HookCommand
{
    public static int Run(string[] args, TextReader input, TextWriter output, IReadOnlyList<IHookHandler> handlers)
    {
        if (args is not [_, string eventName] || eventName.Length == 0)
        {
            return 0;
        }

        string fallbackCwd = Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR") is { Length: > 0 } projectDir
            ? projectDir
            : Directory.GetCurrentDirectory();
        return new HookDispatcher(handlers).Run(eventName, input, output, fallbackCwd);
    }
}
