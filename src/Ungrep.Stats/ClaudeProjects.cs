using System.Text;

namespace Ungrep.Stats;

/// <summary>
/// Finds where Claude Code keeps a project's transcripts: `&lt;config dir&gt;/projects/&lt;name&gt;`, where
/// the name is the project path with every character other than an ASCII letter or digit
/// replaced by '-' (D:\Projects\Unity\purge-squad becomes D--Projects-Unity-purge-squad).
/// </summary>
public static class ClaudeProjects
{
    public static string DirectoryName(string projectPath)
    {
        string trimmed = projectPath.TrimEnd('\\', '/');
        var name = new StringBuilder(trimmed.Length);
        foreach (char c in trimmed)
        {
            name.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        }

        return name.ToString();
    }

    /// <summary>
    /// The transcript folder for a project. <paramref name="configDir"/> defaults to
    /// CLAUDE_CONFIG_DIR, then ~/.claude, the same lookup Claude Code does.
    /// </summary>
    public static string TranscriptDirectory(string projectPath, string? configDir = null)
    {
        configDir ??= Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrEmpty(configDir))
        {
            configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        }

        return Path.Combine(configDir, "projects", DirectoryName(Path.GetFullPath(projectPath)));
    }
}
