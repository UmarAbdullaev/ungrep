using UnityGraph.EditorLink.Tests;

namespace UnityGraph.Cli.Tests;

/// <summary>`ug ui`, `ug click` and the device actions (SPEC-play-input): their requests, ranges, gates and digests, against a fake bridge.</summary>
[Collection(nameof(EditorCommandTests))]
public sealed class PlayInputCommandTests : LoopFixture
{
    private const string Digest = "\"  now: 1 error; scene Main -> Level1; changed Player (moved)\",\"  error  boom  Assets/Scripts/Enemy.cs:42\"";

    [Theory]
    [InlineData("/input/press?key=Space", "press", "Space")]
    [InlineData("/input/press?key=Space&hold=0.5", "press", "Space", "--hold", "0.5")]
    [InlineData("/input/press?key=pad%3Asouth&hold=60", "press", "pad:south", "--hold", "60")]
    [InlineData("/input/stick?side=left&x=1&y=0", "stick", "left", "1,0")]
    [InlineData("/input/stick?side=right&x=-0.5&y=1&hold=2", "stick", "right", "-0.5,1", "--hold", "2")]
    [InlineData("/input/tap?x=640&y=360", "tap", "640,360")]
    [InlineData("/input/tap?x=10.5&y=20&mouse=true&hold=0.3", "tap", "10.5,20", "--mouse", "--hold", "0.3")]
    [InlineData("/input/swipe?x1=100&y1=200&x2=300&y2=200", "swipe", "100,200", "300,200")]
    [InlineData("/input/swipe?x1=0&y1=0&x2=640&y2=0&mouse=true&over=1", "swipe", "0,0", "640,0", "--mouse", "--over", "1")]
    [InlineData("/input/wait?seconds=2", "wait", "2")]
    [InlineData("/input/wait?seconds=0.05", "wait", "0.05")]
    [InlineData("/input/step?frames=3", "play", "--step", "3")]
    [InlineData("/input/step?frames=1", "play", "--step")]
    [InlineData("/ui", "ui")]
    [InlineData("/ui?all=true", "ui", "--all")]
    [InlineData("/ui/click?element=Play", "click", "Play")]
    [InlineData("/ui/click?element=Canvas%2FMenu%2FPlay", "click", "Canvas/Menu/Play")]
    [InlineData("/ui/click?element=320%2C240", "click", "320,240")]
    [InlineData("/ui/click?element=Volume&value=0.5", "click", "Volume", "--value", "0.5")]
    public void Each_command_sends_one_request_and_prints_the_answer(string request, params string[] args)
    {
        Serve(_ => "{\"lines\":[\"did it\"," + Digest + "]}");

        (int exitCode, string output) = Run(args);

        Assert.Equal(0, exitCode);
        Assert.Equal("did it\n  now: 1 error; scene Main -> Level1; changed Player (moved)\n  error  boom  Assets/Scripts/Enemy.cs:42\n", output);
        Assert.Equal(["/status", request], Bridge.Requests);
    }

    [Theory]
    [InlineData("--hold must be 0.05 to 60 seconds", "press", "Space", "--hold", "0.01")]
    [InlineData("--hold must be 0.05 to 60 seconds", "press", "Space", "--hold", "61")]
    [InlineData("--hold must be 0.05 to 60 seconds", "stick", "left", "1,0", "--hold", "0")]
    [InlineData("--hold must be 0.05 to 60 seconds", "tap", "1,1", "--hold", "100")]
    [InlineData("--over must be 0.05 to 10 seconds", "swipe", "0,0", "1,1", "--over", "11")]
    [InlineData("seconds must be 0.05 to 60", "wait", "0.01")]
    [InlineData("seconds must be 0.05 to 60", "wait", "61")]
    [InlineData("side is left or right", "stick", "up", "1,0")]
    [InlineData("x,y must be -1 to 1, like 1,0", "stick", "left", "2,0")]
    [InlineData("x,y must be -1 to 1, like 1,0", "stick", "left", "right")]
    [InlineData("a point is x,y in Game view pixels, like 640,360 (y down from the top)", "tap", "left")]
    [InlineData("a point is x,y in Game view pixels, like 640,360 (y down from the top)", "swipe", "0,0", "1")]
    [InlineData("--step must be 1 to 600 frames", "play", "--step", "0")]
    [InlineData("--step must be 1 to 600 frames", "play", "--step", "601")]
    [InlineData("--step and --for cannot be used together", "play", "--step", "2", "--for", "3")]
    public void A_value_out_of_range_is_refused_before_asking_Unity(string message, params string[] args)
    {
        Serve(_ => "{\"lines\":[\"never\"]}");

        (int exitCode, string output) = Run(args);

        Assert.Equal(1, exitCode);
        Assert.Equal(message + "\n", output);
        Assert.Empty(Bridge.Requests);
    }

    [Fact]
    public void A_refusal_from_the_bridge_is_printed_as_it_is()
    {
        Serve(_ => "{\"error\":\"not playing: ug play first\"}");

        string output = Run("press", "Space").Output;

        Assert.Equal("not playing: ug play first\n", output);
    }

    [Fact]
    public void Shot_adds_the_game_view_picture_after_the_action()
    {
        Serve(path => path.StartsWith("/shot", StringComparison.Ordinal) ? "{\"lines\":[\"shot game 640x360: D:/p/shot.png\"]}" : "{\"lines\":[\"pressed Space for 0.1 s\",\"  now: nothing changed\"]}");

        string output = Run("press", "Space", "--shot").Output;

        Assert.Equal("pressed Space for 0.1 s\n  now: nothing changed\nshot game 640x360: D:/p/shot.png\n", output);
        Assert.Equal(["/status", "/input/press?key=Space", "/status", "/shot?view=game"], Bridge.Requests);
    }

    [Theory]
    [InlineData("click", "Play")]
    [InlineData("stick", "left", "1,0")]
    [InlineData("tap", "1,1")]
    [InlineData("swipe", "0,0", "1,1")]
    [InlineData("wait", "0.5")]
    public void Every_action_takes_shot(params string[] args)
    {
        Serve(path => path.StartsWith("/shot", StringComparison.Ordinal) ? "{\"lines\":[\"picture\"]}" : "{\"lines\":[\"ok\"]}");

        string output = Run([.. args, "--shot"]).Output;

        Assert.Equal("ok\npicture\n", output);
        Assert.Equal("/shot?view=game", Bridge.Requests.Last());
    }

    [Fact]
    public void A_refused_action_takes_no_picture()
    {
        Serve(path => path.StartsWith("/shot", StringComparison.Ordinal) ? "{\"lines\":[\"picture\"]}" : "{\"error\":\"not playing: ug play first\"}");

        string output = Run("tap", "1,1", "--shot").Output;

        Assert.Equal("not playing: ug play first\n", output);
        Assert.DoesNotContain("/shot?view=game", Bridge.Requests);
    }

    [Fact]
    public void A_picture_that_cannot_be_taken_keeps_the_actions_result_and_adds_its_refusal()
    {
        Serve(path => path.StartsWith("/shot", StringComparison.Ordinal) ? "{\"error\":\"no Game view\"}" : "{\"lines\":[\"ok\"]}");

        (int exitCode, string output) = Run("wait", "0.5", "--shot");

        Assert.Equal(0, exitCode);
        Assert.Equal("ok\nno Game view\n", output);
    }

    [Fact]
    public void An_old_bridge_is_named_and_asked_to_be_installed_again()
    {
        Serve(_ => "{\"lines\":[\"never\"]}", "0.3.0");

        string output = Run("press", "Space").Output;

        Assert.Equal("bridge 0.3.0 is too old for ug press: run ug bridge install again\n", output);
        Assert.DoesNotContain("/input/press?key=Space", Bridge.Requests);
    }

    [Fact]
    public void The_module_switch_turns_every_command_off()
    {
        Serve(_ => "{\"lines\":[\"never\"]}");
        Directory.CreateDirectory(Path.Combine(Root, ".claude"));
        File.WriteAllText(Path.Combine(Root, ".claude", "unitygraph.json"), "{\"modules\":{\"play-input\":false}}");

        string[][] commands = [["ui"], ["click", "Play"], ["press", "Space"], ["stick", "left", "1,0"], ["tap", "1,1"], ["swipe", "0,0", "1,1"], ["wait", "1"], ["play", "--step"]];
        foreach (string[] args in commands)
        {
            (int exitCode, string output) = Run(args);

            Assert.Equal(0, exitCode);
            Assert.Equal("play-input is off in .claude/unitygraph.json\n", output);
        }

        Assert.Empty(Bridge.Requests);
    }

    [Fact]
    public void Without_the_bridge_an_action_says_it_needs_it()
    {
        string output = Run("press", "Space").Output;

        Assert.StartsWith("Unity not reachable (", output, StringComparison.Ordinal);
        Assert.Contains("ug press needs the bridge", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Actions_run_in_a_batch_and_a_refusal_stops_it()
    {
        Serve(path => path == "/input/tap?x=1&y=1" ? "{\"error\":\"no touchscreen\"}" : "{\"lines\":[\"ok " + path + "\"]}");

        (_, string output) = Run("batch", "press Space", "stick left 1,0 --hold 0.2", "tap 1,1", "wait 1");

        Assert.Equal("""
            1/4 press Space
            ok /input/press?key=Space
            2/4 stick left 1,0 --hold 0.2
            ok /input/stick?side=left&x=1&y=0&hold=0.2
            3/4 tap 1,1
            no touchscreen
            stopped at step 3: 1 step not run. Earlier steps stay done; ug undo reverts them one by one.

            """.ReplaceLineEndings("\n"), output);
        Assert.Equal(["/status", "/input/press?key=Space", "/input/stick?side=left&x=1&y=0&hold=0.2", "/input/tap?x=1&y=1"], Bridge.Requests);
    }

    [Fact]
    public void A_batch_step_with_shot_takes_its_picture_and_goes_on()
    {
        Serve(path => path.StartsWith("/shot", StringComparison.Ordinal) ? "{\"lines\":[\"picture\"]}" : "{\"lines\":[\"ok\"]}");

        Run("batch", "click Play --shot", "wait 1");

        Assert.Equal(["/status", "/ui/click?element=Play", "/shot?view=game", "/input/wait?seconds=1"], Bridge.Requests);
    }

    [Fact]
    public void A_batch_step_with_a_bad_value_stops_the_batch_without_asking_Unity()
    {
        Serve(_ => "{\"lines\":[\"ok\"]}");

        (_, string output) = Run("batch", "press Space", "wait 100", "wait 1");

        Assert.Contains("seconds must be 0.05 to 60", output, StringComparison.Ordinal);
        Assert.Contains("stopped at step 2", output, StringComparison.Ordinal);
        Assert.Equal(["/status", "/input/press?key=Space"], Bridge.Requests);
    }

    // answer: the bridge's body for a path and query; the status call is answered the way a bridge does.
    private void Serve(Func<string, string> answer, string bridge = "0.4.0")
    {
        Bridge.Publish(Root, bridge);
        Bridge.Handle = path => path == "/status" ? (200, FakeBridge.Status("playing", 3)) : (200, answer(path));
    }
}
