namespace Ungrep.Stats.Tests;

/// <summary>
/// Directory names must match what Claude Code writes, or `ug stats` finds no transcripts.
/// </summary>
public sealed class ClaudeProjectsTests
{
    [Theory]
    [InlineData(@"D:\Projects\Unity\purge-squad", "D--Projects-Unity-purge-squad")]
    [InlineData(@"D:\Projects\Unity\purge-squad\", "D--Projects-Unity-purge-squad")]
    [InlineData(@"D:\Projects\Unity\captain-of-space\.claude\worktrees\a_b", "D--Projects-Unity-captain-of-space--claude-worktrees-a-b")]
    [InlineData("/Users/dev/My Game", "-Users-dev-My-Game")]
    public void Directory_name_replaces_every_non_alphanumeric_character(string projectPath, string expected)
    {
        Assert.Equal(expected, ClaudeProjects.DirectoryName(projectPath));
    }

    [Fact]
    public void Transcript_directory_is_under_the_config_dir()
    {
        string configDir = Path.Combine(Path.GetTempPath(), "claude-config");
        string project = Path.Combine(Path.GetTempPath(), "Game");

        string dir = ClaudeProjects.TranscriptDirectory(project, configDir);

        Assert.Equal(Path.Combine(configDir, "projects", ClaudeProjects.DirectoryName(project)), dir);
    }
}
