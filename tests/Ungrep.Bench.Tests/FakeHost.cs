namespace Ungrep.Bench.Tests;

/// <summary>
/// A host with no shell, editor or `claude`: shell commands answer from <see cref="OnShell"/>,
/// files from <see cref="Files"/>, MCP servers from <see cref="FakeMcp"/>. Every call is logged
/// in order, so tests see what ran and when.
/// </summary>
internal sealed class FakeHost : IBenchHost
{
    public List<string> Calls { get; } = [];

    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, FakeMcp> Servers { get; } = new(StringComparer.Ordinal);

    public Func<string, CallAnswer> OnShell { get; set; } = command => new CallAnswer("ran " + command + "\n", 0, TimeSpan.FromMilliseconds(100));

    public Func<string, IReadOnlyList<string>, (int Exit, string Stdout, string Stderr, TimeSpan Elapsed)> OnExec { get; set; } =
        (_, _) => (0, "", "", TimeSpan.FromSeconds(1));

    public CallAnswer Shell(string command, string cwd, TimeSpan timeout)
    {
        Calls.Add("shell " + command);
        return OnShell(command);
    }

    public string? ReadFile(string path)
    {
        Calls.Add("read " + Path.GetFileName(path));
        return Files.TryGetValue(Path.GetFileName(path), out string? text) ? text : null;
    }

    public IMcpSession OpenMcp(McpServer server, string cwd)
    {
        Calls.Add("open " + server.Command);
        return Servers.TryGetValue(server.Command, out FakeMcp? mcp) ? mcp : throw new McpException("no such server");
    }

    public (int Exit, string Stdout, string Stderr, TimeSpan Elapsed) Exec(string program, IReadOnlyList<string> args, string cwd, TimeSpan timeout, string? stdin = null)
    {
        Calls.Add("exec " + program);
        return OnExec(program, args);
    }
}

/// <summary>An MCP server answering each tool from a function; its tool list is fixed.</summary>
internal sealed class FakeMcp(IReadOnlyList<(string Name, string Json)> tools, Func<string, string, string> answer, List<string> calls) : IMcpSession
{
    public CallAnswer Call(string tool, string argumentsJson, TimeSpan timeout)
    {
        calls.Add("mcp " + tool + " " + argumentsJson);
        return new CallAnswer(answer(tool, argumentsJson), 0, TimeSpan.FromMilliseconds(50));
    }

    public IReadOnlyList<(string Name, string Json)> Tools() => tools;

    public void Dispose()
    {
    }
}
