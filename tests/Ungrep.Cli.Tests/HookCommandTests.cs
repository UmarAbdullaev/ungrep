namespace Ungrep.Cli.Tests;

/// <summary>
/// `ug hook` command line: anything but `hook &lt;event&gt;` is silent with exit 0.
/// </summary>
public sealed class HookCommandTests
{
    [Theory]
    [InlineData("hook")]
    [InlineData("hook|")]
    [InlineData("hook|Stop|extra")]
    public void Bad_command_lines_are_silent(string commandLine)
    {
        var output = new StringWriter();

        Assert.Equal(0, HookCommand.Run(commandLine.Split('|'), new StringReader("{}"), output, []));
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void Outside_a_unity_project_is_silent()
    {
        var output = new StringWriter();

        int exitCode = HookCommand.Run(["hook", "Stop"], new StringReader("""{ "cwd": "/nowhere" }"""), output, []);

        Assert.Equal((0, ""), (exitCode, output.ToString()));
    }
}
