using UnityGraph.EditorLink.Tests;

namespace UnityGraph.Cli.Tests;

/// <summary>
/// `ug play --paused` and `ug play --pause-on-error` (SPEC-editor-ops, Play-mode debugging) against a
/// fake bridge: the requests, the state lines printed after the wait, the `--for` poll, old bridges.
/// </summary>
[Collection(nameof(EditorCommandTests))]
public sealed class PlayDebugCommandTests : LoopFixture
{
    private const string Session =
        "{\"session\":{\"start\":\"2026-10-01T14:02:11.000Z\",\"end\":\"2026-10-01T14:02:13.000Z\",\"after\":4001,\"until\":4120,\"events\":[]}}";

    private const string Caught =
        "{\"lines\":[\"paused at the first error, frame 214, 3.5 s in\",\"  #4105 error  NullReferenceException: Object reference not set  Assets/Scripts/Enemy.cs:42\",\"  more  whole: ug console --id 4105\"]}";

    [Fact]
    public void Paused_waits_for_play_mode_then_prints_the_frame_and_what_loaded()
    {
        int polls = 0;
        Serve(path => path switch
        {
            "/play?paused=true" => (200, "{\"lines\":[\"playing\"],\"wait\":\"playing\"}"),
            "/play/state" => (200, "{\"lines\":[\"paused on frame 1\",\"  loaded: Boot (active, 3 roots, 41 objects), DontDestroyOnLoad (2 roots, 9 objects); 1 error\",\"  error  boom  Assets/Boot.cs:12\"]}"),
            "/status" => (200, FakeBridge.Status(Interlocked.Increment(ref polls) < 3 ? "idle" : "paused", 3)),
            _ => null,
        });

        var (exitCode, output) = Run("play", "--paused");

        Assert.Equal(0, exitCode);
        Assert.Matches(@"^paused on frame 1 \(entered in \d+\.\d s\)\n  loaded: Boot \(active, 3 roots, 41 objects\), DontDestroyOnLoad \(2 roots, 9 objects\); 1 error\n  error  boom  Assets/Boot\.cs:12\n$", output);
        Assert.Equal(["/play?paused=true", "/play/state"], Bridge.Requests.Where(r => r.StartsWith("/play", StringComparison.Ordinal)));
    }

    [Fact]
    public void Pause_on_error_while_playing_arms_and_prints_the_state_without_a_wait()
    {
        Serve(path => path switch
        {
            "/play?onerror=true" => (200, "{\"lines\":[\"playing\"]}"),
            "/play/state" => (200, "{\"lines\":[\"playing on frame 812; pauses at the first error\",\"  loaded: Main (active, 4 roots, 20 objects)\"]}"),
            _ => null,
        });

        Assert.Equal("playing on frame 812; pauses at the first error\n  loaded: Main (active, 4 roots, 20 objects)\n", Run("play", "--pause-on-error").Output);
    }

    [Fact]
    public void Both_flags_go_in_one_request_and_a_caught_error_is_printed_with_its_id()
    {
        Serve(path => path switch
        {
            "/play?paused=true&onerror=true" => (200, "{\"lines\":[\"paused\"]}"),
            "/play/state" => (200, Caught),
            _ => null,
        });

        Assert.Equal(
            "paused at the first error, frame 214, 3.5 s in\n  #4105 error  NullReferenceException: Object reference not set  Assets/Scripts/Enemy.cs:42\n  more  whole: ug console --id 4105\n",
            Run("play", "--paused", "--pause-on-error").Output);
    }

    [Fact]
    public void A_refused_play_or_state_is_printed_and_nothing_follows()
    {
        Serve(path => path switch
        {
            "/play?paused=true" => (200, "{\"error\":\"already playing: ug pause pauses here; ug stop first to start again on frame 1\"}"),
            _ => null,
        });
        Assert.Equal("already playing: ug pause pauses here; ug stop first to start again on frame 1\n", Run("play", "--paused").Output);
        Assert.DoesNotContain("/play/state", Bridge.Requests);

        Serve(path => path switch
        {
            "/play?onerror=true" => (200, "{\"lines\":[\"playing\"]}"),
            "/play/state" => (200, "{\"error\":\"not playing: ug play first\"}"),
            _ => null,
        });
        Assert.Equal("not playing: ug play first\n", Run("play", "--pause-on-error").Output);
    }

    [Theory]
    [InlineData("--paused")]
    [InlineData("--pause-on-error")]
    public void An_older_bridge_refuses_the_flags_by_name_before_any_request(string flag)
    {
        Serve(_ => null, bridge: "0.12.0");

        Assert.Equal("bridge 0.12.0 is too old for ug play " + flag + ": run ug bridge install again\n", Run("play", flag).Output);
        Assert.Equal("bridge 0.12.0 is too old for ug play --pause-on-error: run ug bridge install again\n", Run("play", "--for", "2", "--pause-on-error").Output);
        Assert.DoesNotContain(Bridge.Requests, r => r.StartsWith("/play", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("--paused and --for cannot be used together\n", "--paused", "--for", "2")]
    [InlineData("--step cannot be used with --paused or --pause-on-error\n", "--step", "--paused")]
    [InlineData("--step cannot be used with --paused or --pause-on-error\n", "--step", "2", "--pause-on-error")]
    public void Flags_that_do_not_go_together_are_usage_errors(string message, params string[] flags)
    {
        Assert.Equal((1, message), Run(["play", .. flags]));
    }

    [Fact]
    public void Play_for_with_pause_on_error_ends_at_the_pause_and_leaves_the_game_paused()
    {
        var slept = new List<TimeSpan>();
        EditorCommands.PlaySleep = slept.Add;
        try
        {
            int polls = 0;
            Serve(path => path switch
            {
                "/play?onerror=true" => (200, "{\"lines\":[\"playing\"],\"wait\":\"playing\"}"),
                "/play/state" => (200, Caught),
                "/status" => (200, FakeBridge.Status(Interlocked.Increment(ref polls) switch { < 2 => "idle", < 5 => "playing", _ => "paused" }, 3)),
                _ => null,
            });

            var (exitCode, output) = Run("play", "--for", "30", "--pause-on-error");

            Assert.Equal(0, exitCode);
            Assert.StartsWith("paused at the first error, frame 214, 3.5 s in\n  #4105 error", output, StringComparison.Ordinal);
            Assert.Equal(["/play?onerror=true", "/play/state"], Bridge.Requests.Where(r => r.StartsWith("/play", StringComparison.Ordinal)));
            Assert.DoesNotContain("/stop", Bridge.Requests);
            Assert.All(slept, s => Assert.Equal(TimeSpan.FromMilliseconds(250), s));
            Assert.InRange(slept.Count, 1, 4);
        }
        finally
        {
            EditorCommands.PlaySleep = Thread.Sleep;
        }
    }

    [Fact]
    public void Play_for_with_pause_on_error_and_no_error_stops_at_the_end_and_prints_the_log()
    {
        var slept = new List<TimeSpan>();
        EditorCommands.PlaySleep = slept.Add;
        try
        {
            string state = "idle";
            Serve(path =>
            {
                switch (path.Split('?')[0])
                {
                    case "/play":
                        state = "playing";
                        return (200, "{\"lines\":[\"playing\"],\"wait\":\"playing\"}");
                    case "/stop":
                        state = "idle";
                        return (200, "{\"lines\":[\"stopped after 2.0 s\"],\"wait\":\"idle\"}");
                    case "/status":
                        return (200, FakeBridge.Status(state, 3));
                    case "/playlog":
                        return (200, Session);
                    case "/console":
                        return (200, ConsoleJson(4120));
                    default:
                        return null;
                }
            });

            var (exitCode, output) = Run("play", "--for", "2", "--pause-on-error");

            Assert.Equal(0, exitCode);
            Assert.StartsWith("play 2.0 s, stopped ", output, StringComparison.Ordinal);
            Assert.Equal(8, slept.Count);
            Assert.Equal(["/play?onerror=true", "/stop", "/playlog"], Bridge.Requests.Where(r => r is "/play?onerror=true" or "/stop" or "/playlog"));
            Assert.DoesNotContain("/play/state", Bridge.Requests);
        }
        finally
        {
            EditorCommands.PlaySleep = Thread.Sleep;
        }
    }

    // A bridge answering with handle; null answers the status as idle.
    private void Serve(Func<string, (int, string)?> handle, string bridge = "0.13.0")
    {
        Bridge.Publish(Root, bridge);
        Bridge.Handle = path => handle(path) ?? (200, FakeBridge.Status("idle", 3));
    }
}
