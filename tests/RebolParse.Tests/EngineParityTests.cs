using static RebolParse.P;

namespace RebolParse.Tests;

/// <summary>
/// String input runs on compiled nodes and block input on the interpreter. These tests exercise the same
/// rules on both so the two engines cannot drift apart.
/// </summary>
public class EngineParityTests
{
    // One-character strings, so a text literal like "a" matches the same input item in block mode.
    private static List<object> Strs(string s) => [.. s.Select(c => (object)c.ToString())];

    private static readonly Dictionary<string, Func<Ctx, object?>> Fns = new()
    {
        ["yes"] = _ => true,
        ["no"] = _ => false,
        ["pos"] = c => c.Pos,
    };

    // Each row: input, rule text, expected. Literals are single characters so the same rule works on text and on a block of one-char strings.
    public static TheoryData<string, string, bool> Both => new()
    {
        { "abc", "\"a\" \"b\" \"c\" end", true },
        { "abc", "\"a\" end", false },
        { "ab", "skip skip end", true },
        { "", "end", true },
        { "aabc", "any [\"b\" break | \"a\"] \"c\"", true },
        { "aab", "any [\"b\" break | \"a\"] \"b\"", false },
        { "aab", "some [\"a\" | \"b\" reject]", false },
        { "ab", "[some [\"b\" reject | \"a\"]] | \"a\" \"b\"", true },
        { "a", "reject | \"a\"", true },
        { "aaa", "some [reject \"z\" | \"a\"]", true },
        { "ab", "ahead \"a\" \"a\" \"b\"", true },
        { "ab", "ahead \"b\" skip skip", false },
        { "ab", "not \"b\" skip skip", true },
        { "ab", "not \"a\" skip skip", false },
        { "abc", "to \"c\" \"c\"", true },
        { "abc", "thru \"b\" \"c\"", true },
        { "abc", "to \"z\"", false },
        { "abc", "to [\"b\" \"c\"] \"b\" \"c\"", true },
        { "a", "fail | \"a\"", true },
        { "abc", "thru [\"a\" \"b\"] \"c\"", true },
        { "abc", "to end", true },
        { "abc", "thru end", true },
        { "abc", "m: skip :m \"a\" \"b\" \"c\"", true },
        { "abc", "3 skip", true },
        { "abcd", "2 3 skip", false },
        { "", "0 \"a\"", true },
        { "aaa", "any [copy x \"z\"] some \"a\"", true },
        { "x", "[copy a \"q\" | set b \"q\" | \"x\"]", true },
        { "ab", "collect out [[keep \"a\" \"z\"] | keep \"a\" keep \"b\"]", true },
        { "a", "collect out [keep \"a\" \"z\"] | \"a\"", true },
        { "ab", "collect o [keep pick [\"a\" \"b\"]]", true },
        { "", "collect o [keep pick none]", true },
        { "a", "if (@yes) \"a\"", true },
        { "a", "if (@no) \"a\"", false },
        { "a", "collect o [keep (@pos) \"a\"]", true },
        { "ab", "collect o [[keep (@pos) \"z\"] | \"a\" \"b\"]", true },
    };

    [Theory, MemberData(nameof(Both))]
    public void Text_and_block_engines_agree(string input, string text, bool expected)
    {
        var rule = Dialect.Read(text, values: Fns);
        Assert.Equal(expected, Parser.Parse(input, rule).Matched);
        Assert.Equal(expected, Parser.Parse(Strs(input), rule).Matched);
        Assert.Equal(expected, Parser.IsMatch(input, rule));
        Assert.Equal(expected, Parser.IsMatch(Strs(input), rule));
    }

    [Fact]
    public void Captures_agree()
    {
        var rule = Dialect.Read("copy k to \"=\" \"=\" set first skip collect rest any [keep skip]");
        var text = Parser.Parse("ab=cde", rule);
        var block = Parser.Parse(Strs("ab=cde"), rule);
        Assert.Equal("ab", text.Vars["k"]);
        Assert.Equal(new object[] { "a", "b" }, Assert.IsAssignableFrom<IReadOnlyList<object>>(block.Vars["k"]));
        Assert.Equal('c', text.Vars["first"]);
        Assert.Equal("c", block.Vars["first"]);
        Assert.Equal(new object[] { "d", "e" }, Assert.IsAssignableFrom<IReadOnlyList<object>>(text.Vars["rest"]));
        Assert.Equal(new object[] { "d", "e" }, Assert.IsAssignableFrom<IReadOnlyList<object>>(block.Vars["rest"]));
    }

    [Fact]
    public void Actions_see_the_same_positions_on_both_engines()
    {
        var seen = new List<int>();
        var acts = new Dictionary<string, Action<Ctx>> { ["note"] = c => seen.Add(c.Pos) };
        var rule = Dialect.Read("(@note) \"a\" (@note) \"b\" (@note)", acts);
        Assert.True(Parser.Parse("ab", rule).Matched);
        Assert.True(Parser.Parse(Strs("ab"), rule).Matched);
        Assert.Equal([0, 1, 2, 0, 1, 2], seen);
    }

    [Fact]
    public void Named_rules_and_recursion_agree()
    {
        var rules = Dialect.ReadRules(new Dictionary<string, string> { ["group"] = "\"(\" any group \")\"" });
        foreach (var (input, expected) in new[] { ("(()())", true), ("(()", false), ("", false) })
        {
            Assert.Equal(expected, Parser.Parse(input, Ref("group"), rules).Matched);
            Assert.Equal(expected, Parser.Parse(Strs(input), Ref("group"), rules).Matched);
        }
        Assert.Throws<KeyNotFoundException>(() => Parser.Parse(Strs("a"), Ref("nope")));
        Assert.Throws<KeyNotFoundException>(() => Parser.Parse(Strs("a"), Seek("nope")));
    }

    [Fact]
    public void Edits_that_fail_leave_blocks_alone()
    {
        var r = Parser.Parse(new List<object> { 1 }, Dialect.Read("change \"x\" 2 | integer!"));
        Assert.True(r.Matched);
        Assert.Equal(new object[] { 1 }, Assert.IsAssignableFrom<IReadOnlyList<object>>(r.Output));
    }

    [Fact]
    public void Block_IsMatch_reuses_state_after_collect_and_partial()
    {
        var rule = Dialect.Read("collect o [keep integer!]");
        Assert.True(Parser.IsMatch(new List<object> { 1 }, rule));
        Assert.True(Parser.IsMatch(new List<object> { 1 }, rule));
        Assert.True(Parser.IsMatch(new List<object> { 1, 2 }, Integer, partial: true));
        Assert.True(Parser.Parse(new List<object> { 1, 2 }, Integer, partial: true).Matched);
    }

    // ---- text-only shapes ----

    [Fact]
    public void Text_never_matches_numbers_words_or_blocks()
    {
        Assert.False(Parser.Parse("7", Lit(7)).Matched);
        Assert.False(Parser.Parse("w", Lit(new Word("w"))).Matched);
        Assert.False(Parser.Parse("[1]", Into(Any(Skip))).Matched);
        Assert.True(Parser.Parse("x", Lit(7) | Into(Skip) | "x").Matched);
    }

    [Fact]
    public void Text_item_predicates_without_a_char_test_are_boxed()
    {
        Assert.True(Parser.Parse("a", Is(o => o is 'a')).Matched);
        Assert.False(Parser.Parse("b", Is(o => o is 'a')).Matched);
        Assert.False(Parser.Parse("", Is(_ => true)).Matched);
    }

    [Theory]
    [InlineData("a1b", "to digit digit \"b\"", true)]
    [InlineData("a1b", "thru digit \"b\"", true)]
    [InlineData("ab", "to digit", false)]
    [InlineData("ab", "thru digit", false)]
    [InlineData("abXc", "to #\"x\" skip \"c\"", true)]
    [InlineData("abXc", "thru #\"x\" \"c\"", true)]
    [InlineData("abc", "to #\"x\"", false)]
    [InlineData("abXc", "to \"x\" skip \"c\"", true)]
    [InlineData("abc", "thru \"x\"", false)]
    [InlineData("a1b2", "to [digit \"b\"] skip \"b2\"", true)]
    [InlineData("a1b2", "thru [digit \"b\"] \"2\"", true)]
    [InlineData("a1b2", "to [digit \"z\"]", false)]
    [InlineData("abc", "thru \"\" \"abc\"", true)]
    public void Text_scans_in_every_specialization(string input, string rule, bool expected)
    {
        Assert.Equal(expected, Parser.IsMatch(input, Dialect.Read(rule), ignoreCase: true));
        Assert.Equal(expected, Parser.Parse(input, Dialect.Read(rule), ignoreCase: true).Matched);
    }

    [Fact]
    public void Text_scans_are_case_sensitive_by_default()
    {
        Assert.False(Parser.IsMatch("abXc", Dialect.Read("to #\"x\" skip \"c\"")));
        Assert.True(Parser.IsMatch("abxc", Dialect.Read("to #\"x\" skip \"c\"")));
        Assert.False(Parser.IsMatch("abXc", Dialect.Read("thru \"x\" \"c\"")));
    }

    [Fact]
    public void Text_repeat_bounds_on_char_tests()
    {
        Assert.True(Parser.IsMatch("123", Rep(2, 3, Digit)));
        Assert.False(Parser.IsMatch("1", Rep(2, 3, Digit)));
        Assert.True(Parser.IsMatch("1234", Rep(2, 3, Digit) + Digit));
        Assert.True(Parser.IsMatch("12", Rep(2, int.MaxValue, Digit)));
        Assert.True(Parser.IsMatch("", Rep(0, 5, Digit)));
    }

    [Fact]
    public void Text_loop_with_an_impure_body_that_edits_keeps_going()
    {
        Assert.Equal("", Parser.Parse("aaa", Dialect.Read("any [remove \"a\"]")).Output);
        Assert.Equal("abc", Parser.Parse("abc", Dialect.Read("any [remove none]"), partial: true).Output);
    }

    [Fact]
    public void Rule_compilation_is_cached_per_instance_and_switches_between_rules()
    {
        var a = Dialect.Read("\"a\"");
        var b = Dialect.Read("\"b\"");
        for (var i = 0; i < 3; i++)
        {
            Assert.True(Parser.IsMatch("a", a));
            Assert.True(Parser.IsMatch("b", b));
            Assert.False(Parser.IsMatch("b", a));
        }
    }

    [Fact]
    public void A_named_rule_is_re_resolved_when_the_rule_table_changes()
    {
        var r = Ref("x");
        Assert.True(Parser.IsMatch("a", r, new Dictionary<string, Rule> { ["x"] = "a" }));
        Assert.True(Parser.IsMatch("b", r, new Dictionary<string, Rule> { ["x"] = "b" }));
        Assert.False(Parser.IsMatch("a", r, new Dictionary<string, Rule> { ["x"] = "b" }));
    }

    [Fact]
    public void Unknown_rule_node_throws_on_both_engines()
    {
        Assert.Throws<NotSupportedException>(() => Parser.Parse("a", new Mystery()));
        Assert.Throws<NotSupportedException>(() => Parser.Parse(Strs("a"), new Mystery()));
    }

    private sealed record Mystery : Rule;
}
