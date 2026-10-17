using static RebolParse.P;

namespace RebolParse.Tests;

/// <summary>
/// Side effects made inside a branch that later fails: keeps are dropped, breaks and rejects are forgotten, and
/// the position comes back. The compiled engine skips that bookkeeping for nodes it knows are pure, so every
/// wrapper that can hold an impure body is checked here, on both engines.
/// </summary>
public class BacktrackingTests
{
    private static List<object> Strs(string s) => [.. s.Select(c => (object)c.ToString())];

    private static object[] Kept(ParseResult r) => [.. Assert.IsAssignableFrom<IReadOnlyList<object>>(r.Vars["out"])];

    private static readonly Dictionary<string, Rule> KeepA = new() { ["r"] = Dialect.Read("keep \"a\"") };
    private static readonly Dictionary<string, Rule> BreakRule = new() { ["r"] = Dialect.Read("break") };

    // A keep made under each wrapper, in a branch that then fails on "z": only the second branch's keep may survive.
    [Theory]
    [InlineData("copy x [keep \"a\"]")]
    [InlineData("set x [keep \"a\"]")]
    [InlineData("ahead [keep \"a\"]")]
    [InlineData("to [keep \"a\"]")]
    [InlineData("any [keep \"a\"]")]
    [InlineData("1 [keep \"a\"]")]
    [InlineData("collect inner [keep \"q\"] keep \"a\"")]
    [InlineData("r")]
    public void A_keep_inside_a_failed_branch_is_dropped(string wrapped)
    {
        var rule = Dialect.Read($"collect out [[{wrapped} \"z\"] | \"a\" keep \"b\"]");
        Assert.Equal(new object[] { "b" }, Kept(Parser.Parse("ab", rule, KeepA)));
        Assert.Equal(new object[] { "b" }, Kept(Parser.Parse(Strs("ab"), rule, KeepA)));
    }

    [Fact]
    public void A_keep_inside_a_not_that_fails_is_dropped()
    {
        // 'not' fails because its body (which keeps) succeeds; that keep must not survive
        var rule = Dialect.Read("collect out [[not [keep \"a\"] \"z\"] | \"a\" keep \"b\"]");
        Assert.Equal(new object[] { "b" }, Kept(Parser.Parse("ab", rule)));
        Assert.Equal(new object[] { "b" }, Kept(Parser.Parse(Strs("ab"), rule)));
    }

    // A break set under each wrapper in a branch that fails must not stop the loop; the loop is made
    // break-aware by the trailing reject alternative, which never runs because "q" never matches.
    [Theory]
    [InlineData("copy x [break]")]
    [InlineData("set x [break]")]
    [InlineData("collect c [break]")]
    [InlineData("ahead [break]")]
    [InlineData("to [break]")]
    [InlineData("not [not [break]]")]
    [InlineData("r")]
    public void A_break_inside_a_failed_branch_is_forgotten(string wrapped)
    {
        var rule = Dialect.Read($"some [[{wrapped} \"z\"] | \"a\" | \"q\" reject]");
        Assert.True(Parser.Parse("aaa", rule, BreakRule).Matched);
        Assert.True(Parser.Parse(Strs("aaa"), rule, BreakRule).Matched);
    }

    [Fact]
    public void Reject_only_counts_while_a_loop_is_running()
    {
        var after = Dialect.Read("some [\"a\" break] reject");
        Assert.False(Parser.Parse("a", after).Matched);
        Assert.False(Parser.Parse(Strs("a"), after).Matched);

        var ok = Dialect.Read("some [\"a\" | \"b\" reject]");
        Assert.True(Parser.Parse("aaa", ok).Matched);
        Assert.True(Parser.Parse(Strs("aaa"), ok).Matched);

        var broke = Dialect.Read("any [\"a\" break] \"b\"");
        Assert.True(Parser.Parse("ab", broke).Matched);
        Assert.True(Parser.Parse(Strs("ab"), broke).Matched);

        // without the break the loop would eat both a's and the trailing "a" would have nothing left
        var stops = Dialect.Read("any [\"a\" break] \"a\"");
        Assert.True(Parser.Parse("aa", stops).Matched);
        Assert.True(Parser.Parse(Strs("aa"), stops).Matched);
    }

    [Fact]
    public void Skip_fails_at_the_end_of_a_block()
    {
        Assert.False(Parser.IsMatch(new List<object>(), Skip, partial: true));
        Assert.False(Parser.Parse(Strs("a"), Skip + Skip, partial: true).Matched);
    }

    [Fact]
    public void A_nested_IsMatch_inside_an_action_does_not_disturb_the_outer_parse()
    {
        var acts = new Dictionary<string, Action<Ctx>>
        {
            ["nested"] = _ => Assert.True(Parser.IsMatch("z", Dialect.Read("collect x [keep \"z\"] m: :m"))),
        };
        var rule = Dialect.Read("collect out [keep \"a\" (@nested) keep \"b\"]", acts);
        Assert.True(Parser.IsMatch("ab", rule));
        Assert.True(Parser.IsMatch(Strs("ab"), rule));
        Assert.Equal(new object[] { "a", "b" }, Kept(Parser.Parse("ab", rule)));
    }

    [Fact]
    public void Loops_with_impure_bodies_respect_their_bounds()
    {
        var two3 = Dialect.Read("collect out 2 3 [keep \"a\"]");
        Assert.Equal(3, Parser.Parse("aaaa", two3, partial: true).Position);
        Assert.Equal(3, Parser.Parse(Strs("aaaa"), two3, partial: true).Position);
        Assert.False(Parser.Parse("a", two3, partial: true).Matched);
        Assert.False(Parser.Parse(Strs("a"), two3, partial: true).Matched);

        var zero = Dialect.Read("collect out 0 0 [keep \"a\"]");
        Assert.Equal(0, Parser.Parse("a", zero, partial: true).Position);
        Assert.Equal(0, Parser.Parse(Strs("a"), zero, partial: true).Position);

        var some = Dialect.Read("collect out some [keep \"a\"]");
        Assert.False(Parser.Parse("", some).Matched);
        Assert.False(Parser.Parse(Strs(""), some).Matched);
    }

    [Fact]
    public void Loops_with_pure_bodies_respect_their_bounds()
    {
        Assert.False(Parser.IsMatch("a", Rep(2, 4, "a"), partial: true));
        Assert.False(Parser.IsMatch(Strs("a"), Rep(2, 4, "a"), partial: true));
        Assert.Equal(0, Parser.Parse("12", Rep(0, 0, Digit), partial: true).Position);
        Assert.Equal(2, Parser.Parse("123", Rep(0, 2, Digit), partial: true).Position);
    }

    [Fact]
    public void Failed_scans_report_failure_even_when_partial()
    {
        Assert.False(Parser.IsMatch("a1b2", Dialect.Read("to [digit \"z\"]"), partial: true));
        Assert.False(Parser.IsMatch("ab", Dialect.Read("to digit"), partial: true));
        Assert.False(Parser.IsMatch("ab", Dialect.Read("thru #\"x\""), partial: true, ignoreCase: true));
        Assert.False(Parser.IsMatch(Strs("ab"), Dialect.Read("to \"x\""), partial: true));
    }

    [Fact]
    public void Scans_find_matches_at_the_end_and_right_at_the_cursor()
    {
        Assert.True(Parser.IsMatch("ab", Dialect.Read("to [not skip]")));
        Assert.True(Parser.IsMatch("Xa", Dialect.Read("to #\"x\" skip \"a\""), ignoreCase: true));
        Assert.True(Parser.IsMatch("abXc", Dialect.Read("\"a\" thru #\"x\" \"c\""), ignoreCase: true));
        Assert.True(Parser.IsMatch("abxc", Dialect.Read("\"a\" thru #\"x\" \"c\"")));
    }

    [Fact]
    public void Edits_that_change_nothing_keep_the_original_string()
    {
        var input = "abc";
        Assert.Same(input, Parser.Parse(input, Dialect.Read("remove none to end")).Output);
        Assert.Same(input, Parser.Parse(input, Dialect.Read("insert \"\" to end")).Output);
    }

    [Fact]
    public void Removing_from_the_second_half_of_a_long_string()
    {
        var input = new string('a', 100) + "1" + new string('b', 10);
        var r = Parser.Parse(input, Dialect.Read("to digit remove digit to end"));
        Assert.True(r.Matched);
        Assert.Equal(new string('a', 100) + new string('b', 10), r.Output);
    }

    [Fact]
    public void IsMatch_state_does_not_leak_through_actions_or_exceptions()
    {
        var sawVars = new List<int>();
        var acts = new Dictionary<string, Action<Ctx>>
        {
            ["count"] = c => sawVars.Add(c.Vars.Count),
            ["boom"] = _ => throw new InvalidOperationException("boom"),
        };
        Assert.True(Parser.IsMatch("a", Dialect.Read("copy x \"a\"")));
        Assert.True(Parser.IsMatch("a", Dialect.Read("(@count) \"a\"", acts)));
        Assert.True(Parser.IsMatch(Strs("a"), Dialect.Read("copy x \"a\"")));
        Assert.True(Parser.IsMatch(Strs("a"), Dialect.Read("(@count) \"a\"", acts)));
        Assert.Equal([0, 0], sawVars);

        // an exception thrown inside a collect leaves nothing on the stack for the next call
        Assert.Throws<InvalidOperationException>(() => Parser.IsMatch("a", Dialect.Read("collect o [keep \"a\" (@boom)]", acts)));
        Assert.Throws<InvalidOperationException>(() => Parser.IsMatch("a", Dialect.Read("keep \"a\"")));
        Assert.Throws<InvalidOperationException>(() => Parser.IsMatch(Strs("a"), Dialect.Read("collect o [keep \"a\" (@boom)]", acts)));
        Assert.Throws<InvalidOperationException>(() => Parser.IsMatch(Strs("a"), Dialect.Read("keep \"a\"")));
    }
}
