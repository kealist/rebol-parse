using static RebolParse.P;

namespace RebolParse.Tests;

/// <summary>A choice skips alternatives that cannot start with the next character. These check that it never changes the answer.</summary>
public class DispatchTests
{
    private static bool Ok(string rule, string input, bool ignoreCase = false, IReadOnlyDictionary<string, Rule>? rules = null) =>
        Parser.Parse(input, Dialect.Read(rule), rules, ignoreCase: ignoreCase).Matched;

    [Theory]
    [InlineData("\"日本\" | \"é\" | \"x\"", "日本", true)]
    [InlineData("\"日本\" | \"é\" | \"x\"", "é", true)]
    [InlineData("\"日本\" | \"é\" | \"x\"", "x", true)]
    [InlineData("\"日本\" | \"é\" | \"x\"", "y", false)]
    [InlineData("digit | alpha", "é", true)]
    [InlineData("digit | alpha", "日", true)]
    [InlineData("digit | alpha", "-", false)]
    [InlineData("some digit | some alpha", "日本", true)]
    [InlineData("\"a\" | \"b\" | \"c\"", "", false)]
    [InlineData("\"\" | \"a\"", "", true)]
    [InlineData("\"\" | \"a\"", "a", false)]
    [InlineData("opt \"x\" \"y\" | \"z\"", "y", true)]
    [InlineData("any \"x\" \"y\" | \"z\"", "z", true)]
    [InlineData("[some [not \",\" skip] | \",\"] end", "ab", true)]
    [InlineData("[some [not \",\" skip] | \",\"] end", ",", true)]
    [InlineData("[1 3 [not \",\" skip] | \",\"] end", "ab", true)]
    [InlineData("[1 3 [not \",\" skip] | \",\"] end", "abcd", false)]
    [InlineData("[any [not \",\" skip] \"x\" | \"y\"] end", "y", true)]
    [InlineData("[some [not [digit | space] skip] | digit] end", "ab", true)]
    [InlineData("[some [not [digit | space] skip] | digit] end", "7", true)]
    public void Alternatives_still_match_what_they_should(string rule, string input, bool expected) =>
        Assert.Equal(expected, Ok(rule, input));

    [Fact]
    public void Ignore_case_reaches_alternatives_by_their_other_case()
    {
        Assert.True(Ok("\"a\" | \"b\"", "B", ignoreCase: true));
        Assert.False(Ok("\"a\" | \"b\"", "B"));
        Assert.True(Ok("\"select\" | \"insert\"", "INSERT", ignoreCase: true));
        Assert.True(Ok("#\"a\" | #\"b\"", "B", ignoreCase: true));
        Assert.False(Ok("#\"a\" | #\"b\"", "B"));
        Assert.Equal(2, Parser.Parse("abXcd", Dialect.Read("some [not \"x\" skip]"), partial: true, ignoreCase: true).Position);
        Assert.Equal(5, Parser.Parse("abXcd", Dialect.Read("some [not \"x\" skip]"), partial: true).Position);
        // the same rule instance, used in both modes, keeps both answers
        var rule = Dialect.Read("\"a\" | \"b\"");
        Assert.True(Parser.Parse("B", rule, ignoreCase: true).Matched);
        Assert.False(Parser.Parse("B", rule).Matched);
        Assert.True(Parser.Parse("B", rule, ignoreCase: true).Matched);
    }

    [Fact]
    public void A_choice_reads_the_character_again_after_a_failed_alternative_edits_it()
    {
        // the first alternative removes "a" and then fails; the second must see the "b" that is now there
        var rule = Dialect.Read("[remove skip end | \"b\" | not \"a\"]");
        var r = Parser.Parse("ab", rule, partial: true);
        Assert.True(r.Matched);
        Assert.Equal(1, r.Position);
        Assert.Equal("b", r.Output);
    }

    [Fact]
    public void An_undefined_rule_in_a_later_alternative_is_not_an_error_until_it_runs()
    {
        var rule = Dialect.Read("\"a\" | missing");
        Assert.True(Parser.Parse("a", rule, new Dictionary<string, Rule>()).Matched);
        Assert.Throws<KeyNotFoundException>(() => Parser.Parse("b", rule, new Dictionary<string, Rule>()));
    }

    [Fact]
    public void Tables_follow_the_rule_table_in_use()
    {
        var rule = Choice(Ref("x"), "z");
        var first = new Dictionary<string, Rule> { ["x"] = "a" };
        var second = new Dictionary<string, Rule> { ["x"] = "b" };
        Assert.True(Parser.Parse("a", rule, first).Matched);
        Assert.False(Parser.Parse("b", rule, first).Matched);
        Assert.True(Parser.Parse("b", rule, second).Matched);
        Assert.False(Parser.Parse("a", rule, second).Matched);
    }

    [Fact]
    public void A_rule_that_can_recurse_before_consuming_still_works_when_the_first_alternative_matches()
    {
        var rules = Dialect.ReadGrammar("a = \"y\" | a \"z\"");
        Assert.True(Parser.Parse("y", Ref("a"), rules).Matched);
    }

    [Fact]
    public void Stop_sets_accept_single_item_sequences_and_ignore_unknown_tests()
    {
        var one = Some(Seq(Not(Seq(",")), Skip));
        Assert.Equal(2, Parser.Parse("ab,c", one, partial: true).Position);
        var custom = Some(Seq(Not(Is(o => o is char c && c == ',')), Skip));
        Assert.Equal(2, Parser.Parse("ab,c", custom, partial: true).Position);
    }
}
