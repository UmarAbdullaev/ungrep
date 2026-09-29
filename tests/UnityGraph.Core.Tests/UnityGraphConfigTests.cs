namespace UnityGraph.Core.Tests;

/// <summary>
/// `.claude/unitygraph.json`: defaults, overrides and the errors a bad file gives.
/// </summary>
public sealed class UnityGraphConfigTests
{
    [Fact]
    public void Defaults_turn_on_every_module_but_project_map()
    {
        UnityGraphConfig config = UnityGraphConfig.Default;

        Assert.Equal(1200, config.AnswerBudget);
        Assert.Empty(config.Overrides);
        Assert.False(config.IsEnabled("project-map"));
        Assert.All(UnityGraphConfig.ModuleDefaults.Keys.Where(m => m != "project-map"), m => Assert.True(config.IsEnabled(m)));
    }

    [Fact]
    public void Reads_overrides_with_comments_and_trailing_commas()
    {
        UnityGraphConfig config = UnityGraphConfig.Parse("""
            {
              "$schema": "https://example.invalid/unitygraph.schema.json",
              // quieter answers
              "answerBudget": 600,
              "modules": { "viewer": false, "project-map": true, "query": true, },
            }
            """);

        Assert.Equal(600, config.AnswerBudget);
        Assert.False(config.IsEnabled("viewer"));
        Assert.True(config.IsEnabled("project-map"));
        Assert.Equal(["project-map", "viewer"], config.Overrides);
    }

    [Theory]
    [InlineData("[]", "expected a JSON object")]
    [InlineData("{", "not valid JSON")]
    [InlineData("""{ "budget": 600 }""", "unknown setting 'budget'")]
    [InlineData("""{ "modules": { "graph": false } }""", "unknown module 'graph'")]
    [InlineData("""{ "modules": { "viewer": "off" } }""", "modules.viewer must be true or false")]
    [InlineData("""{ "modules": [] }""", "modules must be an object")]
    [InlineData("""{ "answerBudget": 100 }""", "answerBudget must be a whole number from 200 to 8000")]
    [InlineData("""{ "answerBudget": 9000 }""", "answerBudget must be")]
    [InlineData("""{ "answerBudget": 1.5 }""", "answerBudget must be")]
    public void Rejects_bad_files_naming_the_setting(string json, string message)
    {
        var e = Assert.Throws<FormatException>(() => UnityGraphConfig.Parse(json));
        Assert.StartsWith(message, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_module_names_throw()
    {
        Assert.Throws<ArgumentException>(() => UnityGraphConfig.Default.IsEnabled("graph"));
    }

    [Fact]
    public void Load_falls_back_to_defaults_without_a_file()
    {
        string root = Path.Combine(Path.GetTempPath(), "ug-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Same(UnityGraphConfig.Default, UnityGraphConfig.Load(root));

            Directory.CreateDirectory(Path.Combine(root, ".claude"));
            File.WriteAllText(Path.Combine(root, UnityGraphConfig.RelativePath), """{ "answerBudget": 2000 }""");
            Assert.Equal(2000, UnityGraphConfig.Load(root).AnswerBudget);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
