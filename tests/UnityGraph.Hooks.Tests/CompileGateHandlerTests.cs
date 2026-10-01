using System.Text.Json;
using UnityGraph.EditorLink;

namespace UnityGraph.Hooks.Tests;

/// <summary>The compile gate on recorded PostToolUse and Stop payloads: what marks a session, and when Stop speaks.</summary>
public sealed class CompileGateHandlerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-gate-hooks-" + Guid.NewGuid().ToString("N"));
    private readonly List<(string Root, TimeSpan Deadline)> _checks = [];
    private GateAnswer _answer = new(GateResult.Clean);

    public CompileGateHandlerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Assets"));
        Directory.CreateDirectory(Path.Combine(_root, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_root, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.3.14f1\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Marker => Path.Combine(_root, "Library", "unitygraph", "gate-3f2a9c1e-5b7d-4e8a-9c0f-1a2b3c4d5e6f");

    [Theory]
    [InlineData("Assets/Scripts/PlayerHealth.cs", true)]
    [InlineData("Assets/Scripts/Game.asmdef", true)]
    [InlineData("Packages/com.game.core/Runtime/Core.CS", true)]
    [InlineData("Assets/Scripts/notes.md", false)]
    [InlineData("Assets/Scenes/Main.unity", false)]
    [InlineData("Library/PackageCache/x/Y.cs", false)]
    [InlineData("../elsewhere/Tool.cs", false)]
    public void An_edited_script_marks_the_session_and_nothing_is_printed(string file, bool marks)
    {
        string payload = Payload("PostToolUse.Edit").Replace("Assets/Scripts/PlayerHealth.cs", file, StringComparison.Ordinal);

        var (exitCode, output) = Run("PostToolUse", payload);

        Assert.Equal(0, exitCode);
        Assert.Equal("", output);
        Assert.Equal(marks, File.Exists(Marker));
        Assert.Empty(_checks);
        if (marks)
        {
            Assert.Equal(file.Replace('\\', '/') + "\n", File.ReadAllText(Marker));
        }
    }

    [Fact]
    public void A_path_outside_the_project_does_not_mark()
    {
        string outside = Path.Combine(Path.GetTempPath(), "Other.cs").Replace('\\', '/');
        string payload = Payload("PostToolUse.Edit").Replace(_root.Replace('\\', '/') + "/Assets/Scripts/PlayerHealth.cs", outside, StringComparison.Ordinal);

        Run("PostToolUse", payload);

        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public void Stop_without_an_edit_is_silent_and_asks_nothing()
    {
        var (exitCode, output) = Run("Stop", Payload("Stop"));

        Assert.Equal(0, exitCode);
        Assert.Equal("", output);
        Assert.Empty(_checks);
    }

    [Fact]
    public void Stop_after_an_edit_with_errors_blocks_once_with_the_errors()
    {
        _answer = new GateAnswer(GateResult.Errors, "compiled in 1.2 s: 1 error\n  Assets/Scripts/PlayerHealth.cs:9:5  CS0103  The name 'x' does not exist");
        Run("PostToolUse", Payload("PostToolUse.Edit"));

        string output = Run("Stop", Payload("Stop")).Output;

        using JsonDocument json = JsonDocument.Parse(output);
        Assert.Equal("block", json.RootElement.GetProperty("decision").GetString());
        Assert.Equal(_answer.Text, json.RootElement.GetProperty("reason").GetString());
        Assert.Equal([(_root, CompileGateHandler.Deadline)], _checks);
        Assert.False(File.Exists(Marker));

        // Claude fixed nothing and stops again: no loop.
        Assert.Equal("", Run("Stop", Payload("Stop")).Output);
        Assert.Single(_checks);
    }

    [Theory]
    [InlineData(GateResult.Clean)]
    [InlineData(GateResult.Unreachable)]
    public void Stop_is_silent_when_clean_or_unity_is_out_of_reach(GateResult result)
    {
        _answer = new GateAnswer(result);
        Run("PostToolUse", Payload("PostToolUse.Edit"));

        Assert.Equal("", Run("Stop", Payload("Stop")).Output);
        Assert.Single(_checks);
        Assert.False(File.Exists(Marker));
        Assert.False(File.Exists(Path.Combine(_root, HookDispatcher.ErrorLogPath)));
    }

    [Fact]
    public void A_playing_editor_keeps_the_mark_for_the_next_stop()
    {
        _answer = new GateAnswer(GateResult.Playing);
        Run("PostToolUse", Payload("PostToolUse.Edit"));

        Assert.Equal("", Run("Stop", Payload("Stop")).Output);
        Assert.True(File.Exists(Marker));
    }

    [Fact]
    public void The_module_and_editor_link_switches_turn_it_off()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".claude"));
        File.WriteAllText(Path.Combine(_root, ".claude", "unitygraph.json"), "{\"modules\":{\"unity-loop\":false}}");
        Run("PostToolUse", Payload("PostToolUse.Edit"));
        Assert.False(File.Exists(Marker));

        File.WriteAllText(Path.Combine(_root, ".claude", "unitygraph.json"), "{\"modules\":{\"editor-link\":false}}");
        _answer = new GateAnswer(GateResult.Errors, "boom");
        Run("PostToolUse", Payload("PostToolUse.Edit"));
        Assert.Equal("", Run("Stop", Payload("Stop")).Output);
        Assert.Empty(_checks);
    }

    [Fact]
    public void A_throwing_check_fails_open_and_is_logged()
    {
        Run("PostToolUse", Payload("PostToolUse.Edit"));
        var output = new StringWriter();
        var dispatcher = new HookDispatcher([new CompileGateHandler((_, _) => throw new InvalidOperationException("bridge exploded"))]);

        int exitCode = dispatcher.Run("Stop", new StringReader(Payload("Stop")), output, _root);

        Assert.Equal(0, exitCode);
        Assert.Equal("", output.ToString());
        Assert.Contains("unity-loop: InvalidOperationException: bridge exploded", File.ReadAllText(Path.Combine(_root, HookDispatcher.ErrorLogPath)), StringComparison.Ordinal);
    }

    [Fact]
    public void Stop_gets_a_longer_limit_than_the_tool_hooks()
    {
        Assert.Equal(TimeSpan.FromSeconds(40), HookDispatcher.StopTimeout);
        Assert.True(HookDispatcher.StopTimeout > CompileGateHandler.Deadline);
        Assert.True(HookDispatcher.StopTimeout > HookDispatcher.DefaultTimeout);
    }

    private string Payload(string name)
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Payloads", name + ".json"));
        return json.Replace("<root>", JsonEncodedText.Encode(_root.Replace('\\', '/')).ToString(), StringComparison.Ordinal);
    }

    private (int ExitCode, string Output) Run(string eventName, string payload)
    {
        var handler = new CompileGateHandler((root, deadline) =>
        {
            _checks.Add((root, deadline));
            return _answer;
        });
        var output = new StringWriter();
        int exitCode = new HookDispatcher([handler]).Run(eventName, new StringReader(payload), output, _root);
        return (exitCode, output.ToString());
    }
}
