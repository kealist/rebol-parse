using static RebolParse.P;

namespace RebolParse.Tests;

/// <summary>The examples in README.md and the claims in docs/Reference.md, run as written.</summary>
public class DocsExamplesTests
{
    [Fact]
    public void Readme_opening_example()
    {
        var rule = Dialect.Read("copy key some alpha \"=\" copy value some digit");
        var result = Parser.Parse("width=640", rule);
        Assert.True(result.Matched);
        Assert.Equal("width", result.Vars["key"]);
        Assert.Equal("640", result.Vars["value"]);
    }

    [Fact]
    public void Readme_tour()
    {
        var phone = Dialect.Read("3 digit \"-\" 4 digit");
        Assert.True(Parser.IsMatch("555-1234", phone));
        Assert.False(Parser.IsMatch("5551234", phone));

        var words = Dialect.Read("collect words any [keep some alpha | skip]");
        Assert.Equal(new object[] { "hello", "big", "world" },
            Assert.IsAssignableFrom<IReadOnlyList<object>>(Parser.Parse("hello, big world!", words).Vars["words"]));

        var dashes = Dialect.Read("any [change \"-\" \"+\" | skip]");
        Assert.Equal("a+b+c", Parser.Parse("a-b-c", dashes).Output);

        var rules = Dialect.ReadRules(new Dictionary<string, string> { ["group"] = "\"(\" any group \")\"" });
        Assert.True(Parser.IsMatch("(()(()))", Ref("group"), rules));
        Assert.False(Parser.IsMatch("(()", Ref("group"), rules));

        var total = 0;
        var sum = Dialect.Read("any [copy n some digit (@add) opt \",\"]",
            new Dictionary<string, Action<Ctx>> { ["add"] = c => total += int.Parse((string)c.Vars["n"]!) });
        Assert.True(Parser.Parse("1,2,30", sum).Matched);
        Assert.Equal(33, total);

        var command = Dialect.Read("[quote add integer! integer!] | [quote say string!]");
        Assert.True(Parser.IsMatch(new List<object> { new Word("add"), 1, 2 }, command));
    }

    [Fact]
    public void Readme_fluent_equivalent_of_the_text_rule()
    {
        var fluent = Some(Digit) + "." + Some(Digit);
        var text = Dialect.Read("some digit \".\" some digit");
        foreach (var s in new[] { "3.14", "3.", ".5", "" })
            Assert.Equal(Parser.IsMatch(s, text), Parser.IsMatch(s, fluent));
    }

    [Fact]
    public void Reference_loops_stop_when_nothing_is_consumed()
    {
        Assert.True(Parser.IsMatch("", Dialect.Read("any none")));
        Assert.True(Parser.IsMatch("", Dialect.Read("some none")));
    }

    [Fact]
    public void Reference_failure_restores_position_and_partial_reports_it()
    {
        var r = Parser.Parse("abcd", Dialect.Read("\"ab\" \"x\""), partial: true);
        Assert.False(r.Matched);
        Assert.Equal(0, r.Position);
        var prefix = Parser.Parse("abcd", (Rule)"ab", partial: true);
        Assert.True(prefix.Matched);
        Assert.Equal(2, prefix.Position);
    }

    [Fact]
    public void Reference_a_result_converts_to_bool()
    {
        if (!Parser.Parse("a", (Rule)"a")) Assert.Fail("should have matched");
        Assert.True(Parser.Parse("a", (Rule)"a"));
    }

    [Fact]
    public void Reference_set_captures_a_char_in_text_and_the_item_in_blocks()
    {
        Assert.Equal('x', Parser.Parse("x", Dialect.Read("set v skip")).Vars["v"]);
        Assert.Equal(7, Parser.Parse(new List<object> { 7 }, Dialect.Read("set v skip")).Vars["v"]);
    }

    [Fact]
    public void Reference_unedited_output_is_the_original_instance()
    {
        var text = "abc";
        var list = new List<object> { 1 };
        Assert.Same(text, Parser.Parse(text, Dialect.Read("to end")).Output);
        Assert.Same(list, Parser.Parse(list, Dialect.Read("skip")).Output);
    }

    [Fact]
    public void Reference_a_char_escape_matches_a_double_quote()
    {
        Assert.True(Parser.IsMatch("\"", Dialect.Read("#\"^\"\"")));
        Assert.True(Parser.IsMatch("q", Dialect.Read("\"^q\"")));
    }

    [Fact]
    public void Reference_a_number_in_a_rule_is_a_repeat_count_and_quote_matches_the_number()
    {
        Assert.True(Parser.IsMatch("aaa", Dialect.Read("3 \"a\"")));
        Assert.True(Parser.IsMatch(new List<object> { 42 }, Dialect.Read("quote 42")));
    }

    [Fact]
    public void Reference_if_accepts_any_non_null_non_false_value()
    {
        var values = new Dictionary<string, Func<Ctx, object?>>
        {
            ["zero"] = _ => 0, ["falsy"] = _ => false, ["nil"] = _ => null,
        };
        Assert.True(Parser.IsMatch("", Dialect.Read("if (@zero)", values: values)));
        Assert.False(Parser.IsMatch("", Dialect.Read("if (@falsy)", values: values)));
        Assert.False(Parser.IsMatch("", Dialect.Read("if (@nil)", values: values)));
    }

    [Fact]
    public void Reference_rule_text_that_runs_code_is_rejected()
    {
        Assert.Throws<FormatException>(() => Dialect.Read("(print 1)"));
        Assert.Throws<FormatException>(() => Dialect.Read("#\"ab\""));
    }
}
