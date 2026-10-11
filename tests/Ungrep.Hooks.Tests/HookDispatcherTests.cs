using System.Text;
using System.Text.Json;
using Ungrep.Core;

namespace Ungrep.Hooks.Tests;

/// <summary>
/// `ug hook` dispatch on hook payloads (Payloads/, the shape Claude Code sends) in a small Unity
/// project built in a temp folder, and the fail-open rules: exit 0, no output, one log line.
/// </summary>
public sealed class HookDispatcherTests : IDisposable
{
    private static readonly DateTimeOffset _now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-hooks-" + Guid.NewGuid().ToString("N"));

    public HookDispatcherTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Assets"));
        Directory.CreateDirectory(Path.Combine(_root, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_root, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.3.14f1\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string ErrorLog => Path.Combine(_root, HookDispatcher.ErrorLogPath);

    /// <summary>The payload file with its cwd and paths pointing into the temp project.</summary>
    private string Payload(string name)
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Payloads", name + ".json"));
        string root = JsonEncodedText.Encode(_root.Replace('\\', '/')).ToString();
        return json.Replace("<root>", root, StringComparison.Ordinal);
    }

    private (int ExitCode, string Output) Run(string eventName, string payload, TimeSpan? timeout = null, params IHookHandler[] handlers)
    {
        var output = new StringWriter();
        int exitCode = new HookDispatcher(handlers, timeout, () => _now).Run(eventName, new StringReader(payload), output, _root);
        return (exitCode, output.ToString());
    }

    [Fact]
    public void Dispatches_recorded_payloads()
    {
        var text = new StringBuilder();
        foreach (string name in new[] { "PreToolUse.Grep", "PreToolUse.Read", "PreToolUse.Edit", "PreToolUse.Bash", "PostToolUse.Edit", "Stop" })
        {
            string eventName = name.Split('.')[0];
            var (exitCode, output) = Run(eventName, Payload(name), null, new DescribeHandler());
            Assert.Equal(0, exitCode);
            text.Append(name).Append(": ").Append(output.Replace(_root.Replace('\\', '/'), "<root>", StringComparison.Ordinal)).Append('\n');
        }

        Assert.False(File.Exists(ErrorLog));
        Golden.Match(text.ToString());
    }

    [Fact]
    public void Silent_without_handlers()
    {
        Assert.Equal((0, ""), Run("PreToolUse", Payload("PreToolUse.Edit")));
        Assert.False(File.Exists(ErrorLog));
    }

    [Fact]
    public void Joins_the_output_of_several_handlers_and_skips_silent_ones()
    {
        var (_, output) = Run("Stop", Payload("Stop"), null,
            new FixedHandler("guards", "first"), new FixedHandler("viewer", null), new FixedHandler("unity-loop", "second"));

        Assert.Equal("first\nsecond", output);
    }

    [Fact]
    public void Disabled_modules_are_skipped()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".claude"));
        File.WriteAllText(Path.Combine(_root, UngrepConfig.RelativePath), """{ "modules": { "guards": false } }""");

        var (_, output) = Run("Stop", Payload("Stop"), null, new FixedHandler("guards", "off"), new FixedHandler("unity-loop", "on"));

        Assert.Equal("on", output);
    }

    [Fact]
    public void Throwing_handler_exits_0_silently_and_logs_one_line()
    {
        var (exitCode, output) = Run("PreToolUse", Payload("PreToolUse.Edit"), null,
            new FixedHandler("unity-loop", "never shown"), new ThrowingHandler());

        Assert.Equal((0, ""), (exitCode, output));
        Assert.Equal("2026-09-29T12:00:00Z PreToolUse guards: InvalidOperationException: broken handler line two\n", File.ReadAllText(ErrorLog));
    }

    [Fact]
    public void Slow_handler_times_out_silently()
    {
        // Long enough for the dispatcher to find the project first, even on a slow CI runner: a timeout
        // before that has no project to log to.
        var (exitCode, output) = Run("Stop", Payload("Stop"), TimeSpan.FromSeconds(1), new SlowHandler());

        Assert.Equal((0, ""), (exitCode, output));
        Assert.Equal("2026-09-29T12:00:00Z Stop timed out after 1 s\n", File.ReadAllText(ErrorLog));
    }

    [Fact]
    public void Bad_payload_is_logged_against_the_fallback_folder()
    {
        var (exitCode, output) = Run("Stop", "{ not json", null, new FixedHandler("guards", "x"));

        Assert.Equal((0, ""), (exitCode, output));
        Assert.StartsWith("2026-09-29T12:00:00Z Stop hook payload is not valid JSON: ", File.ReadAllText(ErrorLog), StringComparison.Ordinal);
    }

    [Fact]
    public void Bad_config_is_logged()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".claude"));
        File.WriteAllText(Path.Combine(_root, UngrepConfig.RelativePath), """{ "answerBudget": 1 }""");

        Assert.Equal((0, ""), Run("Stop", Payload("Stop"), null, new FixedHandler("guards", "x")));
        Assert.StartsWith("2026-09-29T12:00:00Z Stop answerBudget must be", File.ReadAllText(ErrorLog), StringComparison.Ordinal);
    }

    [Fact]
    public void Outside_a_unity_project_nothing_runs_or_logs()
    {
        string payload = Payload("Stop").Replace(JsonEncodedText.Encode(_root.Replace('\\', '/')).ToString(), "/nowhere", StringComparison.Ordinal);
        var outside = new StringWriter();
        string elsewhere = Path.GetTempPath();

        int exitCode = new HookDispatcher([new ThrowingHandler()], null, () => _now).Run("Stop", new StringReader(payload), outside, elsewhere);

        Assert.Equal((0, ""), (exitCode, outside.ToString()));
        Assert.False(File.Exists(ErrorLog));
    }

    [Fact]
    public void Error_log_rolls_over_past_the_size_cap()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ErrorLog)!);
        File.WriteAllText(ErrorLog, new string('x', (int)HookDispatcher.MaxErrorLogBytes + 1));

        Run("Stop", Payload("Stop"), null, new ThrowingHandler());

        Assert.True(File.Exists(ErrorLog + ".1"));
        Assert.StartsWith("2026-09-29T12:00:00Z Stop guards:", File.ReadAllText(ErrorLog), StringComparison.Ordinal);
    }

    /// <summary>Says what the dispatcher handed it, so the golden file shows the parsed payload.</summary>
    private sealed class DescribeHandler : IHookHandler
    {
        public string Module => "guards";

        public bool Handles(HookInput input) => true;

        public string? Handle(HookInput input, UnityProject project, CancellationToken cancel)
        {
            string detail = input.ToolInput.ValueKind != JsonValueKind.Object ? "-"
                : input.ToolInput.TryGetProperty("file_path", out JsonElement file) ? "file " + file.GetString()
                : input.ToolInput.TryGetProperty("command", out JsonElement command) ? "command " + command.GetString()
                : input.ToolInput.TryGetProperty("pattern", out JsonElement pattern) ? "pattern " + pattern.GetString()
                : "?";
            return $"{input.EventName} tool={input.ToolName ?? "-"} {detail} project={(project.Root == Path.GetFullPath(input.Cwd!) ? "cwd" : project.Root)}";
        }
    }

    private sealed class FixedHandler(string module, string? output) : IHookHandler
    {
        public string Module => module;

        public bool Handles(HookInput input) => true;

        public string? Handle(HookInput input, UnityProject project, CancellationToken cancel) => output;
    }

    private sealed class ThrowingHandler : IHookHandler
    {
        public string Module => "guards";

        public bool Handles(HookInput input) => true;

        public string? Handle(HookInput input, UnityProject project, CancellationToken cancel) =>
            throw new InvalidOperationException("broken handler\nline two");
    }

    private sealed class SlowHandler : IHookHandler
    {
        public string Module => "unity-loop";

        public bool Handles(HookInput input) => true;

        public string? Handle(HookInput input, UnityProject project, CancellationToken cancel)
        {
            cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
            return "too late";
        }
    }
}
