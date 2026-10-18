using static RebolParse.P;

namespace RebolParse.Tests;

/// <summary>Behaviours the REBOL parse dialect defines, checked through the text reader and the compiled engine.</summary>
public class ConformanceTests
{
    private static bool Ok(string rule, string input, bool partial = false, bool ignoreCase = false) =>
        Parser.Parse(input, Dialect.Read(rule), partial: partial, ignoreCase: ignoreCase).Matched;

    private static List<object> L(params object[] items) => [.. items];

    [Theory]
    [InlineData("to end", "abc", true)]
    [InlineData("thru end", "abc", true)]
    [InlineData("to end", "", true)]
    [InlineData("2 skip", "ab", true)]
    [InlineData("2 skip", "a", false)]
    [InlineData("skip", "", false)]
    [InlineData("end", "", true)]
    [InlineData("some none", "", true)]
    [InlineData("any none end", "", true)]
    [InlineData("opt opt \"a\"", "a", true)]
    [InlineData("2 3 digit", "12", true)]
    [InlineData("2 3 digit", "1", false)]
    [InlineData("2 3 digit", "1234", false)]
    [InlineData("0 digit", "", true)]
    [InlineData("\"a\" |", "", true)]
    [InlineData("| \"a\"", "a", false)] // the empty first alternative wins, leaving "a" unconsumed
    [InlineData("to [\"b\" | \"c\"] to end", "aac", true)]
    [InlineData("thru [\"b\" | \"c\"] end", "aab", true)]
    [InlineData("thru \"z\"", "abc", false)]
    [InlineData("to \"z\"", "abc", false)]
    [InlineData("not not \"a\" \"a\"", "a", true)]
    [InlineData("ahead \"a\" \"a\"", "a", true)]
    [InlineData("and \"a\" \"a\"", "a", true)]
    [InlineData("and \"a\" \"b\"", "a", false)]
    [InlineData("not end skip", "a", true)]
    [InlineData("[\"a\" | \"ab\"] \"c\"", "abc", false)]
    [InlineData("[\"ab\" | \"a\"] \"c\"", "ac", true)]
    [InlineData("while \"a\" end", "aaa", true)]
    [InlineData("while [\"a\" | \"b\"] end", "abba", true)]
    [InlineData("while none end", "", true)]
    [InlineData("fail | \"a\"", "a", true)]
    [InlineData("\"a\" fail | \"a\"", "a", true)]
    public void Text_rules(string rule, string input, bool expected) =>
        Assert.Equal(expected, Ok(rule, input));

    [Fact]
    public void Failed_scans_leave_the_position_alone()
    {
        var r = Parser.Parse("abc", Dialect.Read("thru \"z\" | skip"), partial: true);
        Assert.True(r.Matched);
        Assert.Equal(1, r.Position);
    }

    [Fact]
    public void Copy_of_an_empty_match_is_an_empty_string()
    {
        var r = Parser.Parse("", Dialect.Read("copy v any digit"));
        Assert.True(r.Matched);
        Assert.Equal("", r.Vars["v"]);
    }

    [Fact]
    public void Set_at_the_end_of_input_is_null_without_failing()
    {
        var r = Parser.Parse("", Dialect.Read("set v opt skip"));
        Assert.True(r.Matched);
        Assert.Null(r.Vars["v"]);
    }

    [Fact]
    public void Block_input_matches_by_type_and_value()
    {
        var input = L("x", 3, new Word("go"), 4.5, L(1));
        var rule = Dialect.Read("string! integer! quote go decimal! block!");
        Assert.True(Parser.Parse(input, rule).Matched);
        Assert.False(Parser.Parse(L(3), Dialect.Read("string!")).Matched);
    }

    [Fact]
    public void Into_requires_the_inner_rule_to_consume_the_whole_inner_list()
    {
        Assert.True(Parser.Parse(L(L(1, 2)), Dialect.Read("into [2 integer!]")).Matched);
        Assert.False(Parser.Parse(L(L(1, 2)), Dialect.Read("into [integer!]")).Matched);
        Assert.False(Parser.Parse(L(1), Dialect.Read("into [integer!]")).Matched);
    }

    [Fact]
    public void Marks_can_rewind_to_reparse_the_same_text()
    {
        Assert.True(Ok("m: some alpha :m copy w some alpha", "abc"));
    }

    [Fact]
    public void Break_inside_any_ends_the_loop_successfully()
    {
        var r = Parser.Parse("aab", Dialect.Read("any [\"a\" | break] \"b\""));
        Assert.True(r.Matched);
    }

    [Fact]
    public void Ignore_case_covers_literals_but_not_classes()
    {
        Assert.True(Ok("\"abc\"", "ABC", ignoreCase: true));
        Assert.False(Ok("lower", "A", ignoreCase: true));
    }

    [Fact]
    public void Keywords_are_case_insensitive_in_rule_text()
    {
        Assert.True(Ok("SOME Digit END", "123"));
    }

    [Fact]
    public void Collect_nests_and_keeps_in_order()
    {
        var r = Parser.Parse("a1b2", Dialect.Read("collect out some [keep alpha digit]"));
        Assert.True(r.Matched);
        Assert.Equal(["a", "b"], ((IEnumerable<object>)r.Vars["out"]!).Select(x => x.ToString()));
    }
}
