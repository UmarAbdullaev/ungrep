namespace UnityGraph.Cli.Tests;

/// <summary>
/// The `unity` skill's budget (SPEC, Performance budgets): its body, everything after the front
/// matter, is at most 600 tokens counted as bytes / 4. Option lists belong in `ug &lt;cmd&gt; --help`.
/// </summary>
public sealed class SkillBudgetTests
{
    private const int Budget = 600;

    [Fact]
    public void The_unity_skill_body_stays_within_its_token_budget()
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot(), "skills", "unity", "SKILL.md")).ReplaceLineEndings("\n");
        int end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        Assert.True(text.StartsWith("---\n", StringComparison.Ordinal) && end > 0, "skills/unity/SKILL.md has no front matter");

        int bytes = System.Text.Encoding.UTF8.GetByteCount(text[(end + 5)..]);
        int tokens = (bytes + 3) / 4;
        Assert.True(tokens <= Budget, $"skills/unity/SKILL.md body is {tokens} tokens ({bytes} bytes / 4), over the {Budget} budget");
    }

    internal static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "unitygraph.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("unitygraph.sln not found above " + AppContext.BaseDirectory);
    }
}
