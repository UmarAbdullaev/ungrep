namespace UnityGraph.Cli.Tests;

/// <summary>
/// `ug --version` is the smoke test the launcher and `ug doctor` rely on.
/// </summary>
public sealed class VersionTests
{
    [Fact]
    public void Version_prints_the_assembly_version_and_exits_zero()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        int exitCode = CliApp.Run(["--version"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Equal("0.1.0" + Environment.NewLine, output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void Unknown_option_fails_with_a_message_on_stderr()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        int exitCode = CliApp.Run(["--no-such-option"], output, error);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("--no-such-option", error.ToString(), StringComparison.Ordinal);
    }
}
