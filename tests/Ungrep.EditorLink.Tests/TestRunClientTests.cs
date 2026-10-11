namespace Ungrep.EditorLink.Tests;

/// <summary>`BridgeClient.RunTests`: the wait for a test run through running answers, a domain reload, a deadline and failures.</summary>
public sealed class TestRunClientTests : IDisposable
{
    private const string Finished = "{\"state\":\"finished\",\"passed\":3,\"failed\":1,\"skipped\":0,\"inconclusive\":0,\"seconds\":1.5,\"failures\":["
        + "{\"name\":\"Game.Tests.T.Fails\",\"message\":\"Expected: 3\\n  But was: 2\",\"frame\":\"Assets/Tests/T.cs:9\"}]}";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-testrun-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<FakeBridge> _bridges = [];

    public TestRunClientTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (FakeBridge bridge in _bridges)
        {
            bridge.Dispose();
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Waits_until_the_run_is_no_longer_running()
    {
        int polls = 0;
        FakeBridge bridge = Bridge();
        using BridgeClient client = Connected(bridge, path => path.Split('?')[0] switch
        {
            "/tests" => (200, "{\"id\":\"r1\"}"),
            "/tests/result" => (200, Interlocked.Increment(ref polls) < 3 ? "{\"state\":\"running\"}" : Finished),
            _ => (200, FakeBridge.Status("idle", 1)),
        });

        TestOutcome outcome = client.RunTests("EditMode", "Fails", TimeSpan.FromSeconds(10));

        Assert.Null(outcome.Problem);
        Assert.Equal(1, outcome.Result!.Failed);
        Assert.Equal("Assets/Tests/T.cs:9", outcome.Result.Failures![0].Frame);
        Assert.Contains("/tests?platform=EditMode&filter=Fails", bridge.Requests);
        Assert.Contains("/tests/result?id=r1", bridge.Requests);
    }

    [Fact]
    public void Follows_a_domain_reload_to_a_new_port()
    {
        FakeBridge first = Bridge();
        FakeBridge second = Bridge();
        second.Handle = path => path.StartsWith("/tests/result", StringComparison.Ordinal) ? (200, Finished) : (200, FakeBridge.Status("idle", 1));
        int polls = 0;
        using BridgeClient client = Connected(first, path =>
        {
            if (path.StartsWith("/tests/result", StringComparison.Ordinal) && Interlocked.Increment(ref polls) == 2)
            {
                // A PlayMode run reloads the domain: this bridge goes, the next one comes up.
                second.Publish(_root);
                ThreadPool.QueueUserWorkItem(_ => first.Dispose());
            }

            return path.StartsWith("/tests?", StringComparison.Ordinal) ? (200, "{\"id\":\"r1\"}") : (200, "{\"state\":\"running\"}");
        });

        TestOutcome outcome = client.RunTests("PlayMode", null, TimeSpan.FromSeconds(10));

        Assert.Null(outcome.Problem);
        Assert.Equal(3, outcome.Result!.Passed);
        Assert.Equal(second.Port, client.Info.Port);
    }

    [Theory]
    [InlineData("/tests", 409, "{\"error\":\"a test run is in progress\"}", "bridge answered 409: a test run is in progress")]
    [InlineData("/tests", 501, "{\"error\":\"com.unity.test-framework is not in this project\"}", "bridge answered 501: com.unity.test-framework is not in this project")]
    [InlineData("/tests/result", 404, "{\"error\":\"no test run r1\"}", "bridge answered 404: no test run r1")]
    public void A_refusal_ends_the_wait(string failing, int status, string body, string problem)
    {
        using BridgeClient client = Connected(Bridge(), path => path.Split('?')[0] switch
        {
            "/tests" when failing == "/tests" => (status, body),
            "/tests" => (200, "{\"id\":\"r1\"}"),
            "/tests/result" => (status, body),
            _ => (200, FakeBridge.Status("idle", 1)),
        });

        TestOutcome outcome = client.RunTests("EditMode", null, TimeSpan.FromSeconds(10));

        Assert.Null(outcome.Result);
        Assert.False(outcome.TimedOut);
        Assert.Equal(problem, outcome.Problem);
    }

    [Fact]
    public void Stops_at_the_deadline()
    {
        using BridgeClient client = Connected(Bridge(), path => path.StartsWith("/tests?", StringComparison.Ordinal)
            ? (200, "{\"id\":\"r1\"}")
            : path.StartsWith("/tests/result", StringComparison.Ordinal) ? (200, "{\"state\":\"running\"}") : (200, FakeBridge.Status("idle", 1)));

        TestOutcome outcome = client.RunTests("EditMode", null, TimeSpan.FromMilliseconds(300));

        Assert.True(outcome.TimedOut);
        Assert.Equal("Unity was still running tests after 0.3 s", outcome.Problem);
    }

    [Fact]
    public void Stops_when_the_editor_exits()
    {
        bool alive = true;
        FakeBridge bridge = Bridge();
        bridge.Publish(_root, pid: 9);
        bridge.Handle = path =>
        {
            if (path.StartsWith("/tests/result", StringComparison.Ordinal))
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    alive = false;
                    bridge.Dispose();
                });
                return (200, "{\"state\":\"running\"}");
            }

            return path.StartsWith("/tests?", StringComparison.Ordinal) ? (200, "{\"id\":\"r1\"}") : (200, FakeBridge.Status("idle", 1));
        };
        using BridgeClient client = BridgeClient.Connect(_root, _ => alive).Client!;
        client.PollInterval = TimeSpan.FromMilliseconds(20);

        TestOutcome outcome = client.RunTests("PlayMode", null, TimeSpan.FromSeconds(10));

        Assert.False(outcome.TimedOut);
        Assert.Equal("Unity (pid 9) exited during the test run", outcome.Problem);
    }

    private FakeBridge Bridge()
    {
        var bridge = new FakeBridge();
        _bridges.Add(bridge);
        return bridge;
    }

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
