using System.Text;
using System.Text.Json;
using Ungrep.Code;
using Ungrep.Core;
using Ungrep.Core.Graph;

namespace Ungrep.Query.Tests;

/// <summary>`outline` and `impact` answers on the fixture graph (SPEC-query, `ug outline`, `ug impact`).</summary>
public sealed class OutlineImpactTests(QueryFixture fixture) : IClassFixture<QueryFixture>
{
    private const int Budget = 1200;

    private string Ask(string command, int budget = Budget)
    {
        using GraphStore store = fixture.Open();
        var engine = new QueryEngine(store, fixture.Root);
        string[] words = command.Split(' ', 2);
        bool json = words[1].EndsWith(" --json", StringComparison.Ordinal);
        string argument = json ? words[1][..^" --json".Length] : words[1];
        return words[0] switch
        {
            "outline" => engine.Outline(argument, json, budget),
            "impact" => engine.Impact(argument, json, budget),
            _ => throw new ArgumentException(command),
        };
    }

    [Fact]
    public void Answers_on_the_fixture()
    {
        string[] commands =
        [
            "outline Turntable.cs",
            "outline Assets/Scripts/Lab/LabStage.cs",
            "outline LabStage.Debug.cs",
            "outline Assets/Scripts/Core/Pool.cs",
            "outline Assets/Scripts/Core/Stage.cs",
            "outline Assets/Scripts/Broken.cs",
            "outline Lab.unity",
            "outline Turntable.prefab",
            "outline Turntable Variant.prefab",
            "outline LabSettings.asset",
            "outline Turntable",
            "outline Assets/Scripts/Nowhere.cs",
            "outline ../Outside.cs",
            "impact Turntable",
            "impact Turntable.Rotate",
            "impact Turntable.Rotate(float)",
            "impact Turntable.OnOpened",
            "impact Turntable._speed",
            "impact Turntable.Tint",
            "impact Turntable.Rotated",
            "impact LabStage",
            "impact LabStage.Open",
            "impact LabStage.Spin",
            "impact StageData",
            "impact Game.Core.Stage",
            "impact Stage.Open",
            "impact ILabDebug",
            "impact ILabDebug.Trace",
            "impact Pool",
            "impact Pool.Release",
            "impact Trace",
            "impact Turntable.cs",
            "impact Turntable.prefab",
            "impact Nope.Missing",
            "impact cs:T:Game.Lab.Gone",
            "outline Turntable.cs --json",
            "impact Turntable --json",
            "impact Turntable._speed --json",
            "impact Nothing --json",
            "impact IOpenable",
            "impact Spectator.OnRotated",
            "impact Spectator._cheered",
            "impact LabStage.Count",
        ];

        var transcript = new StringBuilder();
        foreach (string command in commands)
        {
            transcript.Append("$ ug ").Append(command).Append('\n').Append(Ask(command)).Append('\n');
        }

        Golden.Match(transcript.ToString());
    }

    [Fact]
    public void An_ambiguous_name_lists_the_candidates_and_a_key_picks_one()
    {
        string ambiguous = Ask("impact Trace");

        Assert.StartsWith("ambiguous: Trace", ambiguous, StringComparison.Ordinal);
        Assert.Contains("Game.Lab.ILabDebug.Trace", ambiguous, StringComparison.Ordinal);
        Assert.StartsWith("Game.Lab.ILabDebug.Trace(int)  method", Ask("impact Game.Lab.ILabDebug.Trace"), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_symbol_says_so_and_a_missing_file_says_so()
    {
        Assert.StartsWith("not found: Nope", Ask("impact Nope"), StringComparison.Ordinal);
        Assert.StartsWith("not found: Assets/Scripts/Nowhere.cs", Ask("outline Assets/Scripts/Nowhere.cs"), StringComparison.Ordinal);
        Assert.StartsWith("outside the project:", Ask("outline ../Outside.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_type_name_given_to_outline_points_at_explain()
    {
        string text = Ask("outline Turntable");

        Assert.StartsWith("not a file: Turntable", text, StringComparison.Ordinal);
        Assert.Contains("ug explain Turntable --members", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_small_budget_cuts_on_a_line_and_names_a_bigger_one()
    {
        string text = Ask("impact LabStage.Count", budget: 200);
        Assert.DoesNotContain("more lines", text, StringComparison.Ordinal);

        string outline = Ask("outline LabStage.cs", budget: 200);
        Assert.Matches(@"  more  \d+ more lines: ug outline LabStage.cs --budget 400
$", outline);
    }

    [Fact]
    public void A_group_over_ten_rows_shows_ten_and_counts_the_rest()
    {
        string path = Path.Combine(fixture.Root, "Assets", "Scripts", "Many.cs");
        string callers = string.Concat(Enumerable.Range(0, 13).Select(i => $"    void Call{i:00}() {{ Game.Core.Many.Target(); }}\n"));
        File.WriteAllText(path, "namespace Game.Core\n{\n    public static class Many\n    {\n        public static void Target() { }\n" + callers + "    }\n}\n");
        try
        {
            using GraphStore store = GraphStore.Open(fixture.Root, StoreMode.ReadWrite);
            CodeIndex.Update(CodeWorkspace.Load(fixture.Root, VendoredFolders.Default), store);
            string text = new QueryEngine(store, fixture.Root).Impact("Many.Target", json: false, Budget);

            Assert.Contains("  callers  13", text, StringComparison.Ordinal);
            Assert.Contains("    +3 more  ug impact Many.Target --all\n", text, StringComparison.Ordinal);
            Assert.Equal(10, text.Split('\n').Count(l => l.Contains("Game.Core.Many.Call", StringComparison.Ordinal)));
        }
        finally
        {
            File.Delete(path);
            using GraphStore store = GraphStore.Open(fixture.Root, StoreMode.ReadWrite);
            CodeIndex.Update(CodeWorkspace.Load(fixture.Root, VendoredFolders.Default), store);
        }
    }

    [Fact]
    public void Json_answers_parse_and_stay_within_the_budget()
    {
        using JsonDocument impact = JsonDocument.Parse(Ask("impact Turntable --json"));
        JsonElement groups = impact.RootElement.GetProperty("groups");

        Assert.Equal("cs:T:Game.Lab.Turntable", impact.RootElement.GetProperty("symbol")[0].GetProperty("key").GetString());
        Assert.Contains(groups.EnumerateArray(), g => g.GetProperty("label").GetString() == "type uses" && g.GetProperty("rows").GetArrayLength() == 7);
        Assert.False(impact.RootElement.GetProperty("cut").GetBoolean());

        string cut = Ask("impact Turntable --json", budget: 200);
        using JsonDocument cutDocument = JsonDocument.Parse(cut);
        Assert.True(Encoding.UTF8.GetByteCount(cut) <= 200 * 4, cut);
        Assert.True(cutDocument.RootElement.GetProperty("cut").GetBoolean());

        using JsonDocument outline = JsonDocument.Parse(Ask("outline Turntable.cs --json"));
        Assert.Equal("Assets/Scripts/Lab/Turntable.cs", outline.RootElement.GetProperty("file").GetString());

        using JsonDocument error = JsonDocument.Parse(Ask("impact Nothing --json"));
        Assert.StartsWith("not found: Nothing", error.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Same_graph_same_answer()
    {
        Assert.Equal(Ask("impact LabStage"), Ask("impact LabStage"));
        Assert.Equal(Ask("outline Lab.unity --json"), Ask("outline Lab.unity --json"));
    }
}
