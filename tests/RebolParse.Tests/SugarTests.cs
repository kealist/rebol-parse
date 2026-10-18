using static RebolParse.P;

namespace RebolParse.Tests;

public class SugarTests
{
    // ---- operators and chained forms build the same trees as the plain builders ----

    [Fact]
    public void Operators_build_the_same_trees()
    {
        Assert.Equal(Some(Digit), +Digit);
        Assert.Equal(Not(Digit), !Digit);
        Assert.Equal(Rep(3, Digit), Digit * 3);
        Assert.Equal(Rep(3, Digit), 3 * Digit);
    }

    [Fact]
    public void Chained_forms_build_the_same_trees()
    {
        Assert.Equal(Some(Digit), Digit.Some());
        Assert.Equal(Any(Digit), Digit.Any());
        Assert.Equal(Opt(Digit), Digit.Opt());
        Assert.Equal(Rep(3, Digit), Digit.Times(3));
        Assert.Equal(Rep(2, 4, Digit), Digit.Times(2, 4));
        Assert.Equal(Not(Digit), Digit.Not());
        Assert.Equal(Ahead(Digit), Digit.Ahead());
        Assert.Equal(To(Digit), Digit.To());
        Assert.Equal(Thru(Digit), Digit.Thru());
        Assert.Equal(Copy("n", Digit), Digit.Copy("n"));
        Assert.Equal(Set("n", Digit), Digit.Set("n"));
        Assert.Equal(Keep(Digit), Digit.Keep());
        Assert.Equal(Into(Digit), Digit.Into());
        Assert.Equal(Remove(Digit), Digit.Remove());
        Assert.IsType<Change>(Digit.ChangeTo("x"));
    }

    [Fact]
    public void Sugared_rules_match_like_the_plain_ones()
    {
        var rule = (Digit * 3) + "-" + (+Digit) + !Alpha;
        Assert.True(rule.IsMatch("555-1234"));
        Assert.False(rule.IsMatch("555-1234x"));
        Assert.False(rule.IsMatch("55-1234"));

        var pair = Alpha.Some().Copy("k") + "=" + Digit.Some().Copy("v");
        var r = pair.Parse("width=640");
        Assert.True(r.Matched);
        Assert.Equal("width", r.Vars["k"]);
        Assert.Equal("640", r.Vars["v"]);

        var edited = (Rule)"-" ;
        Assert.Equal("a+b", Choice(edited.ChangeTo("+"), Skip).Any().Parse("a-b").Output);
        Assert.Equal("ab", Choice(Digit.Remove(), Skip).Any().Parse("a1b").Output);
    }

    [Fact]
    public void Then_runs_an_action_after_the_match()
    {
        var seen = new List<int>();
        var rule = Alpha.Some().Then(c => seen.Add(c.Pos));
        Assert.True(rule.IsMatch("abc"));
        Assert.Equal([3], seen);
    }

    [Fact]
    public void Chained_collect_forms()
    {
        var rule = Collect("w", Choice(Alpha.Some().Keep(), Skip).Any());
        var words = rule.Parse("hi there");
        Assert.Equal(new object[] { "hi", "there" }, Assert.IsAssignableFrom<IReadOnlyList<object>>(words.Vars["w"]));
    }

    [Fact]
    public void Chained_scans_and_lookahead_and_into()
    {
        Assert.True(("a" + (Rule)"=").To().IsMatch("xx=", partial: true) == false);
        Assert.True(((Rule)"=").To().IsMatch("a=b", partial: true));
        Assert.True((((Rule)"=").Thru() + "b").IsMatch("a=b"));
        Assert.True((((Rule)"a").Ahead() + "a").IsMatch("a"));
        Assert.True(Integer.Some().Into().IsMatch(new List<object> { new List<object> { 1, 2 } }));
    }

    // ---- instance shorthands ----

    [Fact]
    public void Rule_has_IsMatch_and_Parse_for_text_and_blocks()
    {
        var rule = Dialect.Read("some digit");
        Assert.True(rule.IsMatch("123"));
        Assert.True(rule.IsMatch("123x", partial: true));
        Assert.False(rule.IsMatch("123x"));
        Assert.True(rule.Parse("123").Matched);

        var words = Dialect.Read("quote a quote b");
        Assert.True(words.IsMatch(new List<object> { new Word("A"), new Word("b") }, ignoreCase: true));
        Assert.True(words.Parse(new List<object> { new Word("a"), new Word("b"), 1 }, partial: true).Matched);

        var named = Ref("d");
        var table = new Dictionary<string, Rule> { ["d"] = Digit };
        Assert.True(named.IsMatch("7", table));
        Assert.True(named.Parse("7", table).Matched);
    }

    // ---- charset ----

    [Theory]
    [InlineData("a-z", "m", true)]
    [InlineData("a-z", "A", false)]
    [InlineData("a-zA-Z_", "_", true)]
    [InlineData("a-zA-Z_", "Q", true)]
    [InlineData("a-zA-Z_", "5", false)]
    [InlineData("0-9a-fA-F", "f", true)]
    [InlineData("0-9a-fA-F", "g", false)]
    [InlineData("-+", "-", true)]
    [InlineData("-+", "+", true)]
    [InlineData("+-", "-", true)]
    [InlineData("a-", "-", true)]
    [InlineData("a-", "b", false)]
    [InlineData("a-z-", "-", true)]
    [InlineData("a-z-", "k", true)]
    [InlineData("", "a", false)]
    [InlineData("x", "x", true)]
    [InlineData("@-Z", "@", true)]
    [InlineData("@-Z", "A", true)]
    [InlineData("@-Z", "[", false)]
    [InlineData("Ā-ſ", "Ġ", true)]
    [InlineData("Ā-ſ", "ƀ", false)]
    [InlineData("Ā-ſ", "a", false)]
    [InlineData("Ā-ſ", "é", false)]
    [InlineData("a-ÿ", "z", true)]
    [InlineData("a-ÿ", "é", true)]
    [InlineData("a-ÿ", "Ā", false)]
    [InlineData("a-ÿ", "`", false)]
    public void Charset_membership(string spec, string ch, bool expected) =>
        Assert.Equal(expected, CharSet.Parse(spec).Contains(ch[0]));

    [Fact]
    public void Charset_ranges_must_not_run_backwards()
    {
        var ex = Assert.Throws<FormatException>(() => CharSet.Parse("z-a"));
        Assert.Contains("backwards", ex.Message);
        Assert.Throws<FormatException>(() => CharSet.Parse("+-*/")); // '+' (43) comes after '*' (42): write "-+*/"
        Assert.True(CharSet.Parse("-+*/").Contains('*'));
    }

    [Fact]
    public void Charset_in_rules_and_text()
    {
        var ident = Chars("a-zA-Z_") + Chars("a-zA-Z0-9_").Any();
        Assert.True(ident.IsMatch("snake_case9"));
        Assert.False(ident.IsMatch("9lives"));

        var viaText = Dialect.Read("charset \"a-zA-Z_\" any charset \"a-zA-Z0-9_\"");
        Assert.True(viaText.IsMatch("snake_case9"));
        Assert.False(viaText.IsMatch("9lives"));

        var hex = Dialect.Read("\"#\" 6 charset \"0-9a-fA-F\"");
        Assert.True(hex.IsMatch("#00ff7A"));
        Assert.False(hex.IsMatch("#00ff7G"));
        Assert.Equal("charset \"a-z\"", Assert.IsType<Item>(Chars("a-z")).Name);
    }

    [Fact]
    public void Charset_works_in_blocks_of_chars_and_with_scans()
    {
        Assert.True(Dialect.Read("some charset \"a-c\"").IsMatch(new List<object> { 'a', 'b' }));
        Assert.False(Dialect.Read("some charset \"a-c\"").IsMatch(new List<object> { 'a', "b" }));
        Assert.True(Dialect.Read("to charset \"0-9\" some digit").IsMatch("ab12"));
        Assert.True(Dialect.Read("thru charset \"0-9\" some alpha").IsMatch("1ab"));
    }

    [Fact]
    public void Charset_stays_case_sensitive_under_ignoreCase()
    {
        Assert.False(Dialect.Read("charset \"a-z\"").IsMatch("A", ignoreCase: true));
    }

    [Fact]
    public void Charset_keyword_needs_a_string()
    {
        Assert.Contains("charset", Assert.Throws<FormatException>(() => Dialect.Read("charset")).Message);
        Assert.Throws<FormatException>(() => Dialect.Read("charset digit"));
        Assert.Throws<FormatException>(() => Dialect.Read("charset \"z-a\""));
    }

    // ---- ReadGrammar ----

    [Fact]
    public void Grammar_reads_named_rules_across_lines()
    {
        var rules = Dialect.ReadGrammar("""
            group = "(" any item ")"
            item  = some alpha | group
            """);
        Assert.Equal(["group", "item"], rules.Keys.Order());
        Assert.True(Ref("group").IsMatch("(a(bc)(d(e)))", rules));
        Assert.False(Ref("group").IsMatch("(a(bc)", rules));
    }

    [Fact]
    public void Grammar_rules_can_span_lines_and_use_groups_and_comments()
    {
        var rules = Dialect.ReadGrammar("""
            list  = "[" opt [
                        item          ; the first one
                        any ["," item]
                    ] "]"
            item  = some digit | list
            """);
        Assert.True(Ref("list").IsMatch("[1,[2,3],4]", rules));
        Assert.True(Ref("list").IsMatch("[]", rules));
        Assert.False(Ref("list").IsMatch("[1,]", rules));
    }

    [Fact]
    public void Grammar_binds_actions_and_value_functions()
    {
        var count = 0;
        var acts = new Dictionary<string, Action<Ctx>> { ["tick"] = _ => count++ };
        var values = new Dictionary<string, Func<Ctx, object?>> { ["yes"] = _ => true };
        var rules = Dialect.ReadGrammar("""
            pair = copy k some alpha (@tick) "=" if (@yes) copy v some digit
            """, acts, values);
        var r = Ref("pair").Parse("a=1", rules);
        Assert.True(r.Matched);
        Assert.Equal(1, count);
        Assert.Equal("1", r.Vars["v"]);
    }

    [Fact]
    public void Grammar_keeps_a_lone_equals_inside_a_string_as_text()
    {
        var rules = Dialect.ReadGrammar("""
            kv = some alpha "=" some alpha
            ws = any " "
            """);
        Assert.True(Ref("kv").IsMatch("a=b", rules));
    }

    [Theory]
    [InlineData("", "series of")]
    [InlineData("some digit", "series of")]
    [InlineData("a = ", "empty")]
    [InlineData("a = \"x\"\na = \"y\"", "defined twice")]
    [InlineData("a = \"x\" ]", "Unmatched")]
    [InlineData("a = [ \"x\"", "Missing")]
    [InlineData("a = \"x\"\nb = ", "empty")]
    public void Grammar_errors_explain_themselves(string text, string fragment)
    {
        var ex = Assert.Throws<FormatException>(() => Dialect.ReadGrammar(text));
        Assert.Contains(fragment, ex.Message);
    }

    [Fact]
    public void Grammar_ignores_an_equals_inside_brackets()
    {
        // inside [...] the pair "x =" is not a definition boundary, so this is one rule that refers to rules x and "="
        var rules = Dialect.ReadGrammar("a = [x =] \"z\"");
        Assert.Equal(["a"], rules.Keys);
    }
}
