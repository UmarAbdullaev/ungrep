using System.Text.Json;
using UnityGraph.Stats;

namespace UnityGraph.Cli.Tests;

/// <summary>
/// The worker subagent's file (SPEC-worker-agent). What every session pays for the plugin: the
/// skill's listing line and the agent's listing line, at most 300 tokens together (bytes / 4,
/// SPEC Overhead budget). The front matter keeps to what a plugin agent supports, and the prompt
/// keeps the verdict contract.
/// </summary>
public sealed class AgentBudgetTests
{
    private const int Budget = 300;

    // Claude Code's docs (plugins/components, 2026-10-11): fields a plugin agent supports.
    private static readonly string[] _pluginAgentFields =
    [
        "name", "description", "model", "effort", "maxTurns", "tools", "disallowedTools", "skills",
        "memory", "background", "omitClaudeMd", "isolation", "color",
    ];

    private static (Dictionary<string, List<string>> Fields, string Body) Read(params string[] path)
    {
        string text = File.ReadAllText(Path.Combine([SkillBudgetTests.RepoRoot(), .. path])).ReplaceLineEndings("\n");
        int end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        Assert.True(text.StartsWith("---\n", StringComparison.Ordinal) && end > 0, string.Join('/', path) + " has no front matter");

        var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? last = null;
        foreach (string line in text[4..end].Split('\n'))
        {
            if (line.StartsWith("  - ", StringComparison.Ordinal) && last is not null)
            {
                last.Add(line[4..].Trim());
                continue;
            }

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            Assert.True(colon > 0, "not a front matter line: " + line);
            string value = line[(colon + 1)..].Trim();
            last = value.Length == 0 ? [] : [value];
            fields.Add(line[..colon], last);
        }

        return (fields, text[(end + 5)..]);
    }

    private static string PluginName()
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(SkillBudgetTests.RepoRoot(), ".claude-plugin", "plugin.json")));
        return manifest.RootElement.GetProperty("name").GetString()!;
    }

    [Fact]
    public void Skill_and_agent_descriptions_stay_within_the_idle_budget()
    {
        string plugin = PluginName();
        var (skill, _) = Read("skills", "unity", "SKILL.md");
        var (agent, _) = Read("agents", "unity-worker.md");

        // The lines Claude Code lists every session: the Skill tool's and the Agent tool's.
        string skillLine = $"- {plugin}:{skill["name"][0]}: {skill["description"][0]}\n";
        string agentLine = $"- {plugin}:{agent["name"][0]}: {agent["description"][0]} (Tools: {agent["tools"][0]})\n";
        int bytes = System.Text.Encoding.UTF8.GetByteCount(skillLine + agentLine);
        int tokens = (bytes + 3) / 4;

        Assert.True(tokens <= Budget, $"skill and agent listing lines are {tokens} tokens ({bytes} bytes / 4), over the {Budget} budget");
    }

    [Fact]
    public void The_agent_file_keeps_to_what_a_plugin_agent_supports()
    {
        var (agent, _) = Read("agents", "unity-worker.md");

        Assert.Empty(agent.Keys.Except(_pluginAgentFields));
        Assert.Equal(WorkerDelegations.AgentName, agent["name"][0]);
        // SPEC Decisions #6: Sonnet by default.
        Assert.Equal("sonnet", agent["model"][0]);
        // ug through Bash, files and pictures through Read; no edits to project files, no nested agents.
        Assert.Equal(["Bash", "Read", "Grep"], agent["tools"][0].Split(',', StringSplitOptions.TrimEntries));
        Assert.Equal([PluginName() + ":unity"], agent["skills"]);
        Assert.Contains("3+", agent["description"][0], StringComparison.Ordinal);
        Assert.Contains("800 tokens", agent["description"][0], StringComparison.Ordinal);
    }

    [Fact]
    public void The_agent_prompt_carries_the_verdict_contract()
    {
        var (_, body) = Read("agents", "unity-worker.md");

        foreach (string line in new[] { "verdict:", "did:", "checked:", "evidence:", "left:", "state:", "recheck:" })
        {
            Assert.Contains("\n" + line + " ", body, StringComparison.Ordinal);
        }

        Assert.Contains("at most 800 tokens", body, StringComparison.Ordinal);
        Assert.Contains("worker-agent is off", body, StringComparison.Ordinal);
        Assert.Contains("${CLAUDE_PLUGIN_ROOT}/bin/ug", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_skill_points_at_the_agent_and_its_guide()
    {
        var (_, body) = Read("skills", "unity", "SKILL.md");

        Assert.Contains($"`{PluginName()}:{WorkerDelegations.AgentName}`", body, StringComparison.Ordinal);
        Assert.Contains("`worker.md`", body, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(SkillBudgetTests.RepoRoot(), "skills", "unity", "worker.md")));
    }
}
