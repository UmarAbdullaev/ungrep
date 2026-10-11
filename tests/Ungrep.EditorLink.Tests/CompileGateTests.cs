namespace Ungrep.EditorLink.Tests;

/// <summary>`CompileGate.Check` against a fake bridge: errors, clean, playing, and every way Unity is out of reach.</summary>
public sealed class CompileGateTests : IDisposable
{
    private const string OneError = "{\"compile\":4,\"started\":\"2026-09-29T14:00:00Z\",\"finished\":\"2026-09-29T14:00:04Z\",\"messages\":["
        + "{\"assembly\":\"Assembly-CSharp\",\"file\":\"Assets/Scripts/Turntable.cs\",\"line\":17,\"column\":9,\"severity\":\"error\",\"code\":\"CS0103\",\"text\":\"The name 'spd' does not exist in the current context\"}]}";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-gate-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeBridge _bridge = new();

    public CompileGateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _bridge.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Errors_are_the_compile_text()
    {
        Serve(path => path.Split('?')[0] switch
        {
            "/refresh" => (200, "{\"compiling\":false,\"compile\":4}"),
            "/compile" => (200, OneError),
            _ => (200, FakeBridge.Status("idle", 4, errors: 1)),
        });

        GateAnswer answer = CompileGate.Check(_root, TimeSpan.FromSeconds(5), _ => true);

        Assert.Equal(GateResult.Errors, answer.Result);
        Assert.Equal("no script changes: 1 error\n  Assets/Scripts/Turntable.cs:17:9  CS0103  The name 'spd' does not exist in the current context", answer.Text);
    }

    [Fact]
    public void A_clean_compile_is_clean()
    {
        Serve(path => path.Split('?')[0] switch
        {
            "/refresh" => (200, "{\"compiling\":false,\"compile\":4}"),
            "/compile" => (200, "{\"compile\":4,\"messages\":[{\"file\":\"Assets/A.cs\",\"line\":1,\"column\":1,\"severity\":\"warning\",\"code\":\"CS0168\",\"text\":\"unused\"}]}"),
            _ => (200, FakeBridge.Status("idle", 4)),
        });

        Assert.Equal(GateResult.Clean, CompileGate.Check(_root, TimeSpan.FromSeconds(5), _ => true).Result);
    }

    [Theory]
    [InlineData("playing")]
    [InlineData("paused")]
    public void A_playing_editor_is_not_refreshed(string state)
    {
        Serve(_ => (200, FakeBridge.Status(state, 4)));

        Assert.Equal(GateResult.Playing, CompileGate.Check(_root, TimeSpan.FromSeconds(5), _ => true).Result);
        Assert.DoesNotContain("/refresh", _bridge.Requests);
    }

    [Fact]
    public void No_bridge_a_dead_editor_an_old_bridge_and_a_timeout_are_unreachable()
    {
        Assert.Equal(GateResult.Unreachable, CompileGate.Check(_root, TimeSpan.FromSeconds(1), _ => true).Result);

        Serve(_ => (200, FakeBridge.Status("idle", 4)));
        Assert.Equal(GateResult.Unreachable, CompileGate.Check(_root, TimeSpan.FromSeconds(1), _ => false).Result);

        _bridge.Publish(_root, bridge: "0.0.9");
        Assert.Equal(GateResult.Unreachable, CompileGate.Check(_root, TimeSpan.FromSeconds(1), _ => true).Result);

        Serve(path => path == "/refresh" ? (200, "{\"compiling\":true,\"compile\":4}") : (200, FakeBridge.Status("compiling", 4)));
        Assert.Equal(GateResult.Unreachable, CompileGate.Check(_root, TimeSpan.FromMilliseconds(400), _ => true).Result);
    }

    private void Serve(Func<string, (int, string)> handle)
    {
        _bridge.Handle = handle;
        _bridge.Publish(_root);
    }
}
