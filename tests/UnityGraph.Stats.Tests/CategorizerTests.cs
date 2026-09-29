using System.Text.Json;

namespace UnityGraph.Stats.Tests;

/// <summary>
/// One case per category of research/transcript-analysis/usage.js, plus the precedence cases
/// where a call could match more than one.
/// </summary>
public sealed class CategorizerTests
{
    [Theory]
    // Unity MCP: the short name after the server prefix picks the category.
    [InlineData("mcp__UnityMCP__read_console", "{}", "unity: console/logs")]
    [InlineData("mcp__UnityMCP__run_tests", "{}", "unity: tests")]
    [InlineData("mcp__UnityMCP__refresh_unity", "{}", "unity: compile/refresh/status")]
    [InlineData("mcp__UnityMCP__manage_editor_play", "{}", "unity: play/screenshot")]
    [InlineData("mcp__unity_pipeline__eval", "{}", "unity: eval/run_script")]
    [InlineData("mcp__UnityMCP__manage_gameobject", "{}", "unity: scene/asset inspect+edit")]
    [InlineData("mcp__UnityMCP__set_active_instance", "{}", "unity: other mcp")]
    [InlineData("mcp__claude-in-chrome__navigate", "{}", "browser")]
    [InlineData("mcp__codegraph__codegraph_explore", "{}", "other mcp")]
    // Unity CLI through the shell: the subcommand picks the category.
    [InlineData("Bash", """{"command":"unity command console --lines 50"}""", "unity: console/logs")]
    [InlineData("PowerShell", """{"command":"unity.exe command --project-path \"D:\\p\" run_tests"}""", "unity: tests")]
    [InlineData("Bash", """{"command":"unity command compile"}""", "unity: compile/refresh/status")]
    [InlineData("Bash", """{"command":"unity command screenshot"}""", "unity: play/screenshot")]
    [InlineData("Bash", """{"command":"unity command eval \"1+1\""}""", "unity: eval/run_script")]
    [InlineData("Bash", """{"command":"unity command selection"}""", "unity: other cli")]
    // Shell.
    [InlineData("PowerShell", """{"command":"Get-Content $env:LOCALAPPDATA\\Unity\\Editor\\Editor.log -Tail 40"}""", "unity: console/logs")]
    [InlineData("Bash", """{"command":"git diff --stat"}""", "shell: git")]
    [InlineData("Bash", """{"command":"grep -n m_Script Assets/Scenes/Hub.unity"}""", "read/search: unity yaml")]
    [InlineData("Bash", """{"command":"rg -n PlayerMotor Assets/Scripts"}""", "shell: search/read")]
    [InlineData("PowerShell", """{"command":"Start-Sleep -Seconds 5"}""", "shell: waiting")]
    [InlineData("Bash", """{"command":"dotnet build"}""", "shell: other")]
    // Read.
    [InlineData("Read", """{"file_path":"D:\\p\\Assets\\Prefabs\\Gun.prefab"}""", "read/search: unity yaml")]
    [InlineData("Read", """{"file_path":"D:\\p\\Assets\\Scripts\\Gun.cs"}""", "read: .cs")]
    [InlineData("Read", """{"file_path":"D:\\p\\CODEMAP.md"}""", "read: .md (plan/map/docs)")]
    [InlineData("Read", """{"file_path":"C:\\tmp\\shot.PNG"}""", "read: image")]
    [InlineData("Read", """{"file_path":"D:\\p\\Packages\\manifest.json"}""", "read: other")]
    // Search.
    [InlineData("Grep", """{"pattern":"m_Script","glob":"*.prefab"}""", "read/search: unity yaml")]
    [InlineData("Glob", """{"pattern":"**/*Player*.cs"}""", "search: grep/glob")]
    // Write.
    [InlineData("Edit", """{"file_path":"D:\\p\\Assets\\Scripts\\Gun.cs"}""", "write: .cs")]
    [InlineData("Write", """{"file_path":"D:\\p\\PLAN.md"}""", "write: .md")]
    [InlineData("MultiEdit", """{"file_path":"D:\\p\\Assets\\Scenes\\Hub.unity"}""", "write: other")]
    // Everything else.
    [InlineData("Agent", "{}", "subagent")]
    [InlineData("Task", "{}", "subagent")]
    [InlineData("ToolSearch", "{}", "toolsearch")]
    [InlineData("TodoWrite", "{}", "other: TodoWrite")]
    public void Categorizes_like_the_prototype(string tool, string input, string expected)
    {
        using var doc = JsonDocument.Parse(input);

        Assert.Equal(expected, Categorizer.Categorize(tool, doc.RootElement));
    }

    [Fact]
    public void Missing_input_is_treated_as_empty()
    {
        Assert.Equal("read: other", Categorizer.Categorize("Read", default));
        Assert.Equal("search: grep/glob", Categorizer.Categorize("Grep", default));
        Assert.Equal("shell: other", Categorizer.Categorize("Bash", default));
    }
}
