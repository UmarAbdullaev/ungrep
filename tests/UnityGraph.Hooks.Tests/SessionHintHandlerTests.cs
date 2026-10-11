using System.Text.Json;
using UnityGraph.Core;

namespace UnityGraph.Hooks.Tests;

/// <summary>
/// The SessionStart hint (B1f): one line of context in a Unity project, nothing elsewhere or with
/// the query module off, and within its share of the plugin's idle budget (SPEC principle 3).
/// </summary>
public sealed class SessionHintHandlerTests : IDisposable
{
    private const int HintBudget = 80;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-hint-" + Guid.NewGuid().ToString("N"));

    public SessionHintHandlerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Assets"));
        Directory.CreateDirectory(Path.Combine(_root, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_root, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.3.14f1\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Run(string eventName, string cwd)
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Payloads", "SessionStart.json"))
            .Replace("<root>", JsonEncodedText.Encode(cwd.Replace('\\', '/')).ToString(), StringComparison.Ordinal);
        var output = new StringWriter();
        new HookDispatcher(HookHandlers.All).Run(eventName, new StringReader(json), output, cwd);
        return output.ToString();
    }

    [Fact]
    public void Session_start_in_a_unity_project_adds_the_hint_as_context()
    {
        using JsonDocument doc = JsonDocument.Parse(Run("SessionStart", _root));

        JsonElement hook = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("SessionStart", hook.GetProperty("hookEventName").GetString());
        Assert.Equal(SessionHintHandler.Hint, hook.GetProperty("additionalContext").GetString());
    }

    [Fact]
    public void The_hint_stays_within_its_budget()
    {
        int tokens = (System.Text.Encoding.UTF8.GetByteCount(SessionHintHandler.Hint) + 3) / 4;
        Assert.True(tokens <= HintBudget, $"the session hint is {tokens} tokens, over the {HintBudget} budget");
    }

    [Fact]
    public void Silent_with_the_query_module_off()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".claude"));
        File.WriteAllText(Path.Combine(_root, UnityGraphConfig.RelativePath), """{ "modules": { "query": false } }""");

        Assert.Equal("", Run("SessionStart", _root));
    }

    [Fact]
    public void Silent_outside_a_unity_project()
    {
        string other = Path.Combine(Path.GetTempPath(), "ug-hint-none-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        try
        {
            Assert.Equal("", Run("SessionStart", other));
        }
        finally
        {
            Directory.Delete(other);
        }
    }

    [Fact]
    public void Other_events_get_no_hint()
    {
        Assert.False(new SessionHintHandler().Handles(HookInput.Parse("UserPromptSubmit", "{}")));
    }
}
