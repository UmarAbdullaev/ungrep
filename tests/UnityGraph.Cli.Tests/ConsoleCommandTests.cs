namespace UnityGraph.Cli.Tests;

/// <summary>`ug console` (SPEC-unity-loop): folding, order, level, budget and the ways Unity is out of reach.</summary>
[Collection(nameof(EditorCommandTests))]
public sealed class ConsoleCommandTests : LoopFixture
{
    [Fact]
    public void Folds_repeats_and_puts_errors_first_then_the_newest()
    {
        Bridge.Publish(Root, "0.2.0");
        Bridge.Handle = _ => (200, ConsoleJson(
            4120,
            (4100, "warning", "The referenced script on this Behaviour is missing!", null, 1),
            (4101, "error", "NullReferenceException: Object reference not set\nat Enemy.Update", "Assets/Scripts/Enemy.cs:42", 5),
            (4105, "error", "Missing key 'Hit' in Animator", "Assets/Scripts/Enemy.cs:51", 1),
            (4110, "error", "NullReferenceException: Object reference not set", "Assets/Scripts/Enemy.cs:42", 7),
            (4120, "warning", "Old shader", null, 2)));

        var (exitCode, output) = Run("console");

        Assert.Equal(0, exitCode);
        Assert.Equal("""
            console  13 errors, 3 warnings  (last #4120)
              error x12  NullReferenceException: Object reference not set  Assets/Scripts/Enemy.cs:42
              error  Missing key 'Hit' in Animator  Assets/Scripts/Enemy.cs:51
              warning x2  Old shader
              warning  The referenced script on this Behaviour is missing!

            """.ReplaceLineEndings("\n"), output);
    }

    [Fact]
    public void Asks_for_the_level_the_count_and_the_id_it_was_given()
    {
        Bridge.Publish(Root, "0.2.0");
        Bridge.Handle = path => path.StartsWith("/status", StringComparison.Ordinal) ? (200, "{}") : (200, ConsoleJson(7));

        Assert.Equal("console  nothing at warning level or worse  (last #7)\n", Run("console").Output);
        Assert.Equal("console  nothing at error level  (last #7)\n", Run("console", "--level", "error", "--count", "50", "--after", "6").Output);
        Run("console", "--level", "log", "--count", "900");

        string[] asked = [.. Bridge.Requests.Where(r => r.StartsWith("/console", StringComparison.Ordinal))];
        Assert.Equal(["/console?after=0&limit=200&types=error%2Cwarning", "/console?after=6&limit=50&types=error", "/console?after=0&limit=500"], asked);
    }

    [Fact]
    public void Logs_are_rows_at_log_level_and_long_text_is_cut()
    {
        Bridge.Publish(Root, "0.2.0");
        Bridge.Handle = _ => (200, ConsoleJson(
            9,
            (8, "log", new string('x', 300), null, 1),
            (9, "log", "hello", null, 3)));

        string[] lines = Run("console", "--level", "log").Output.Split('\n');

        Assert.Equal("console  4 logs  (last #9)", lines[0]);
        Assert.Equal("  log x3  hello", lines[1]);
        Assert.Equal("  log  " + new string('x', 157) + "...", lines[2]);
    }

    [Fact]
    public void A_cut_answer_ends_with_a_more_line_naming_a_bigger_budget()
    {
        Bridge.Publish(Root, "0.2.0");
        Bridge.Handle = _ => (200, ConsoleJson(
            500,
            [.. Enumerable.Range(1, 200).Select(i => ((long)i, "warning", "warning number " + i + " " + new string('w', 60), (string?)null, 1))]));

        string output = Run("console", "--budget", "200").Output;

        Assert.EndsWith("  more  ug console --level warning --count 200 --budget 400\n", output, StringComparison.Ordinal);
        Assert.True(output.Length < 200 * 4 + 200, output.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Says_so_when_unity_is_not_reachable()
    {
        string output = Run("console").Output;

        Assert.StartsWith("Unity not reachable (no bridge.json: Unity is closed or the bridge is not installed); ug console needs the bridge\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Says_so_when_unity_does_not_answer()
    {
        Bridge.Publish(Root, "0.2.0");
        Bridge.Handle = path => path.StartsWith("/status", StringComparison.Ordinal) ? (200, "{}") : (500, "{\"error\":\"boom\"}");

        Assert.Equal("no answer from Unity (bridge answered 500: boom)\n", Run("console").Output);
    }

    [Fact]
    public void The_module_switch_and_a_bad_level_are_answered()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".claude"));
        File.WriteAllText(Path.Combine(Root, ".claude", "unitygraph.json"), "{\"modules\":{\"unity-loop\":false}}");
        Assert.Equal("unity-loop is off in .claude/unitygraph.json\n", Run("console").Output);

        File.Delete(Path.Combine(Root, ".claude", "unitygraph.json"));
        Bridge.Publish(Root, "0.2.0");
        Bridge.Handle = _ => (200, ConsoleJson(1));
        Assert.Equal((1, "--level must be error, warning or log\n"), Run("console", "--level", "debug"));
    }

    private const string NullStack = "Enemy:Update () (at Assets/Scripts/Enemy.cs:42)\nUnityEngine.Debug:LogException (System.Exception)";

    private string FullPage(long first, int count) => FullConsoleJson(
        4120,
        3000,
        (first, "error", "NullReferenceException: Object reference not set\n  at Enemy.Update", "Assets/Scripts/Enemy.cs:42", count, NullStack),
        (4120, "warning", "Old shader", null, 1, null));

    [Fact]
    public void The_default_answer_names_the_whole_rows_and_the_older_entries()
    {
        Bridge.Publish(Root, "0.9.0");
        Bridge.Handle = path => path.StartsWith("/status", StringComparison.Ordinal) ? (200, "{}") : (200, FullPage(4101, 1));

        string output = Run("console", "--count", "2").Output;

        Assert.Equal("""
            console  1 error, 1 warning  (last #4120)
              error  NullReferenceException: Object reference not set  Assets/Scripts/Enemy.cs:42
              warning  Old shader
              more  whole: ug console --whole --count 2; older: ug console --count 2 --before 4101

            """.ReplaceLineEndings("\n"), output);
    }

    [Fact]
    public void An_old_bridge_answers_as_before_and_refuses_the_new_flags_by_name()
    {
        Bridge.Publish(Root, "0.8.0");
        Bridge.Handle = path => path.StartsWith("/status", StringComparison.Ordinal) ? (200, "{}") : (200, FullPage(4101, 1));

        Assert.DoesNotContain("more", Run("console", "--count", "2").Output, StringComparison.Ordinal);
        Assert.Equal("bridge 0.8.0 is too old for ug console --whole: run ug bridge install again\n", Run("console", "--whole").Output);
        Assert.Equal("bridge 0.8.0 is too old for ug console --id: run ug bridge install again\n", Run("console", "--id", "4101").Output);
        Assert.Equal("bridge 0.8.0 is too old for ug console --before: run ug bridge install again\n", Run("console", "--before", "4101").Output);
    }

    [Fact]
    public void Whole_rows_carry_their_id_their_message_and_their_stack()
    {
        Bridge.Publish(Root, "0.9.0");
        Bridge.Handle = path => path.StartsWith("/status", StringComparison.Ordinal) ? (200, "{}") : (200, FullPage(4101, 3));

        string output = Run("console", "--whole", "--before", "4200").Output;

        Assert.Equal("""
            console  3 errors, 1 warning  (last #4120)
              error x3  #4101  NullReferenceException: Object reference not set  Assets/Scripts/Enemy.cs:42
                NullReferenceException: Object reference not set
                  at Enemy.Update
                stack:
                  Enemy:Update () (at Assets/Scripts/Enemy.cs:42)
                  UnityEngine.Debug:LogException (System.Exception)
              warning  #4120  Old shader

            """.ReplaceLineEndings("\n"), output);
        Assert.Contains("/console?after=0&limit=200&types=error%2Cwarning&before=4200", Bridge.Requests);
    }

    [Fact]
    public void Id_prints_one_entry_whole_or_says_the_ring_dropped_it()
    {
        Bridge.Publish(Root, "0.9.0");
        Bridge.Handle = path => path.StartsWith("/status", StringComparison.Ordinal) ? (200, "{}")
            : path == "/console?id=4101" ? (200, FullConsoleJson(4120, 3000, (4101, "error", "boom\nsecond line", "Assets/A.cs:3", 12, NullStack)))
            : (200, FullConsoleJson(4120, 3000));

        Assert.Equal("""
            #4101 error x12
            boom
            second line
            stack:
            Enemy:Update () (at Assets/Scripts/Enemy.cs:42)
            UnityEngine.Debug:LogException (System.Exception)

            """.ReplaceLineEndings("\n"), Run("console", "--id", "4101").Output);
        Assert.Equal("no console entry #17 (the bridge keeps the newest 2000)\n", Run("console", "--id", "17").Output);
        Assert.Equal((1, "--id takes no other flag but --budget\n"), Run("console", "--id", "17", "--whole"));
    }

    [Fact]
    public void A_clean_short_answer_has_no_more_line()
    {
        Bridge.Publish(Root, "0.9.0");
        Bridge.Handle = path => path.StartsWith("/status", StringComparison.Ordinal) ? (200, "{}")
            : (200, FullConsoleJson(4120, 4120, (4120, "warning", "Old shader", null, 1, "UnityEngine.Debug:LogWarning (object)")));

        Assert.Equal("console  1 warning  (last #4120)\n  warning  Old shader\n", Run("console").Output);
    }
}
