using System.Globalization;
using System.Text.RegularExpressions;
using UnityGraph.Assets;
using UnityGraph.Code;
using UnityGraph.Core;
using UnityGraph.Core.Graph;

namespace UnityGraph.Query.Tests;

/// <summary>
/// `--all` and `--from` (SPEC-query, Nothing out of reach): a capped answer names the command that
/// lifts the cap, the default stays as it was, and pages of a cut answer join up.
/// </summary>
public sealed partial class NothingOutOfReachTests(QueryFixture fixture) : IClassFixture<QueryFixture>
{
    private const int Budget = 1200;

    // Writes a script into the fixture, indexes it, runs the test and takes the script out again.
    private void With(string name, string source, Action<GraphStore> test)
    {
        string path = Path.Combine(fixture.Root, "Assets", "Scripts", name);
        File.WriteAllText(path, source);
        try
        {
            using GraphStore store = GraphStore.Open(fixture.Root, StoreMode.ReadWrite);
            CodeIndex.Update(CodeWorkspace.Load(fixture.Root, VendoredFolders.Default), store);
            test(store);
        }
        finally
        {
            File.Delete(path);
            using GraphStore store = GraphStore.Open(fixture.Root, StoreMode.ReadWrite);
            CodeIndex.Update(CodeWorkspace.Load(fixture.Root, VendoredFolders.Default), store);
        }
    }

    [Fact]
    public void Find_past_fifty_names_all_and_all_lists_every_match()
    {
        string methods = string.Concat(Enumerable.Range(0, 55).Select(i => $"        void Widget{i:00}() {{ }}\n"));
        With("Widgets.cs", "namespace Game.Core\n{\n    public class Widgets\n    {\n" + methods + "    }\n}\n", store =>
        {
            string capped = new QueryEngine(store).Find("Widget", null, 8000);
            Assert.EndsWith("50+ found, narrow the text or add --kind; all: ug find Widget --all\n", capped, StringComparison.Ordinal);

            string all = new QueryEngine(store) { All = true }.Find("Widget", null, 8000);
            Assert.Matches(FoundLine(), all);
            Assert.True(int.Parse(FoundLine().Match(all).Groups[1].Value, CultureInfo.InvariantCulture) >= 55);
            Assert.DoesNotContain("--all", all, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void An_ambiguous_name_past_twenty_candidates_names_all()
    {
        string types = string.Concat(Enumerable.Range(0, 22).Select(i => $"    public class Dup{i:00} {{ public void Clash() {{ }} }}\n"));
        With("Dups.cs", "namespace Game.Core\n{\n" + types + "}\n", store =>
        {
            string capped = new QueryEngine(store).Refs("Clash", 8000);
            Assert.Contains("\n  +2 more candidates: ug refs Clash --all\n", capped, StringComparison.Ordinal);
            Assert.Equal(20, capped.Split('\n').Count(l => l.StartsWith("  Game.Core.Dup", StringComparison.Ordinal)));

            string all = new QueryEngine(store) { All = true }.Refs("Clash", 8000);
            Assert.Equal(22, all.Split('\n').Count(l => l.StartsWith("  Game.Core.Dup", StringComparison.Ordinal)));
            Assert.DoesNotContain("more candidates", all, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Past_two_thousand_edges_each_command_says_so_and_all_pages_through_every_one()
    {
        string callers = string.Concat(Enumerable.Range(0, 2001).Select(i => $"        static void Call{i:0000}() {{ Target(); }}\n"));
        With("Crowd.cs", "namespace Game.Core\n{\n    public static class Crowd\n    {\n        public static void Target() { }\n" + callers + "    }\n}\n", store =>
        {
            // The default reads 2,000 edges; its last page says so and names --all.
            (List<string> some, string end) = Pages(from => new QueryEngine(store) { From = from }.Refs("Crowd.Target", 8000), "ug refs Crowd.Target");
            Assert.Matches(@"^\d+\+ references: ug refs Crowd\.Target --all$", end);
            Assert.InRange(some.Count, 1990, 2000);

            var capped = new QueryEngine(store);
            string explain = capped.Explain("Crowd.Target", false, Budget);
            Assert.Contains("\n  note  cut: edges past 2000: ug explain Crowd.Target --all\n", explain, StringComparison.Ordinal);
            Assert.EndsWith("  more  ug explain Crowd.Target --all | ug refs Crowd.Target\n", explain, StringComparison.Ordinal);

            string impact = capped.Impact("Crowd.Target", false, 8000);
            Assert.Matches(@"\n    \+\d+ more  ug impact Crowd\.Target --all\n", impact);
            Assert.Contains("  note     cut: edges past 2000: ug impact Crowd.Target --all\n", impact, StringComparison.Ordinal);

            // --all reads every edge; pages at the largest budget join up.
            (List<string> rows, string last) = Pages(from => new QueryEngine(store) { All = true, From = from }.Refs("Crowd.Target", 8000), "ug refs Crowd.Target --all");
            Assert.Equal("2001 references", last);
            Assert.Equal(2001, rows.Count);
            Assert.Equal(2001, rows.Distinct(StringComparer.Ordinal).Count());
        });
    }

    // Follows `more  <command> --budget 8000 --from <n>` from page to page: the `called from` rows and the last line.
    private static (List<string> Rows, string Last) Pages(Func<int, string> ask, string command)
    {
        var rows = new List<string>();
        int from = 0;
        for (int page = 0; page < 20; page++)
        {
            string[] lines = ask(from).TrimEnd('\n').Split('\n');
            rows.AddRange(lines.Where(l => l.StartsWith("  called from", StringComparison.Ordinal)));
            Match next = NextPage().Match(lines[^1]);
            if (!next.Success)
            {
                Assert.True(page > 0, "the whole list fits no single answer");
                return (rows, lines[^1]);
            }

            Assert.Equal(command, next.Groups[1].Value);
            from = int.Parse(next.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        throw new InvalidOperationException("more than 20 pages");
    }

    [Fact]
    public void Held_via_past_three_levels_is_said_and_all_walks_on()
    {
        string chain = "    [System.Serializable] public class Link0 { public int value; }\n"
            + string.Concat(Enumerable.Range(1, 5).Select(i => $"    [System.Serializable] public class Link{i} {{ public Link{i - 1} next; }}\n"));
        With("Chain.cs", "namespace Game.Core\n{\n" + chain + "}\n", store =>
        {
            string capped = new QueryEngine(store).Impact("Link0", false, 8000);
            Assert.Contains("cut: held via past 3 levels: ug impact Link0 --all\n", capped, StringComparison.Ordinal);

            string all = new QueryEngine(store) { All = true }.Impact("Link0", false, 8000);
            Assert.DoesNotContain("cut:", all, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void From_skips_lines_and_past_the_end_says_how_long_the_answer_is()
    {
        using GraphStore store = fixture.Open();
        string whole = new QueryEngine(store).Explain("LabStage", true, 8000);
        string[] lines = whole.TrimEnd('\n').Split('\n');

        string rest = new QueryEngine(store) { From = 2 }.Explain("LabStage", true, 8000);
        Assert.Equal(string.Join('\n', lines.Skip(2)) + "\n", rest);

        string past = new QueryEngine(store) { From = 500 }.Explain("LabStage", true, 8000);
        Assert.Equal($"(nothing past line 500: the answer has {lines.Length} lines)\n", past);

        string json = new QueryEngine(store) { From = 2 }.Impact("LabStage", true, 8000);
        Assert.StartsWith("{\"symbol\":", json, StringComparison.Ordinal);
    }

    // A data asset holding the Turntable prefab four times, written into the fixture while the test runs.
    private const string Waves = """
        %YAML 1.1
        %TAG !u! tag:unity3d.com,2011:
        --- !u!114 &11400000
        MonoBehaviour:
          m_ObjectHideFlags: 0
          m_Script: {fileID: 11500000, guid: a7000000000000000000000000000007, type: 3}
          m_Name: Waves
          m_EditorClassIdentifier: Assembly-CSharp::Game.Data.Waves
          waves:
          - prefab: {fileID: 100, guid: b1000000000000000000000000000001, type: 3}
            count: 3
          - prefab: {fileID: 100, guid: b1000000000000000000000000000001, type: 3}
            count: 5
          - prefab: {fileID: 100, guid: b1000000000000000000000000000001, type: 3}
            count: 8
          - prefab: {fileID: 100, guid: b1000000000000000000000000000001, type: 3}
            count: 13

        """;

    [Fact]
    public void Refs_folds_four_asset_lines_from_one_file_and_all_lists_each()
    {
        string path = Path.Combine(fixture.Root, "Assets", "Data", "Waves.asset");
        File.WriteAllText(path, Waves);
        File.WriteAllText(path + ".meta", "fileFormatVersion: 2\nguid: f7000000000000000000000000000007\nNativeFormatImporter:\n  mainObjectFileID: 11400000\n");
        try
        {
            using (GraphStore writer = GraphStore.Open(fixture.Root, StoreMode.ReadWrite))
            {
                AssetIndex.Update(AssetWorkspace.Scan(fixture.Root, VendoredFolders.Default), writer, []);
            }

            Folds();
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".meta");
            using GraphStore store = GraphStore.Open(fixture.Root, StoreMode.ReadWrite);
            AssetIndex.Update(AssetWorkspace.Scan(fixture.Root, VendoredFolders.Default), store, []);
        }
    }

    private void Folds()
    {
        using GraphStore store = fixture.Open();

        string folded = new QueryEngine(store).Refs("Assets/Prefabs/Turntable.prefab", 8000);
        Assert.Matches(@"\n  referenced by +4 fields +Assets/Data/Waves\.asset:10-16\n", folded);
        Assert.EndsWith(", 4 folded into 1 line: ug refs 'Assets/Prefabs/Turntable.prefab' --all\n", folded, StringComparison.Ordinal);

        string all = new QueryEngine(store) { All = true }.Refs("Assets/Prefabs/Turntable.prefab", 8000);
        Assert.Equal(4, all.Split('\n').Count(l => l.Contains("Assets/Data/Waves.asset: waves[", StringComparison.Ordinal)));
        Assert.DoesNotContain("folded", all, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\n(\d+) found\n$")]
    private static partial Regex FoundLine();

    [GeneratedRegex(@"^  more  (.+) --budget 8000 --from (\d+)$")]
    private static partial Regex NextPage();
}
