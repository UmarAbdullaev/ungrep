namespace UnityGraph.EditorLink.Tests;

/// <summary>
/// The client against a fake bridge: discovery and its failures, requests, and the compile wait
/// through a domain reload, a deadline and an editor that exits.
/// </summary>
public sealed class BridgeClientTests : IDisposable
{
    private const string OneError = "{\"compile\":4,\"started\":\"2026-09-29T14:00:00Z\",\"finished\":\"2026-09-29T14:00:04Z\",\"messages\":["
        + "{\"assembly\":\"Assembly-CSharp\",\"file\":\"Assets/Scripts/Turntable.cs\",\"line\":17,\"column\":9,\"severity\":\"error\",\"code\":\"CS0103\",\"text\":\"The name 'spd' does not exist in the current context\"}]}";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-link-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<FakeBridge> _bridges = [];

    public BridgeClientTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (FakeBridge bridge in _bridges)
        {
            bridge.Dispose();
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void No_bridge_json()
    {
        BridgeConnection connection = BridgeClient.Connect(_root, _ => true);

        Assert.Null(connection.Client);
        Assert.Equal("no bridge.json: Unity is closed or the bridge is not installed", connection.Problem);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"bridge\":\"0.1.0\",\"port\":0,\"token\":\"t\",\"pid\":1,\"unity\":\"u\"}")]
    [InlineData("null")]
    public void Unreadable_bridge_json(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BridgeClient.DiscoveryPath(_root))!);
        File.WriteAllText(BridgeClient.DiscoveryPath(_root), json);

        Assert.Equal("bridge.json unreadable", BridgeClient.Connect(_root, _ => true).Problem);
    }

    [Fact]
    public void Stale_bridge_json()
    {
        Bridge().Publish(_root, pid: 77);

        Assert.Equal("stale bridge.json: Unity (pid 77) is not running", BridgeClient.Connect(_root, pid => pid != 77).Problem);
    }

    [Fact]
    public void Older_bridge()
    {
        Bridge().Publish(_root, bridge: "0.0.9");

        Assert.Equal("bridge 0.0.9 is older than 0.1.0: run `ug bridge install` again", BridgeClient.Connect(_root, _ => true).Problem);
    }

    [Fact]
    public void Wrong_token()
    {
        FakeBridge bridge = Bridge();
        bridge.Handle = _ => (401, "{\"error\":\"missing or wrong X-UG-Token\"}");
        bridge.Publish(_root);

        Assert.Equal("the bridge refused the token (401): bridge.json is out of date", BridgeClient.Connect(_root, _ => true).Problem);
    }

    [Fact]
    public void Refused_while_reloading()
    {
        FakeBridge bridge = Bridge();
        bridge.Publish(_root);
        bridge.Dispose();

        Assert.Equal("refused: Unity is starting or reloading scripts", BridgeClient.Connect(_root, _ => true).Problem);
    }

    [Fact]
    public void Connects_and_reads_status_with_the_token()
    {
        FakeBridge bridge = Bridge();
        bridge.Handle = _ => (200, FakeBridge.Status("idle", 3, errors: 1));
        bridge.Publish(_root);

        BridgeConnection connection = BridgeClient.Connect(_root, _ => true);
        using BridgeClient client = connection.Client!;

        Assert.Null(connection.Problem);
        Assert.Equal(new BridgeStatus("idle", "6000.3.14f1", "0.1.0", 3, 1, 0, 7), connection.Status);
        Assert.Equal(bridge.Port, client.Info.Port);
        Assert.Equal([FakeBridge.Token], bridge.Tokens);
    }

    [Fact]
    public void Requests_escape_their_parameters_and_parse_answers()
    {
        FakeBridge bridge = Bridge();
        bridge.Handle = path => path.Split('?')[0] switch
        {
            "/status" => (200, FakeBridge.Status("idle", 3)),
            "/run" => (200, "{\"ran\":true,\"result\":null,\"error\":\"System.InvalidOperationException: boom\",\"frame\":\"Assets/Editor/Tools.cs:12\",\"ms\":4.2}"),
            "/console" => (200, "{\"last\":9,\"entries\":[{\"id\":9,\"type\":\"error\",\"text\":\"x\",\"frame\":null,\"count\":2}]}"),
            "/tests" => (200, "{\"id\":\"run-1\"}"),
            "/tests/result" => (200, "{\"state\":\"finished\",\"passed\":3,\"failed\":1,\"skipped\":0,\"inconclusive\":0,\"seconds\":0.5,\"failures\":[{\"name\":\"A.B\",\"message\":\"m\",\"frame\":null}]}"),
            _ => (404, "{\"error\":\"unknown request " + path + "\"}"),
        };
        bridge.Publish(_root);
        using BridgeClient client = BridgeClient.Connect(_root, _ => true).Client!;

        RunOutcome run = client.Run("Tools/Build Lab & Go", TimeSpan.FromSeconds(5));
        ConsolePage page = client.Console(4, 10, "error,warning");
        ConsolePage all = client.Console(0, 50, null);
        string id = client.StartTests("EditMode", "Game.Tests");
        string unfiltered = client.StartTests("PlayMode", null);
        TestRunResult result = client.TestResult("run 1");

        Assert.Equal(new RunOutcome(true, null, "System.InvalidOperationException: boom", "Assets/Editor/Tools.cs:12", 4.2), run);
        Assert.Equal(2, page.Entries.Single().Count);
        Assert.Equal(9, all.Last);
        Assert.Equal("run-1", id);
        Assert.Equal("run-1", unfiltered);
        Assert.Equal((3, 1, "A.B"), (result.Passed, result.Failed, result.Failures!.Single().Name));
        Assert.False(result.IsRunning);
        Assert.Equal(
            [
                "/status",
                "/run?target=Tools%2FBuild%20Lab%20%26%20Go",
                "/console?after=4&limit=10&types=error%2Cwarning",
                "/console?after=0&limit=50",
                "/tests?platform=EditMode&filter=Game.Tests",
                "/tests?platform=PlayMode",
                "/tests/result?id=run%201",
            ],
            bridge.Requests);
    }

    [Theory]
    [InlineData(404, "{\"error\":\"unknown request /x\"}", "bridge answered 404: unknown request /x")]
    [InlineData(503, "<h1>busy</h1>", "bridge answered 503")]
    [InlineData(200, "[1,", "unreadable answer from the bridge")]
    [InlineData(200, "null", "empty answer from the bridge")]
    public void Bad_answers_are_reasons(int status, string body, string reason)
    {
        using BridgeClient client = Connected(path => path == "/status" ? (200, FakeBridge.Status("idle", 3)) : (status, body));

        var e = Assert.Throws<BridgeUnavailableException>(() => client.LastCompile());

        Assert.StartsWith(reason, e.Message);
    }

    [Fact]
    public void A_start_without_an_id_is_a_reason()
    {
        using BridgeClient client = Connected(path => path == "/status" ? (200, FakeBridge.Status("idle", 3)) : (200, "{}"));

        Assert.Equal("the bridge gave no test run id", Assert.Throws<BridgeUnavailableException>(() => client.StartTests("EditMode", null)).Message);
    }

    [Fact]
    public void A_busy_editor_times_out()
    {
        using BridgeClient client = Connected(path =>
        {
            if (path == "/refresh")
            {
                Thread.Sleep(1500);
            }

            return (200, FakeBridge.Status("idle", 3));
        });

        var e = Assert.Throws<BridgeUnavailableException>(() => client.Refresh(TimeSpan.FromMilliseconds(200)));

        Assert.Equal("no answer within 0.2 s: Unity is busy", e.Message);
    }

    [Fact]
    public void Compile_without_changes_answers_the_last_compile()
    {
        using BridgeClient client = Connected(path => path switch
        {
            "/refresh" => (200, "{\"compiling\":false,\"compile\":4}"),
            "/compile" => (200, OneError),
            _ => (200, FakeBridge.Status("idle", 4, errors: 1)),
        });

        CompileOutcome outcome = client.Compile(TimeSpan.FromSeconds(5));

        Assert.False(outcome.Changed);
        Assert.Null(outcome.Problem);
        Assert.Equal("CS0103", outcome.Result!.Messages.Single().Code);
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 14, 0, 4, TimeSpan.Zero), outcome.Result.Finished);
    }

    [Fact]
    public void Compile_waits_for_the_counter_and_idle()
    {
        int polls = 0;
        FakeBridge bridge = Bridge();
        using BridgeClient client = Connected(bridge, path => path switch
        {
            "/refresh" => (200, "{\"compiling\":true,\"compile\":3}"),
            "/compile" => (200, OneError),
            _ => Interlocked.Increment(ref polls) switch
            {
                1 => (200, FakeBridge.Status("idle", 3)),      // Connect
                2 => (200, FakeBridge.Status("compiling", 3)),
                3 => (200, FakeBridge.Status("compiling", 4)), // counted, not yet idle
                _ => (200, FakeBridge.Status("idle", 4, errors: 1)),
            },
        });

        CompileOutcome outcome = client.Compile(TimeSpan.FromSeconds(10));

        Assert.True(outcome.Changed);
        Assert.Null(outcome.Problem);
        Assert.Equal(4, outcome.Result!.Compile);
        Assert.Equal(4, polls);
        Assert.Equal("/compile", bridge.Requests.Last());
    }

    [Fact]
    public void Compile_follows_a_domain_reload_to_a_new_port()
    {
        FakeBridge before = Bridge();
        FakeBridge after = Bridge();
        after.Handle = path => path == "/compile" ? (200, "{\"compile\":4,\"messages\":[]}") : (200, FakeBridge.Status("idle", 4));
        using BridgeClient client = Connected(before, path =>
        {
            if (path != "/refresh")
            {
                return (200, FakeBridge.Status("idle", 3));
            }

            // The successful compile reloads the domain: this bridge goes, the next one comes up.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(50);
                before.Dispose();
                Thread.Sleep(100);
                after.Publish(_root);
            });
            return (200, "{\"compiling\":true,\"compile\":3}");
        });

        CompileOutcome outcome = client.Compile(TimeSpan.FromSeconds(10));

        Assert.True(outcome.Changed, outcome.Problem);
        Assert.Equal(4, outcome.Result!.Compile);
        Assert.Equal(after.Port, client.Info.Port);
    }

    [Fact]
    public void Compile_stops_at_the_deadline()
    {
        using BridgeClient client = Connected(path => path == "/refresh"
            ? (200, "{\"compiling\":true,\"compile\":3}")
            : (200, FakeBridge.Status("compiling", 3)));

        CompileOutcome outcome = client.Compile(TimeSpan.FromMilliseconds(300));

        Assert.True(outcome.TimedOut);
        Assert.Null(outcome.Result);
        Assert.Equal("Unity did not finish compiling within 0.3 s", outcome.Problem);
    }

    [Fact]
    public void Compile_stops_when_the_editor_exits()
    {
        bool alive = true;
        FakeBridge bridge = Bridge();
        bridge.Publish(_root, pid: 9);
        bridge.Handle = path =>
        {
            if (path == "/refresh")
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Thread.Sleep(50);
                    alive = false;
                    bridge.Dispose();
                });
                return (200, "{\"compiling\":true,\"compile\":3}");
            }

            return (200, FakeBridge.Status("idle", 3));
        };
        using BridgeClient client = BridgeClient.Connect(_root, _ => alive).Client!;
        client.PollInterval = TimeSpan.FromMilliseconds(20);

        CompileOutcome outcome = client.Compile(TimeSpan.FromSeconds(10));

        Assert.False(outcome.TimedOut);
        Assert.Equal("Unity (pid 9) exited during the compile", outcome.Problem);
    }

    [Fact]
    public void Compile_reports_a_refresh_that_fails()
    {
        using BridgeClient client = Connected(path => path == "/refresh"
            ? (503, "{\"error\":\"the bridge is stopping (domain reload or quit)\"}")
            : (200, FakeBridge.Status("idle", 3)));

        Assert.Equal("bridge answered 503: the bridge is stopping (domain reload or quit)", client.Compile(TimeSpan.FromSeconds(5)).Problem);
    }

    [Fact]
    public void Compile_reports_a_lost_answer()
    {
        using BridgeClient client = Connected(path => path switch
        {
            "/refresh" => (200, "{\"compiling\":false,\"compile\":3}"),
            "/compile" => (500, "{\"error\":\"boom\"}"),
            _ => (200, FakeBridge.Status("idle", 3)),
        });

        Assert.Equal("bridge answered 500: boom", client.Compile(TimeSpan.FromSeconds(5)).Problem);
    }

    [Fact]
    public void Only_live_unity_processes_count()
    {
        Assert.False(BridgeClient.IsUnityRunning(int.MaxValue));
        Assert.Equal(
            System.Diagnostics.Process.GetCurrentProcess().ProcessName.StartsWith("Unity", StringComparison.OrdinalIgnoreCase),
            BridgeClient.IsUnityRunning(Environment.ProcessId));
    }

    private FakeBridge Bridge()
    {
        var bridge = new FakeBridge();
        _bridges.Add(bridge);
        return bridge;
    }

    private BridgeClient Connected(Func<string, (int, string)> handle) => Connected(Bridge(), handle);

    private BridgeClient Connected(FakeBridge bridge, Func<string, (int, string)> handle)
    {
        bridge.Handle = handle;
        bridge.Publish(_root);
        BridgeConnection connection = BridgeClient.Connect(_root, _ => true);
        BridgeClient client = connection.Client ?? throw new InvalidOperationException(connection.Problem);
        client.PollInterval = TimeSpan.FromMilliseconds(20);
        return client;
    }
}
