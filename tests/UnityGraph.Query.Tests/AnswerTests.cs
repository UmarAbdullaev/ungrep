using UnityGraph.Core.Graph;

namespace UnityGraph.Query.Tests;

/// <summary>SPEC-query, Budget; and the name helpers answers are built from.</summary>
public sealed class AnswerTests
{
    [Fact]
    public void Lines_go_in_whole_until_the_budget()
    {
        var answer = new Answer(200);

        Assert.True(answer.Add(new string('a', 500)));
        Assert.True(answer.Add(new string('b', 150)));
        Assert.False(answer.Add(new string('c', 100)));
        Assert.False(answer.Add("d"));

        Assert.Equal(new string('a', 500) + "\n" + new string('b', 150) + "\n  more  ug x --budget 400\n", answer.Finish("ug x"));
    }

    [Fact]
    public void The_first_line_always_fits()
    {
        var answer = new Answer(200);

        Assert.True(answer.Add(new string('a', 2000)));
        Assert.False(answer.Cut);
    }

    [Fact]
    public void At_the_largest_budget_the_more_line_asks_for_a_narrower_question()
    {
        var answer = new Answer(Answer.MaxBudget);
        answer.AddRange(Enumerable.Repeat(new string('a', 1000), 40));

        Assert.EndsWith("  more  (cut at the largest budget; ask a narrower question)\n", answer.Finish("ug x"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(199)]
    [InlineData(8001)]
    public void Budget_outside_the_range_throws(int budget) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Answer(budget));

    [Fact]
    public void Table_pads_every_column_but_the_last()
    {
        string[] lines = [.. Answer.Table([["a", "bbb", "x"], ["cc", "d", ""]], "  ")];

        Assert.Equal(["  a   bbb  x", "  cc  d"], lines);
        Assert.Empty(Answer.Table([]));
    }

    [Theory]
    [InlineData("cs:T:Game.Lab.LabStage", "Game.Lab.LabStage")]
    [InlineData("cs:M:Game.Core.Pool`1.Release(`0)", "Game.Core.Pool.Release")]
    [InlineData("cs:M:Game.Util.Make``1(System.Int32)", "Game.Util.Make")]
    [InlineData("cs:F:Outer`2.Inner`1.value", "Outer.Inner.value")]
    [InlineData("a:5a1b2c3d4e5f60718293a4b5c6d7e8f9", "a:5a1b2c3d4e5f60718293a4b5c6d7e8f9")]
    [InlineData("cs:", "cs:")]
    public void Qualified_names_drop_prefix_parameters_and_arity(string key, string qualified) =>
        Assert.Equal(qualified, Names.Qualified(key));

    [Theory]
    [InlineData("LabStage.Open", "LabStage.Open")]
    [InlineData("cs:T:A`1", "'cs:T:A`1'")]
    [InlineData("Rotate(float)", "'Rotate(float)'")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("", "''")]
    public void Arguments_are_quoted_for_the_shell_only_when_needed(string text, string quoted) =>
        Assert.Equal(quoted, Names.Quote(text));

    [Fact]
    public void Props_that_are_not_json_read_as_absent()
    {
        var node = new Node("cs:T:A", NodeKind.Type, "A", "A.cs", 1, 2, "{not json");

        Assert.Null(Names.Prop(node, "subkind"));
        Assert.Empty(Names.List(node, "former"));
        Assert.Equal("type", Names.Kind(node));
    }

    [Fact]
    public void Join_cuts_at_eight()
    {
        Assert.Equal("(none)", Names.Join([]));
        Assert.Equal("1, 2, 3, 4, 5, 6, 7, 8 (+2)", Names.Join([.. Enumerable.Range(1, 10).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))]));
    }
}
