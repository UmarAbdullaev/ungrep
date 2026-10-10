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
    [InlineData("""{ "vendored": "Assets/Plugins/" }""", "vendored must be an array")]
    [InlineData("""{ "vendored": [1] }""", "vendored: 1 is not a string")]
    [InlineData("""{ "vendored": ["Assets/Plugins"] }""", "vendored: \"Assets/Plugins\" must end in '/'")]
    [InlineData("""{ "vendored": ["Assets\\Plugins/"] }""", """vendored: "Assets\Plugins/" use forward slashes""")]
    [InlineData("""{ "docs": false }""", "docs must be an object")]
    [InlineData("""{ "docs": { "offline": true } }""", "unknown setting 'docs.offline' (known: online)")]
    [InlineData("""{ "docs": { "online": "no" } }""", "docs.online must be true or false")]
    [InlineData("""{ "guards": "deny" }""", "guards must be an object")]
    [InlineData("""{ "guards": { "meta": "maybe" } }""", "guards.meta must be \"warn\", \"deny\" or \"off\"")]
    [InlineData("""{ "guards": { "rename": "warn" } }""", "unknown setting 'guards.rename' (known: field-removed, serialize-reference, meta, vendored, lint)")]
    [InlineData("""{ "guards": { "lint": "warn" } }""", "guards.lint must be an object")]
    [InlineData("""{ "guards": { "lint": { "mode": "loud" } } }""", "guards.lint.mode must be")]
    [InlineData("""{ "guards": { "lint": { "level": "warn" } } }""", "unknown setting 'guards.lint.level' (known: mode, patterns)")]
    [InlineData("""{ "guards": { "lint": { "patterns": {} } } }""", "guards.lint.patterns must be an array")]
    [InlineData("""{ "guards": { "lint": { "patterns": [ "Find" ] } } }""", "guards.lint.patterns[0] must be an object with a \"pattern\" string")]
    [InlineData("""{ "guards": { "lint": { "patterns": [ { "pattern": "(" } ] } } }""", "guards.lint.patterns[0].pattern is not a valid regex")]
    [InlineData("""{ "guards": { "lint": { "patterns": [ { "pattern": "x", "why": "y" } ] } } }""", "guards.lint.patterns[0]: only \"pattern\" and a string \"message\" are known")]
    public void Rejects_bad_files_naming_the_setting(string json, string message)
    {
        var e = Assert.Throws<FormatException>(() => UnityGraphConfig.Parse(json));
        Assert.StartsWith(message, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Docs_may_be_fetched_online_unless_the_file_says_not()
    {
        Assert.True(UnityGraphConfig.Default.DocsOnline);
        Assert.True(UnityGraphConfig.Parse("{}").DocsOnline);
        Assert.True(UnityGraphConfig.Parse("""{ "docs": {} }""").DocsOnline);
        Assert.True(UnityGraphConfig.Parse("""{ "docs": { "online": true } }""").DocsOnline);
        Assert.False(UnityGraphConfig.Parse("""{ "docs": { "online": false } }""").DocsOnline);
    }

    [Fact]
    public void Vendored_folders_default_or_come_from_the_file()
    {
        Assert.True(UnityGraphConfig.Default.VendoredIsDefault);
        Assert.True(UnityGraphConfig.Default.Vendored.Contains("assets/plugins/DOTween/Tween.cs"));

        UnityGraphConfig config = UnityGraphConfig.Parse("""{ "vendored": ["Assets/Ext/"] }""");

        Assert.False(config.VendoredIsDefault);
        Assert.Equal(["Assets/Ext/"], config.Vendored.Prefixes);
        Assert.False(config.Vendored.Contains("Assets/Plugins/A.cs"));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("/Assets/", "must be project-relative")]
    [InlineData("C:/Assets/", "must be project-relative")]
    [InlineData("Assets/../x/", "no '.' or '..' parts")]
    [InlineData("Assets/Plugins/", null)]
    public void Vendored_prefixes_are_checked(string prefix, string? problem)
    {
        Assert.Equal(problem, VendoredFolders.Problem(prefix));
        if (problem is not null)
        {
            Assert.Throws<ArgumentException>(() => new VendoredFolders([prefix]));
        }
    }

    [Fact]
    public void Guards_warn_by_default_and_read_modes_and_lint_patterns()
    {
        Assert.All(GuardSettings.Cases.Append("lint"), c => Assert.Equal(GuardMode.Warn, UnityGraphConfig.Default.Guards.ModeOf(c)));
        Assert.Empty(UnityGraphConfig.Default.Guards.LintPatterns);

        GuardSettings guards = UnityGraphConfig.Parse("""
            {
              "guards": {
                "meta": "deny",
                "vendored": "off",
                "lint": {
                  "mode": "deny",
                  "patterns": [
                    { "pattern": "GameObject\\.Find\\(", "message": "cache it" },
                    { "pattern": "SendMessage\\(" },
                  ],
                },
              },
            }
            """).Guards;

        Assert.Equal(GuardMode.Deny, guards.ModeOf("meta"));
        Assert.Equal(GuardMode.Off, guards.ModeOf("vendored"));
        Assert.Equal(GuardMode.Warn, guards.ModeOf("field-removed"));
        Assert.Equal(GuardMode.Deny, guards.ModeOf("lint"));
        Assert.Equal(["cache it", ""], guards.LintPatterns.Select(p => p.Message));
        Assert.Matches(guards.LintPatterns[0].Pattern, """var t = GameObject.Find("Turntable");""");
        Assert.Throws<ArgumentException>(() => guards.ModeOf("rename"));
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
