using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Ungrep.Bench;

/// <summary>
/// Runs steps in a project the way Claude Code's tools would answer them (SPEC-bench, Suites):
/// `run` through the shell, `read` like the Read tool, `mcp` on a suite server started on its
/// first call and kept open until <see cref="EndSession"/>. Keeps each server's tool list, for
/// the idle cost.
/// </summary>
public sealed class StepExecutor : IDisposable
{
    /// <summary>The Read tool's limits: lines per read and characters per line.</summary>
    public const int ReadLines = 2000;

    public const int ReadLineChars = 2000;

    private static readonly TimeSpan _mcpTimeout = TimeSpan.FromSeconds(120);

    private readonly IBenchHost _host;
    private readonly Suite _suite;
    private readonly string _project;
    private readonly Dictionary<string, IMcpSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _startErrors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<(string Name, string Json)>> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _toolErrors = new(StringComparer.Ordinal);
    private readonly Action<TimeSpan> _sleep;

    // sleep: waits between polls; tests pass a no-op.
    public StepExecutor(IBenchHost host, Suite suite, string project, Action<TimeSpan>? sleep = null)
    {
        _host = host;
        _suite = suite;
        _project = project;
        _sleep = sleep ?? Thread.Sleep;
    }

    /// <summary>
    /// A server's tool list, read in its first session; null with the reason when it never
    /// started or tools/list failed.
    /// </summary>
    public IReadOnlyList<(string Name, string Json)>? ToolsOf(string server, out string? error)
    {
        if (_sessions.TryGetValue(server, out IMcpSession? session))
        {
            ReadTools(server, session);
        }

        error = _startErrors.ContainsKey(server) ? "did not start"
            : _toolErrors.TryGetValue(server, out string? failed) ? "tools/list failed: " + failed
            : _tools.ContainsKey(server) ? null : "did not start";
        return _tools.GetValueOrDefault(server);
    }

    /// <summary>
    /// Closes the open servers, so the next leg starts them fresh: a real session answers one
    /// question, and a server may keep state between calls (codegraph's explore leaves out source
    /// it already sent in the session). A server that did not start is not tried again.
    /// </summary>
    public void EndSession()
    {
        foreach ((string server, IMcpSession session) in _sessions)
        {
            ReadTools(server, session);
            session.Dispose();
        }

        _sessions.Clear();
    }

    /// <summary>
    /// Runs one step: once, or as a poll until its answer matches (each poll a call). A failing
    /// call is still a call, with its bytes. `${name}` in the step takes a value captured earlier
    /// into <paramref name="values"/>; this step's captures are added to it.
    /// </summary>
    public IReadOnlyList<(StepResult Result, string Answer)> Execute(Step step, Dictionary<string, string> values)
    {
        var calls = new List<(StepResult, string)>();
        while (true)
        {
            (string request, CallAnswer answer) = step switch
            {
                RunStep run => (Fill(run.Command, values), _host.Shell(Fill(run.Command, values), _project, run.Timeout)),
                ReadStep read => Read(read with { Path = Fill(read.Path, values) }),
                McpStep mcp => ("mcp__" + mcp.Server + "__" + mcp.Tool + Fill(mcp.ArgumentsJson, values), Call(mcp with { ArgumentsJson = Fill(mcp.ArgumentsJson, values) })),
                _ => throw new ArgumentException("unknown step " + step.GetType().Name, nameof(step)),
            };

            calls.Add((new StepResult(step.Label, Bytes(request), Bytes(answer.Text), Math.Round(answer.Elapsed.TotalSeconds, 2), answer.Exit, step.Redo), answer.Text));
            foreach ((string name, System.Text.RegularExpressions.Regex pattern) in step.Control.Capture)
            {
                if (pattern.Match(answer.Text) is { Success: true } m)
                {
                    values[name] = m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
                }
            }

            if (step.Control.Until is not { } until || until.IsMatch(answer.Text) || calls.Count >= step.Control.MaxPolls)
            {
                return calls;
            }

            _sleep(step.Control.Every);
        }
    }

    /// <summary>`${name}` replaced by its captured value; unknown names stay as written, so the call fails visibly.</summary>
    internal static string Fill(string text, IReadOnlyDictionary<string, string> values)
    {
        foreach ((string name, string value) in values)
        {
            text = text.Replace("${" + name + "}", value, StringComparison.Ordinal);
        }

        return text;
    }

    public void Dispose()
    {
        foreach (IMcpSession session in _sessions.Values)
        {
            session.Dispose();
        }

        _sessions.Clear();
    }

    private void ReadTools(string server, IMcpSession session)
    {
        if (_tools.ContainsKey(server) || _toolErrors.ContainsKey(server))
        {
            return;
        }

        try
        {
            _tools[server] = session.Tools();
        }
        catch (McpException e)
        {
            _toolErrors[server] = e.Message;
        }
    }

    /// <summary>
    /// The Read tool's answer: `cat -n` numbering (6 wide, a tab), `offset` 1-based, at most
    /// 2,000 lines, long lines cut at 2,000 characters.
    /// </summary>
    internal static string ReadLikeClaude(string text, int? offset, int? limit)
    {
        string[] lines = text.ReplaceLineEndings("\n").Split('\n');
        int count = text.Length == 0 ? 0 : text.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        int start = (offset ?? 1) - 1;
        if (count == 0)
        {
            return "<system-reminder>Warning: the file exists but the contents are empty.</system-reminder>";
        }

        if (start >= count)
        {
            return string.Create(CultureInfo.InvariantCulture, $"<system-reminder>Warning: the file exists but is shorter than the provided offset ({start + 1}). The file has {count} lines.</system-reminder>");
        }

        var answer = new StringBuilder();
        int end = Math.Min(count, start + (limit ?? ReadLines));
        for (int i = start; i < end; i++)
        {
            string line = lines[i];
            answer.Append((i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(6)).Append('\t')
                .Append(line.Length > ReadLineChars ? line[..ReadLineChars] : line).Append('\n');
        }

        return answer.ToString();
    }

    private (string Request, CallAnswer Answer) Read(ReadStep read)
    {
        string path = Path.GetFullPath(Path.Combine(_project, read.Path));
        var input = new Dictionary<string, object> { ["file_path"] = path };
        if (read.Offset is { } offset)
        {
            input["offset"] = offset;
        }

        if (read.Limit is { } limit)
        {
            input["limit"] = limit;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        string? text = _host.ReadFile(path);
        CallAnswer answer = text is null
            ? new CallAnswer("File does not exist.", 1, watch.Elapsed)
            : new CallAnswer(ReadLikeClaude(text, read.Offset, read.Limit), 0, watch.Elapsed);
        return (JsonSerializer.Serialize(input), answer);
    }

    private CallAnswer Call(McpStep step)
    {
        if (_startErrors.TryGetValue(step.Server, out string? failed))
        {
            return new CallAnswer(failed, 1, TimeSpan.Zero);
        }

        if (!_sessions.TryGetValue(step.Server, out IMcpSession? session))
        {
            try
            {
                session = _host.OpenMcp(_suite.Servers[step.Server], _project);
            }
            catch (Exception e) when (e is McpException or IOException or Win32Exception or InvalidOperationException)
            {
                _startErrors[step.Server] = $"MCP server {step.Server} did not start: {e.Message}";
                return new CallAnswer(_startErrors[step.Server], 1, TimeSpan.Zero);
            }

            _sessions[step.Server] = session;
        }

        return session.Call(step.Tool, step.ArgumentsJson, _mcpTimeout);
    }

    private static int Bytes(string text) => Encoding.UTF8.GetByteCount(text);
}
