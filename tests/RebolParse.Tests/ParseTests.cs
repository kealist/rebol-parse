using static RebolParse.P;

namespace RebolParse.Tests;

public class ParseTests
{
    // Each case: input, fluent rule, equivalent dialect text, expected whole-input match.
    public static TheoryData<string, Rule, string, bool> Cases => new()
    {
        { "aaab", Some("a") + "b", "some \"a\" \"b\"", true },
        { "aaa", Some("a") + "b", "some \"a\" \"b\"", false },
        { "abab", Some("a" + (Rule)"b"), "some [\"a\" \"b\"]", true },
        { "", Any("a"), "any \"a\"", true },
        { "b", Opt("a") + "b", "opt \"a\" \"b\"", true },
        { "123", Some(Digit), "some digit", true },
        { "12x", Some(Digit), "some digit", false },
        { "ab", (Rule)"a" | "b", "\"a\" | \"b\"", false },
        { "b", (Rule)"a" | "b", "\"a\" | \"b\"", true },
        // alternation backtracks to the next alternative
        { "ac", ("a" + (Rule)"b") | ("a" + (Rule)"c"), "[\"a\" \"b\"] | [\"a\" \"c\"]", true },
        { "aaa", Rep(3, "a"), "3 \"a\"", true },
        { "aaaa", Rep(3, "a"), "3 \"a\"", false },
        { "aaaa", Rep(2, 4, "a"), "2 4 \"a\"", true },
        { "a", Rep(2, 4, "a"), "2 4 \"a\"", false },
        { "abc", Skip + Skip + Skip + End, "skip skip skip end", true },
        { "abc", Skip + Skip + End, "skip skip end", false },
        { "ab", Not("x") + Skip + Skip, "not \"x\" skip skip", true },
        { "ab", Ahead("a") + "ab", "ahead \"a\" \"ab\"", true },
        { "key=value", To("=") + "=" + To(End), "to \"=\" \"=\" to end", true },
        { "key=value", Thru("=") + To(End), "thru \"=\" to end", true },
        { "key", To("=") + To(End), "to \"=\" to end", false },
        { "abc", Fail | Some(Alpha), "fail | some alpha", true },
        // a zero-width body must not loop forever
        { "", Some(None), "some none", true },
    };

    [Theory, MemberData(nameof(Cases))]
    public void Fluent_and_text_agree(string input, Rule fluent, string text, bool expected)
    {
        Assert.Equal(expected, Parser.Parse(input, fluent).Matched);
        Assert.Equal(expected, Parser.Parse(input, Dialect.Read(text)).Matched);
    }

    [Fact]
    public void Copy_captures_a_slice()
    {
        var result = Parser.Parse("name=joshua", Copy("k", To("=")) + "=" + Copy("v", To(End)));
        Assert.True(result.Matched);
        Assert.Equal("name", result.Vars["k"]);
        Assert.Equal("joshua", result.Vars["v"]);

        var viaText = Parser.Parse("name=joshua", Dialect.Read("copy k to \"=\" \"=\" copy v to end"));
        Assert.Equal("joshua", viaText.Vars["v"]);
    }

    [Fact]
    public void Set_captures_first_item()
    {
        var result = Parser.Parse("xyz", Dialect.Read("set c skip to end"));
        Assert.True(result.Matched);
        Assert.Equal('x', result.Vars["c"]);
    }

    [Fact]
    public void Marks_and_seek_rewind()
    {
        // match "ab" twice by seeking back to the mark
        var rule = Dialect.Read("start: \"ab\" :start \"ab\"");
        Assert.True(Parser.Parse("ab", rule, partial: true).Matched);
    }

    [Fact]
    public void Actions_run_on_the_matched_path()
    {
        var seen = new List<int>();
        var acts = new Dictionary<string, Action<Ctx>> { ["note"] = c => seen.Add(c.Pos) };
        var rule = Dialect.Read("\"ab\" (@note) \"c\" (@note)", acts);
        Assert.True(Parser.Parse("abc", rule).Matched);
        Assert.Equal([2, 3], seen);
    }

    [Fact]
    public void Break_leaves_a_loop()
    {
        // Matches 'a' until a 'b' is seen, then breaks; trailing text remains for the next rule.
        var rule = Dialect.Read("any [\"b\" break | \"a\"] \"c\"");
        Assert.True(Parser.Parse("aabc", rule).Matched);
    }

    [Fact]
    public void Recursive_named_rules_match_nested_parens()
    {
        var rules = Dialect.ReadRules(new Dictionary<string, string>
        {
            ["group"] = "\"(\" any [group | alpha] \")\"",
        });
        Assert.True(Parser.Parse("(a(bc)(d(e)))", Ref("group"), rules).Matched);
        Assert.False(Parser.Parse("(a(bc)", Ref("group"), rules).Matched);
    }

    [Fact]
    public void Block_mode_matches_values_and_datatypes()
    {
        var input = new List<object> { new Word("add"), 3, 4 };
        var rule = Dialect.Read("quote add integer! integer!");
        Assert.True(Parser.Parse(input, rule).Matched);
        Assert.False(Parser.Parse(new List<object> { new Word("add"), 3, "x" }, rule).Matched);
    }

    [Fact]
    public void Into_descends_into_nested_blocks()
    {
        var input = new List<object> { "list", new List<object> { 1, 2, 3 }, "end" };
        var rule = Dialect.Read("\"list\" into [some integer!] \"end\"");
        Assert.True(Parser.Parse(input, rule).Matched);

        var bad = new List<object> { "list", new List<object> { 1, "x" }, "end" };
        Assert.False(Parser.Parse(bad, rule).Matched);
    }

    [Fact]
    public void Partial_allows_unconsumed_input()
    {
        Assert.False(Parser.Parse("abc", (Rule)"a").Matched);
        Assert.True(Parser.Parse("abc", (Rule)"a", partial: true).Matched);
    }

    [Fact]
    public void Failed_match_restores_position_for_the_next_alternative()
    {
        var r = Parser.Parse("abd", Dialect.Read("\"abc\" | \"ab\" \"d\""));
        Assert.True(r.Matched);
    }

    [Theory]
    [InlineData("some [")]
    [InlineData("]")]
    [InlineData("copy")]
    [InlineData("(print 1)")]
    [InlineData("\"unterminated")]
    public void Malformed_rules_throw(string text) =>
        Assert.Throws<FormatException>(() => Dialect.Read(text));

    [Fact]
    public void Undefined_rule_reference_throws()
    {
        Assert.Throws<KeyNotFoundException>(() => Parser.Parse("a", Dialect.Read("nope")));
    }
}
