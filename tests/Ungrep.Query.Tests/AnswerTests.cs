using Ungrep.Core.Graph;

namespace Ungrep.Query.Tests;

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

    [Fact]
    public void A_paged_answer_skips_its_first_lines_and_names_the_next_page_at_the_largest_budget()
    {
        string line = new('x', 999);
        var first = new Answer(Answer.MaxBudget, from: 0);
        for (int i = 0; i < 40; i++)
        {
            first.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture) + line);
        }

        string text = first.Finish("ug refs Foo");
        Assert.EndsWith("  more  ug refs Foo --budget 8000 --from 31\n", text, StringComparison.Ordinal);

        var second = new Answer(Answer.MaxBudget, from: 31);
        for (int i = 0; i < 40; i++)
        {
            second.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture) + line);
        }

        string page = second.Finish("ug refs Foo");
        Assert.StartsWith("31x", page, StringComparison.Ordinal);
        Assert.DoesNotContain("more", page, StringComparison.Ordinal);
    }

    [Fact]
    public void A_paged_answer_below_the_largest_budget_keeps_its_from_in_the_more_line()
    {
        var answer = new Answer(Answer.MinBudget, from: 2);
        for (int i = 0; i < 40; i++)
        {
            answer.Add(new string('y', 60));
        }

        Assert.EndsWith("  more  ug find y --from 2 --budget 400\n", answer.Finish("ug find y"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_page_past_the_end_says_how_long_the_answer_is()
    {
        var answer = new Answer(1200, from: 5);
        answer.AddRange(["a", "b"]);

        Assert.Equal("(nothing past line 5: the answer has 2 lines)\n", answer.Finish("ug find a"));
    }

    [Fact]
    public void An_unpaged_answer_cut_at_the_largest_budget_still_asks_a_narrower_question()
    {
        var answer = new Answer(Answer.MaxBudget);
        for (int i = 0; i < 40; i++)
        {
            answer.Add(new string('z', 999));
        }

        Assert.EndsWith("  more  (cut at the largest budget; ask a narrower question)\n", answer.Finish("ug console"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_footer_comes_last_and_its_room_is_kept_from_the_budget()
    {
        var answer = new Answer(Answer.MinBudget);
        string footer = "  more  whole: ug console --whole";
        answer.Footer(footer);
        for (int i = 0; i < 40; i++)
        {
            answer.Add(new string('w', 60));
        }

        string text = answer.Finish("ug console");
        Assert.EndsWith("--budget 400\n" + footer + "\n", text, StringComparison.Ordinal);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) <= Answer.MinBudget * 4);
    }
}
