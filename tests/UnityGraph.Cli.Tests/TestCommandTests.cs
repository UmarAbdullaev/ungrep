using System.Globalization;
using System.Text;

namespace UnityGraph.Cli.Tests;

/// <summary>`ug test` (SPEC-unity-loop): the compile check first, then one line when green and failures with file:line.</summary>
[Collection(nameof(EditorCommandTests))]
public sealed class TestCommandTests : LoopFixture
{
    private const string Clean = "{\"compile\":2,\"messages\":[]}";

    [Fact]
    public void Green_is_one_line()
    {
        Handle(Clean, "{\"state\":\"finished\",\"passed\":156,\"failed\":0,\"skipped\":4,\"inconclusive\":0,\"seconds\":8.24,\"failures\":[]}");

        var (exitCode, output) = Run("test");

        Assert.Equal(0, exitCode);
        Assert.Equal("EditMode: 156 passed, 4 skipped in 8.2 s\n", output);
        Assert.Contains("/tests?platform=EditMode", Bridge.Requests);
    }

    [Fact]
    public void Red_lists_failures_with_file_and_line()
    {
        Handle(Clean, Finished(1, 11, ("Game.Tests.TurretTests.Fires_at_range", "Expected: 3\n  But was: 2", "Assets/Tests/Editor/TurretTests.cs:34"), ("Game.Tests.Other", null, null)));

        string output = Run("test", "--mode", "playmode", "--filter", "Turret").Output;

        Assert.Equal("""
            PlayMode: 1 failed, 11 passed in 1.4 s
              Assets/Tests/Editor/TurretTests.cs:34  Game.Tests.TurretTests.Fires_at_range  Expected: 3
              (no file)  Game.Tests.Other

            """.ReplaceLineEndings("\n"), output);
        Assert.Contains("/tests?platform=PlayMode&filter=Turret", Bridge.Requests);
    }

    [Fact]
    public void At_most_20_failures_are_listed()
    {
        Handle(Clean, Finished(25, 0, [.. Enumerable.Range(1, 25).Select(i => ("T" + i, "bad " + i, "Assets/T.cs:" + i))]));

        string[] lines = Run("test").Output.TrimEnd('\n').Split('\n');

        Assert.Equal("EditMode: 25 failed in 1.4 s", lines[0]);
        Assert.Equal(22, lines.Length);
        Assert.Equal("  (+5 more failures)", lines[^1]);
    }

    [Fact]
    public void A_new_bridge_names_the_failures_and_the_whole_failure_commands()
    {
        Handle(Clean, Finished(25, 0, [.. Enumerable.Range(1, 25).Select(i => ("T" + i, "Expected: " + i + "\n  But was: 0", "Assets/T.cs:" + i))]), "0.9.0");

        string[] lines = Run("test").Output.TrimEnd('\n').Split('\n');

        Assert.Equal(23, lines.Length);
        Assert.Equal("  (+5 more failures: ug test --failures)", lines[^2]);
        Assert.Equal("  more  whole: ug test --failure <name>", lines[^1]);
    }

    [Fact]
    public void Failures_lists_every_failure_of_the_last_run_without_a_new_run()
    {
        Handle(Clean, Finished(25, 3, [.. Enumerable.Range(1, 25).Select(i => ("T" + i, "bad " + i, "Assets/T.cs:" + i))]), "0.9.0");

        string[] lines = Run("test", "--failures").Output.TrimEnd('\n').Split('\n');

        Assert.Equal("last run: 25 failed, 3 passed in 1.4 s", lines[0]);
        Assert.Equal(26, lines.Length);
        Assert.Equal("  Assets/T.cs:25  T25  bad 25", lines[^1]);
        Assert.DoesNotContain(Bridge.Requests, r => r.StartsWith("/tests?", StringComparison.Ordinal) || r.StartsWith("/refresh", StringComparison.Ordinal));
        Assert.Contains("/tests/result?id=", Bridge.Requests);
    }

    [Fact]
    public void Failure_prints_one_failure_whole_or_says_why_not()
    {
        string stack = "at Game.Tests.TurretTests.Fires_at_range () [0x00001] in Assets/Tests/Editor/TurretTests.cs:34\n";
        string result = "{\"state\":\"finished\",\"passed\":1,\"failed\":2,\"skipped\":0,\"inconclusive\":0,\"seconds\":1.4,\"failures\":["
            + "{\"name\":\"Game.Tests.TurretTests.Fires_at_range\",\"message\":\"  Expected: 3\\n  But was:  2\\n\",\"frame\":\"Assets/Tests/Editor/TurretTests.cs:34\",\"stack\":" + System.Text.Json.JsonSerializer.Serialize(stack) + "},"
            + "{\"name\":\"Game.Tests.TurretTests.Turns\",\"message\":null,\"frame\":null,\"stack\":null}]}";
        Handle(Clean, result, "0.9.0");

        Assert.Equal("""
            Game.Tests.TurretTests.Fires_at_range  Assets/Tests/Editor/TurretTests.cs:34
              Expected: 3
              But was:  2
            stack:
            at Game.Tests.TurretTests.Fires_at_range () [0x00001] in Assets/Tests/Editor/TurretTests.cs:34

            """.ReplaceLineEndings("\n"), Run("test", "--failure", "fires_at").Output);
        Assert.Equal("2 failures match TurretTests: give more of the name\n  Game.Tests.TurretTests.Fires_at_range\n  Game.Tests.TurretTests.Turns\n", Run("test", "--failure", "TurretTests").Output);
        Assert.Equal("no failure matching Nope in the last run\n", Run("test", "--failure", "Nope").Output);
    }

    [Fact]
    public void Failures_without_a_run_or_on_an_old_bridge_say_so()
    {
        Handle(Clean, "{\"state\":\"none\"}", "0.9.0");
        Assert.Equal("no test run since the editor opened\n", Run("test", "--failures").Output);

        Handle(Clean, "{\"state\":\"none\"}", "0.8.0");
        Assert.Equal("bridge 0.8.0 is too old for ug test --failures: run ug bridge install again\n", Run("test", "--failures").Output);
        Assert.Equal("bridge 0.8.0 is too old for ug test --failure: run ug bridge install again\n", Run("test", "--failure", "X").Output);
    }

    [Fact]
    public void No_tests_is_said()
    {
        Handle(Clean, "{\"state\":\"finished\",\"passed\":0,\"failed\":0,\"skipped\":0,\"inconclusive\":0,\"seconds\":0,\"failures\":[]}");

        Assert.Equal("EditMode: no tests ran\n", Run("test", "--filter", "Nope").Output);
    }

    [Fact]
    public void Compile_errors_stop_the_run_and_are_listed()
    {
        Handle(
            "{\"compile\":2,\"messages\":[{\"assembly\":\"A\",\"file\":\"Assets/Scripts/X.cs\",\"line\":3,\"column\":5,\"severity\":\"error\",\"code\":\"CS0103\",\"text\":\"The name 'q' does not exist\"}]}",
            Finished(0, 1));

        Assert.Equal("""
            not run: the scripts do not compile (1 error)
              Assets/Scripts/X.cs:3:5  CS0103  The name 'q' does not exist

            """.ReplaceLineEndings("\n"), Run("test").Output);
        Assert.DoesNotContain(Bridge.Requests, r => r.StartsWith("/tests", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(409, "a test run is in progress", "not run: a test run is in progress\n")]
    [InlineData(501, "com.unity.test-framework is not in this project", "not run: com.unity.test-framework is not in this project\n")]
    public void A_refused_start_is_the_bridge_s_reason(int status, string error, string expected)
    {
        Bridge.Publish(Root, "0.2.0");
        Bridge.Handle = path => path.Split('?')[0] switch
        {
            "/refresh" => (200, "{\"compiling\":false,\"compile\":2}"),
            "/compile" => (200, Clean),
            "/tests" => (status, "{\"error\":\"" + error + "\"}"),
            _ => (200, FakeStatus("idle")),
        };

        Assert.Equal(expected, Run("test").Output);
    }

    [Fact]
    public void Play_mode_is_refused_and_unreachable_is_said()
    {
        Bridge.Publish(Root, "0.2.0");
        Bridge.Handle = _ => (200, FakeStatus("playing"));
        Assert.Equal("not run: Unity is in play mode: ug stop first\n", Run("test").Output);

        Bridge.Dispose();
        File.Delete(UnityGraph.EditorLink.BridgeClient.DiscoveryPath(Root));
        Assert.StartsWith("Unity not reachable (", Run("test").Output, StringComparison.Ordinal);
        Assert.Equal((1, "--mode must be EditMode or PlayMode\n"), Run("test", "--mode", "Both"));
    }

    private static string FakeStatus(string state) =>
        "{\"state\":\"" + state + "\",\"unity\":\"6000.3.14f1\",\"bridge\":\"0.2.0\",\"compile\":2,\"errors\":0,\"warnings\":0,\"console\":7}";

    private static string Finished(int failed, int passed, params (string Name, string? Message, string? Frame)[] failures)
    {
        var text = new StringBuilder("{\"state\":\"finished\",\"passed\":").Append(passed.ToString(CultureInfo.InvariantCulture))
            .Append(",\"failed\":").Append(failed.ToString(CultureInfo.InvariantCulture)).Append(",\"skipped\":0,\"inconclusive\":0,\"seconds\":1.4,\"failures\":[");
        text.AppendJoin(',', failures.Select(f =>
            "{\"name\":\"" + f.Name + "\",\"message\":" + System.Text.Json.JsonSerializer.Serialize(f.Message) + ",\"frame\":" + (f.Frame == null ? "null" : "\"" + f.Frame + "\"") + "}"));
        return text.Append("]}").ToString();
    }

    // An idle 0.2.0 bridge: a compile that finds nothing to do, then a run that finishes with result.
    private void Handle(string compile, string result, string bridge = "0.2.0")
    {
        Bridge.Publish(Root, bridge);
        Bridge.Handle = path => path.Split('?')[0] switch
        {
            "/refresh" => (200, "{\"compiling\":false,\"compile\":2}"),
            "/compile" => (200, compile),
            "/tests" => (200, "{\"id\":\"r1\"}"),
            "/tests/result" => (200, result),
            _ => (200, FakeStatus("idle")),
        };
    }
}
