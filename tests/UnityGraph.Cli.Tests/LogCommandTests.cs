using System.Globalization;
using UnityGraph.EditorLink.Tests;

namespace UnityGraph.Cli.Tests;

/// <summary>`ug log` and `ug play --for` (SPEC-unity-loop): the last play session as a digest.</summary>
[Collection(nameof(EditorCommandTests))]
public sealed class LogCommandTests : LoopFixture
{
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private static readonly DateTimeOffset _start = new(2026, 10, 1, 14, 2, 11, TimeSpan.Zero);

    private static string Stopped => _start.AddSeconds(12.4).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Finished =>
        "{\"session\":{\"start\":\"2026-10-01T14:02:11.000Z\",\"end\":\"" + _start.AddSeconds(12.4).ToString(TimeFormat, CultureInfo.InvariantCulture)
        + "\",\"after\":4001,\"until\":4120,\"events\":[{\"at\":4.1,\"what\":\"pause\"},{\"at\":6,\"what\":\"resume\"}]}}";

    [Fact]
    public void A_finished_session_is_the_time_the_pauses_and_the_folded_problems()
    {
        Serve(Finished, ConsoleJson(
            4120,
            (4101, "error", "NullReferenceException: Object reference not set", "Assets/Scripts/Enemy.cs:42", 1),
            (4110, "error", "NullReferenceException: Object reference not set", "Assets/Scripts/Enemy.cs:42", 1),
            (4115, "warning", "The referenced script on this Behaviour is missing!", null, 1)), ConsoleJson(4120, (4102, "log", "tick", null, 212)));

        var (exitCode, output) = Run("log");

        Assert.Equal(0, exitCode);
        Assert.Equal(
            "play 12.4 s, stopped " + Stopped + "  (pause 4.1 s, resume 6.0 s)\n"
            + "  2 errors, 1 warning, 212 logs  (last #4120)\n"
            + "  error x2  NullReferenceException: Object reference not set  Assets/Scripts/Enemy.cs:42\n"
            + "  warning  The referenced script on this Behaviour is missing!\n",
            output);
        Assert.Equal(
            ["/console?after=4001&limit=500&types=error%2Cwarning", "/console?after=4001&limit=500&types=log"],
            Bridge.Requests.Where(r => r.StartsWith("/console", StringComparison.Ordinal)));
    }

    [Fact]
    public void Entries_after_the_session_ended_are_not_in_it()
    {
        Serve(Finished, ConsoleJson(
            4130,
            (4110, "error", "inside", null, 1),
            (4125, "error", "after the stop", null, 1)), ConsoleJson(4130, (4111, "log", "in", null, 2), (4126, "log", "out", null, 5)));

        string output = Run("log").Output;

        Assert.Contains("\n  1 error, 2 logs  (last #4120)\n  error  inside\n", output, StringComparison.Ordinal);
        Assert.DoesNotContain("after the stop", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clean_session_says_nothing_logged_and_a_full_log_page_gets_a_plus()
    {
        Serve(Finished, ConsoleJson(4120), ConsoleJson(4120));
        Assert.EndsWith("\n  nothing logged  (last #4120)\n", Run("log").Output, StringComparison.Ordinal);

        (long, string, string, string?, int)[] many = [.. Enumerable.Range(1, 500).Select(_ => ((long)4010, "log", "tick", (string?)null, 1))];
        Serve(Finished, ConsoleJson(4120), ConsoleJson(4120, many));
        Assert.EndsWith("\n  500+ logs  (last #4120)\n", Run("log").Output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_full_page_names_the_console_ids_of_the_rest_and_whole_prints_stacks()
    {
        (long, string, string, string?, int)[] many = [.. Enumerable.Range(0, 500).Select(i => ((long)4100 + i, "log", "tick", (string?)null, 1))];
        Serve(Finished, FullConsoleJson(4120, 1, (4110, "error", "boom\nmore", "Assets/A.cs:3", 1, "A:Fire () (at Assets/A.cs:3)\nUnityEngine.Debug:LogError (object)")), ConsoleJson(4120, many), bridge: "0.9.0");

        string[] lines = Run("log").Output.Split('\n');
        Assert.Equal("  more  whole: ug log --whole; earlier logs: ug console --after 4001 --before 4100 --level log", lines[^2]);

        string whole = Run("log", "--whole").Output;
        Assert.Contains("  error  #4110  boom  Assets/A.cs:3\n    boom\n    more\n    stack:\n      A:Fire () (at Assets/A.cs:3)\n", whole, StringComparison.Ordinal);

        Serve(Finished, ConsoleJson(4120), ConsoleJson(4120));
        Assert.Equal("bridge 0.3.0 is too old for ug log --whole: run ug bridge install again\n", Run("log", "--whole").Output);
        Assert.Equal((1, "--whole goes with --for\n"), Run("play", "--whole"));
    }

    [Fact]
    public void A_running_session_says_so()
    {
        string start = DateTimeOffset.UtcNow.AddSeconds(-5).ToString(TimeFormat, CultureInfo.InvariantCulture);
        Serve(
            "{\"session\":{\"start\":\"" + start + "\",\"end\":null,\"after\":4001,\"until\":null,\"events\":[]}}",
            ConsoleJson(4010, (4005, "error", "boom", null, 1)),
            ConsoleJson(4010));

        string[] lines = Run("log").Output.Split('\n');

        Assert.Matches(@"^playing [5-9]\.\d s so far$", lines[0]);
        Assert.Equal("  1 error  (last #4010)", lines[1]);
        Assert.Equal("  error  boom", lines[2]);
    }

    [Fact]
    public void No_session_an_old_bridge_and_no_bridge_each_have_their_line()
    {
        Assert.StartsWith("Unity not reachable (", Run("log").Output, StringComparison.Ordinal);

        Serve("{\"session\":null}", ConsoleJson(1), ConsoleJson(1));
        Assert.Equal("no play session since the editor opened\n", Run("log").Output);

        Bridge.Publish(Root, "0.2.0");
        Assert.Equal("bridge 0.2.0 is too old for ug log: run ug bridge install again\n", Run("log").Output);

        Directory.CreateDirectory(Path.Combine(Root, ".claude"));
        File.WriteAllText(Path.Combine(Root, ".claude", "unitygraph.json"), "{\"modules\":{\"unity-loop\":false}}");
        Assert.Equal("unity-loop is off in .claude/unitygraph.json\n", Run("log").Output);
    }

    [Fact]
    public void The_budget_cuts_the_rows_and_ends_with_more()
    {
        (long, string, string, string?, int)[] entries =
        [
            .. Enumerable.Range(1, 60).Select(i => ((long)i + 4001, "error", "problem number " + i.ToString(CultureInfo.InvariantCulture) + new string('.', 60), (string?)null, 1)),
        ];
        Serve(Finished, ConsoleJson(4120, entries), ConsoleJson(4120));

        string output = Run("log", "--budget", "200").Output;

        Assert.True(output.Length <= 900, output.Length.ToString(CultureInfo.InvariantCulture));
        Assert.EndsWith("  more  ug log --budget 400\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Play_for_plays_waits_stops_and_prints_the_log()
    {
        string state = "idle";
        var slept = new List<TimeSpan>();
        EditorCommands.PlaySleep = slept.Add;
        try
        {
            Serve(Finished, ConsoleJson(4120, (4110, "error", "boom", null, 1)), ConsoleJson(4120), path =>
            {
                switch (path)
                {
                    case "/play":
                        state = "playing";
                        return (200, "{\"lines\":[\"playing\"],\"wait\":\"playing\"}");
                    case "/stop":
                        state = "idle";
                        return (200, "{\"lines\":[\"stopped after 12.4 s\"],\"wait\":\"idle\"}");
                    case "/status":
                        return (200, FakeBridge.Status(state, 3));
                    default:
                        return null;
                }
            });

            var (exitCode, output) = Run("play", "--for", "3");

            Assert.Equal(0, exitCode);
            Assert.Equal(
                "play 12.4 s, stopped " + Stopped + "  (pause 4.1 s, resume 6.0 s)\n  1 error  (last #4120)\n  error  boom\n",
                output);
            Assert.Equal([TimeSpan.FromSeconds(3)], slept);
            Assert.Equal(["/play", "/stop", "/playlog"], Bridge.Requests.Where(r => r is "/play" or "/stop" or "/playlog"));
        }
        finally
        {
            EditorCommands.PlaySleep = Thread.Sleep;
        }
    }

    [Fact]
    public void Play_for_ends_at_a_refused_play_and_shows_a_failed_stop()
    {
        var slept = new List<TimeSpan>();
        EditorCommands.PlaySleep = slept.Add;
        try
        {
            Serve(Finished, ConsoleJson(4120), ConsoleJson(4120), path => path == "/play" ? (200, "{\"error\":\"the scripts do not compile: ug compile\"}") : null);
            Assert.Equal("the scripts do not compile: ug compile\n", Run("play", "--for", "2").Output);
            Assert.Empty(slept);
            Assert.DoesNotContain("/stop", Bridge.Requests);

            Serve(Finished, ConsoleJson(4120), ConsoleJson(4120), path => path switch
            {
                "/play" => (200, "{\"lines\":[\"playing\"]}"),
                "/stop" => (200, "{\"error\":\"not playing\"}"),
                _ => null,
            });
            string output = Run("play", "--for", "2").Output;
            Assert.StartsWith("not playing\nplay 12.4 s, stopped ", output, StringComparison.Ordinal);
        }
        finally
        {
            EditorCommands.PlaySleep = Thread.Sleep;
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("601")]
    public void Play_for_is_one_to_600_seconds(string seconds)
    {
        var (exitCode, output) = Run("play", "--for", seconds);

        Assert.Equal(1, exitCode);
        Assert.Equal("--for must be 1 to 600 seconds\n", output);
    }

    // problems: the answer to the error,warning query; logs: to the log query; extra: other paths (null: not served).
    private void Serve(string playlog, string problems, string logs, Func<string, (int, string)?>? extra = null, string bridge = "0.3.0")
    {
        Bridge.Publish(Root, bridge);
        Bridge.Handle = path =>
        {
            if (extra?.Invoke(path) is { } answer)
            {
                return answer;
            }

            return path.Split('?')[0] switch
            {
                "/playlog" => (200, playlog),
                "/console" => (200, path.Contains("types=log", StringComparison.Ordinal) ? logs : problems),
                _ => (200, FakeBridge.Status("idle", 3)),
            };
        };
    }
}
