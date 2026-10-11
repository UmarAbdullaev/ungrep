namespace Ungrep.Cli.Tests;

/// <summary>
/// The `unity` skill's budgets (SPEC, Performance budgets): its body, everything after the front
/// matter, is at most 600 tokens counted as bytes / 4, and the line Claude Code lists every
/// session for it at most 300. Option lists belong in `ug &lt;cmd&gt; --help`.
/// </summary>
public sealed class SkillBudgetTests
{
    private const int Budget = 600;

    private const int ListingBudget = 300;

    private static (string FrontMatter, string Body) Read()
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot(), "skills", "unity", "SKILL.md")).ReplaceLineEndings("\n");
        int end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        Assert.True(text.StartsWith("---\n", StringComparison.Ordinal) && end > 0, "skills/unity/SKILL.md has no front matter");
        return (text[4..end], text[(end + 5)..]);
    }

    [Fact]
    public void The_unity_skill_body_stays_within_its_token_budget()
    {
        int bytes = System.Text.Encoding.UTF8.GetByteCount(Read().Body);
        int tokens = (bytes + 3) / 4;
        Assert.True(tokens <= Budget, $"skills/unity/SKILL.md body is {tokens} tokens ({bytes} bytes / 4), over the {Budget} budget");
    }

    [Fact]
    public void The_skill_listing_line_stays_within_the_idle_budget()
    {
        var fields = Read().FrontMatter.Split('\n')
            .Select(line => line.Split(':', 2))
            .ToDictionary(kv => kv[0], kv => kv[1].Trim(), StringComparer.Ordinal);
        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), ".claude-plugin", "plugin.json")));

        // What the Skill tool lists every session, whether the skill is used or not.
        string line = $"- {manifest.RootElement.GetProperty("name").GetString()}:{fields["name"]}: {fields["description"]}\n";
        int bytes = System.Text.Encoding.UTF8.GetByteCount(line);
        int tokens = (bytes + 3) / 4;
        Assert.True(tokens <= ListingBudget, $"the skill listing line is {tokens} tokens ({bytes} bytes / 4), over the {ListingBudget} budget");
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ungrep.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("ungrep.sln not found above " + AppContext.BaseDirectory);
    }
}
