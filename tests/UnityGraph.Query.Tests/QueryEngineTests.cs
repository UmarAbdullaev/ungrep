using System.Text;
using UnityGraph.Core.Graph;

namespace UnityGraph.Query.Tests;

/// <summary>`find`, `refs` and `explain` answers on the fixture graph (SPEC-query, Commands).</summary>
public sealed class QueryEngineTests(QueryFixture fixture) : IClassFixture<QueryFixture>
{
    private const int Budget = 1200;

    private string Ask(string command)
    {
        using GraphStore store = fixture.Open();
        var engine = new QueryEngine(store);
        string[] words = command.Split(' ', 2);
        string rest = words.Length > 1 ? words[1] : "";
        return words[0] switch
        {
            "find" when rest.Split(" --kind ") is [var text, var kind] => engine.Find(text, KindNames.ParseNode(kind), Budget),
            "find" => engine.Find(rest, null, Budget),
            "refs" => engine.Refs(rest, Budget),
            "explain" when rest.EndsWith(" --members", StringComparison.Ordinal) => engine.Explain(rest[..^" --members".Length], true, Budget),
            "explain" => engine.Explain(rest, false, Budget),
            _ => throw new ArgumentException(command),
        };
    }

    [Fact]
    public void Answers_on_the_fixture()
    {
        string[] commands =
        [
            "find Turntable",
            "find turn --kind field",
            "find Open",
            "find Nothing",
            "refs Turntable.Stop",
            "refs Turntable.Rotate",
            "refs Game.Core.Stage",
            "refs LabStage",
            "refs ILabDebug",
            "refs ILabDebug.Trace",
            "refs Pool",
            "refs Pool.Release",
            "refs OnOpened",
            "explain LabStage",
            "explain LabStage --members",
            "explain Game.Lab.ILabDebug",
            "explain Stage.Open",
            "explain Turntable.Rotate(float)",
            "explain Turntable._speed",
            "explain Turntable.Tint",
            "explain labstage.resetstage",
            "explain Trace",
            "explain Nope.Missing",
            "refs cs:T:Game.Lab.LabStage",
            "refs cs:T:Game.Lab.Gone",
        ];

        var transcript = new StringBuilder();
        foreach (string command in commands)
        {
            transcript.Append("$ ug ").Append(command).Append('\n').Append(Ask(command)).Append('\n');
        }

        Golden.Match(transcript.ToString());
    }

    [Fact]
    public void A_small_budget_cuts_on_a_line_and_names_a_bigger_one()
    {
        using GraphStore store = fixture.Open();

        string text = new QueryEngine(store).Explain("LabStage", members: true, budget: 200);

        Assert.True(Encoding.UTF8.GetByteCount(text) <= 200 * 4, text);
        Assert.EndsWith("  more  ug explain LabStage --members --budget 400\n", text, StringComparison.Ordinal);
        Assert.StartsWith("Game.Lab.LabStage  class", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_graph_same_answer()
    {
        Assert.Equal(Ask("explain LabStage --members"), Ask("explain LabStage --members"));
    }
}
