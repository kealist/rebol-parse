using static RebolParse.P;

namespace RebolParse.Tests;

/// <summary>Fills the gaps ParseTests leaves: builders, series, reader edge cases, error paths.</summary>
public class CoverageTests
{
    private static bool Matches(string input, string text, bool partial = false) =>
        Parser.Parse(input, Dialect.Read(text), partial: partial).Matched;

    private static bool Matches(IReadOnlyList<object> input, string text) =>
        Parser.Parse(input, Dialect.Read(text)).Matched;

    // ---- Builders and operators ----

    [Fact]
    public void Plus_and_bar_flatten_nested_sequences_and_choices()
    {
        var seq = (Rule)"a" + "b" + "c";
        Assert.Equal(3, Assert.IsType<Seq>(seq).Items.Length);
        var alt = (Rule)"a" | "b" | "c";
        Assert.Equal(3, Assert.IsType<Alt>(alt).Items.Length);
        // a Seq inside an Alt is not flattened into it
        Assert.Equal(2, Assert.IsType<Alt>(((Rule)"a" + "b") | "c").Items.Length);
    }

    [Fact]
    public void Choice_and_Seq_builders()
    {
        Assert.True(Parser.Parse("b", Choice("a", "b")).Matched);
        Assert.True(Parser.Parse("ab", Seq("a", "b")).Matched);
        Assert.False(Parser.Parse("ba", Seq("a", "b")).Matched);
    }

    [Fact]
    public void Charset_builders()
    {
        Assert.True(Parser.Parse("abc", Some(CharIn("abc"))).Matched);
        Assert.False(Parser.Parse("abd", Some(CharIn("abc"))).Matched);
        Assert.True(Parser.Parse("mno", Some(CharRange('a', 'z'))).Matched);
        Assert.False(Parser.Parse("mNo", Some(CharRange('a', 'z'))).Matched);
        Assert.True(Parser.Parse("xyz", Some(CharIf(c => c > 'w'))).Matched);
        Assert.Equal("[abc]", Assert.IsType<Item>(CharIn("abc")).Name);
        Assert.Equal("a-z", Assert.IsType<Item>(CharRange('a', 'z')).Name);
        Assert.Equal("char", Assert.IsType<Item>(CharIf(char.IsDigit)).Name);
    }

    [Fact]
    public void Char_literals_convert_implicitly_and_charsets_reject_non_chars()
    {
        Rule a = 'a';
        Assert.True(Parser.Parse("a", a).Matched);
        Assert.False(Parser.Parse(new List<object> { 1 }, Digit).Matched);
    }

    [Fact]
    public void Builtin_character_classes()
    {
        Assert.True(Parser.Parse("AB", Some(Upper)).Matched);
        Assert.False(Parser.Parse("Ab", Some(Upper)).Matched);
        Assert.True(Parser.Parse("ab", Some(Lower)).Matched);
        Assert.True(Parser.Parse("a1b2", Some(AlphaNum)).Matched);
        Assert.False(Parser.Parse("a-b", Some(AlphaNum)).Matched);
        Assert.True(Parser.Parse(" \t\n", Some(Space)).Matched);
        Assert.True(Parser.Parse("\n\t", Newline + Tab).Matched);
        Assert.False(Parser.Parse("\t\n", Newline + Tab).Matched);
    }

    [Fact]
    public void Fluent_builders_for_captures_marks_actions_and_refs()
    {
        var seen = new List<int>();
        var rule = Mark("m") + Set("c", Skip) + Copy("rest", To(End)) + Seek("m")
                   + Do(ctx => seen.Add(ctx.Pos)) + Lit("xyz");
        var result = Parser.Parse("xyz", rule);
        Assert.True(result.Matched);
        Assert.Equal('x', result.Vars["c"]);
        Assert.Equal("yz", result.Vars["rest"]);
        Assert.Equal([0], seen);

        var rules = new Dictionary<string, Rule> { ["d"] = Some(Digit) };
        Assert.True(Parser.Parse("42", Ref("d"), rules).Matched);
    }

    [Fact]
    public void Datatype_rules_in_block_mode()
    {
        var items = new List<object> { 1, 2L, 1.5, 2.5f, 3.5m, "s", new Word("w"), new List<object> { 1 } };
        var rule = Integer + Integer + P.Decimal + P.Decimal + P.Decimal + Str + WordType + BlockType;
        Assert.True(Parser.Parse(items, rule).Matched);
        Assert.True(Parser.Parse(new List<object> { 7 }, Is(o => o is 7, "seven")).Matched);
        Assert.False(Parser.Parse(new List<object> { 8 }, Is(o => o is 7)).Matched);
        Assert.False(Parser.Parse(new List<object> { "x" }, Integer).Matched);
        Assert.False(Parser.Parse(new List<object> { 1 }, Str).Matched);
        Assert.False(Parser.Parse(new List<object> { 1 }, WordType).Matched);
        Assert.False(Parser.Parse(new List<object> { 1 }, BlockType).Matched);
        Assert.False(Parser.Parse(new List<object> { 1 }, P.Decimal).Matched);
    }

    // ---- Series ----

    [Fact]
    public void Word_prints_its_name() => Assert.Equal("add", new Word("add").ToString());

    [Fact]
    public void StringSeries_literals_indexer_and_slice()
    {
        var s = new StringSeries("hello");
        Assert.Equal(5, s.Length);
        Assert.Equal('e', s[1]);
        Assert.Equal("ell", s.Slice(1, 4));
        Assert.Equal(1, s.MatchLiteral(0, 'h'));
        Assert.Equal(-1, s.MatchLiteral(0, 'x'));
        Assert.Equal(-1, s.MatchLiteral(5, 'o'));
        Assert.Equal(3, s.MatchLiteral(2, "llo"));
        Assert.Equal(-1, s.MatchLiteral(2, "lx"));
        Assert.Equal(-1, s.MatchLiteral(4, "oo"));
        Assert.Equal(-1, s.MatchLiteral(0, 42)); // only chars and strings match text
    }

    [Fact]
    public void BlockSeries_literals_indexer_and_slice()
    {
        var s = new BlockSeries(new List<object> { 1, "two", new Word("three") });
        Assert.Equal(3, s.Length);
        Assert.Equal("two", s[1]);
        Assert.Equal(1, s.MatchLiteral(0, 1));
        Assert.Equal(1, s.MatchLiteral(2, new Word("three")));
        Assert.Equal(-1, s.MatchLiteral(0, 2));
        Assert.Equal(-1, s.MatchLiteral(3, 1));
        var slice = Assert.IsAssignableFrom<IReadOnlyList<object>>(s.Slice(0, 2));
        Assert.Equal(new object[] { 1, "two" }, slice);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<object>>(s.Slice(1, 1)));
    }

    // ---- Matcher ----

    [Fact]
    public void ParseResult_converts_to_bool_and_reports_position()
    {
        ParseResult ok = Parser.Parse("ab", (Rule)"a", partial: true);
        bool b = ok;
        Assert.True(b);
        Assert.Equal(1, ok.Position);
        Assert.False(Parser.Parse("ab", (Rule)"x"));
    }

    [Fact]
    public void Skip_and_item_rules_fail_at_end_of_input()
    {
        Assert.False(Parser.Parse("", Skip).Matched);
        Assert.False(Parser.Parse("", Digit).Matched);
        Assert.False(Parser.Parse(new List<object>(), Integer).Matched);
        Assert.False(Parser.Parse(new List<object>(), Lit(1)).Matched);
    }

    [Fact]
    public void Set_stores_null_when_the_body_consumes_nothing()
    {
        var result = Parser.Parse("", Set("c", Opt("x")));
        Assert.True(result.Matched);
        Assert.True(result.Vars.ContainsKey("c"));
        Assert.Null(result.Vars["c"]);
    }

    [Fact]
    public void Copy_in_block_mode_captures_a_list()
    {
        var result = Parser.Parse(new List<object> { 1, 2, 3 }, Copy("xs", Some(Integer)));
        Assert.Equal(new object[] { 1, 2, 3 }, Assert.IsAssignableFrom<IReadOnlyList<object>>(result.Vars["xs"]));
    }

    [Fact]
    public void Failed_copy_and_set_leave_vars_untouched()
    {
        var result = Parser.Parse("x", Copy("a", Digit) | Set("b", Digit) | "x");
        Assert.True(result.Matched);
        Assert.Empty(result.Vars);
    }

    [Fact]
    public void Into_fails_on_non_blocks_end_of_input_and_partial_inner_matches()
    {
        Assert.False(Parser.Parse(new List<object>(), Into(Any(Integer))).Matched);
        Assert.False(Parser.Parse(new List<object> { 5 }, Into(Any(Integer))).Matched);
        Assert.False(Parser.Parse(new List<object> { "text" }, Into(Any(Skip))).Matched);
        // inner rule stops early, leaving 2 behind: into requires the whole block
        Assert.False(Parser.Parse(new List<object> { new List<object> { 1, 2 } }, Into(Integer)).Matched);
        Assert.True(Parser.Parse(new List<object> { new List<object>() }, Into(Any(Integer))).Matched);
        // inner rule fails outright
        Assert.False(Parser.Parse(new List<object> { new List<object> { "x" } }, Into(Integer)).Matched);
    }

    [Fact]
    public void Undefined_mark_throws()
    {
        Assert.Throws<KeyNotFoundException>(() => Parser.Parse("a", Seek("nowhere")));
        Assert.Throws<KeyNotFoundException>(() => Parser.Parse("a", Dialect.Read(":nowhere")));
    }

    [Fact]
    public void Unknown_rule_node_throws()
    {
        Assert.Throws<NotSupportedException>(() => Parser.Parse("a", new MysteryRule()));
    }

    private sealed record MysteryRule : Rule;

    [Fact]
    public void Break_in_a_loop_stops_only_that_loop()
    {
        // inner loop breaks on 'b'; the outer loop keeps going
        var rule = Dialect.Read("some [any [\"b\" break | \"a\"] \"x\"]");
        Assert.True(Parser.Parse("aabxabx", rule).Matched);
    }

    [Fact]
    public void Loop_respects_max_and_leaves_extra_input()
    {
        var r = Parser.Parse("aaaa", Rep(0, 2, "a"), partial: true);
        Assert.True(r.Matched);
        Assert.Equal(2, r.Position);
    }

    [Fact]
    public void Thru_and_to_scan_to_end_of_input_and_can_fail()
    {
        Assert.True(Matches("abc", "thru end"));
        Assert.False(Matches("abc", "thru \"z\""));
        Assert.True(Matches("abc", "to \"\" to end")); // empty literal matches at once
        Assert.True(Matches("abc", "to \"c\" \"c\""));
    }

    [Fact]
    public void Not_and_ahead_do_not_consume()
    {
        Assert.False(Matches("a", "not \"a\" skip"));
        Assert.True(Matches("a", "not \"b\" skip"));
        Assert.True(Matches("a", "ahead \"a\" skip"));
        Assert.False(Matches("a", "ahead \"b\" skip"));
    }

    // ---- Dialect: reading ----

    [Theory]
    [InlineData("SOME DIGIT", "123", true)]
    [InlineData("Any Alpha", "", true)]
    [InlineData("some alnum", "a1", true)]
    [InlineData("some upper", "AB", true)]
    [InlineData("some lower", "ab", true)]
    [InlineData("some space", " \t", true)]
    [InlineData("newline tab", "\n\t", true)]
    [InlineData("none", "", true)]
    [InlineData("fail", "", false)]
    [InlineData("skip", "z", true)]
    [InlineData("opt \"a\" end", "", true)]
    public void Keywords_and_builtins_are_case_insensitive(string text, string input, bool expected) =>
        Assert.Equal(expected, Matches(input, text));

    [Fact]
    public void Block_datatype_words_in_text()
    {
        var input = new List<object> { 1, 2.5, "s", new Word("w"), new List<object>() };
        Assert.True(Matches(input, "integer! decimal! string! word! block!"));
        Assert.False(Matches(input, "integer! integer!"));
    }

    [Fact]
    public void Outer_brackets_are_optional_and_nesting_works()
    {
        Assert.True(Matches("ab", "\"a\" \"b\""));
        Assert.True(Matches("ab", "[\"a\" \"b\"]"));
        Assert.True(Matches("b", "[[\"a\"] | [[\"b\"]]]"));
        Assert.True(Matches("", "[]"));
    }

    [Fact]
    public void Char_literals_and_escapes()
    {
        Assert.True(Matches("a", "#\"a\""));
        Assert.True(Matches("\n", "#\"^/\""));
        Assert.True(Matches("\t", "#\"^-\""));
        Assert.True(Matches("\"", "#\"^\"\""));
        Assert.True(Matches("^", "#\"^^\""));
        Assert.True(Matches("a\nb\tc\"d^e", "\"a^/b^-c^\"d^^e\""));
        Assert.True(Matches("q", "\"^q\"")); // unknown escape keeps the character
    }

    [Theory]
    [InlineData("#\"\"")]
    [InlineData("#\"ab\"")]
    public void Char_literals_must_be_one_character(string text) =>
        Assert.Throws<FormatException>(() => Dialect.Read(text));

    [Fact]
    public void Hash_without_quote_is_an_ordinary_word()
    {
        var rules = new Dictionary<string, Rule> { ["#tag"] = "t" };
        Assert.True(Parser.Parse("t", Dialect.Read("#tag"), rules).Matched);
        Assert.True(Parser.Parse("t", Dialect.Read("#tag"), rules, partial: true).Matched);
    }

    [Fact]
    public void Comments_run_to_end_of_line()
    {
        Assert.True(Matches("ab", "\"a\" ; skip this \"z\" ]\n\"b\" ; trailing"));
        Assert.True(Matches("a", "\"a\" ;"));
    }

    [Fact]
    public void Integer_repeat_forms()
    {
        Assert.True(Matches("aaa", "3 \"a\""));
        Assert.True(Matches("aa", "1 3 \"a\""));
        Assert.False(Matches("aaaa", "1 3 \"a\""));
        Assert.True(Matches("abab", "2 [\"a\" \"b\"]"));
        Assert.True(Matches("", "0 \"a\""));
    }

    [Fact]
    public void Get_and_set_words_are_marks_and_seeks()
    {
        Assert.True(Matches("abab", "m: \"ab\" :m \"ab\" :m", partial: true));
        // a lone ':' is an ordinary word
        Assert.Throws<KeyNotFoundException>(() => Parser.Parse("a", Dialect.Read(":")));
    }

    [Fact]
    public void Quote_accepts_strings_chars_integers_and_words()
    {
        Assert.True(Parser.Parse(new List<object> { "s" }, Dialect.Read("quote \"s\"")).Matched);
        Assert.True(Parser.Parse(new List<object> { 'c' }, Dialect.Read("quote #\"c\"")).Matched);
        Assert.True(Parser.Parse(new List<object> { 7 }, Dialect.Read("quote 7")).Matched);
        Assert.True(Parser.Parse(new List<object> { new Word("w") }, Dialect.Read("quote w")).Matched);
        Assert.False(Parser.Parse(new List<object> { new Word("z") }, Dialect.Read("quote w")).Matched);
    }

    [Theory]
    [InlineData("quote")]
    [InlineData("quote [")]
    [InlineData("quote |")]
    public void Bad_quote_throws(string text) =>
        Assert.Throws<FormatException>(() => Dialect.Read(text));

    [Theory]
    [InlineData("some")]
    [InlineData("any")]
    [InlineData("opt")]
    [InlineData("ahead")]
    [InlineData("not")]
    [InlineData("to")]
    [InlineData("thru")]
    [InlineData("into")]
    [InlineData("copy x")]
    [InlineData("set x")]
    [InlineData("3")]
    [InlineData("1 2")]
    public void Keyword_with_nothing_to_apply_to_throws(string text) =>
        Assert.Throws<FormatException>(() => Dialect.Read(text));

    [Theory]
    [InlineData("copy [")]
    [InlineData("set \"x\" skip")]
    [InlineData("copy 3 skip")]
    public void Copy_and_set_need_a_word_for_the_variable(string text) =>
        Assert.Throws<FormatException>(() => Dialect.Read(text));

    [Theory]
    [InlineData("some |")]
    [InlineData("some ]")]
    [InlineData("[ \"a\"")]
    [InlineData("[ \"a\" | ")]
    [InlineData("\"a\" ]")]
    [InlineData("(@x")]
    [InlineData("(")]
    [InlineData("\"a^")]
    [InlineData("\"")]
    public void Structural_errors_throw(string text) =>
        Assert.Throws<FormatException>(() => Dialect.Read(text));

    [Fact]
    public void Paren_code_must_be_a_named_action()
    {
        Assert.Throws<FormatException>(() => Dialect.Read("(x)"));
        Assert.Throws<FormatException>(() => Dialect.Read("( (nested) )"));
        Assert.Throws<FormatException>(() => Dialect.Read("()"));
    }

    [Fact]
    public void Unregistered_action_throws()
    {
        Assert.Throws<KeyNotFoundException>(() => Dialect.Read("(@log)"));
        var acts = new Dictionary<string, Action<Ctx>> { ["other"] = _ => { } };
        Assert.Throws<KeyNotFoundException>(() => Dialect.Read("(@log)", acts));
        Assert.Throws<KeyNotFoundException>(() => Dialect.Read("(@log (x))", acts));
    }

    [Fact]
    public void Actions_can_be_padded_and_nest_in_groups()
    {
        var hits = 0;
        var acts = new Dictionary<string, Action<Ctx>> { ["hit"] = _ => hits++ };
        Assert.True(Parser.Parse("ab", Dialect.Read("\"a\" ( @hit ) [\"b\" (@hit)]", acts)).Matched);
        Assert.Equal(2, hits);
    }

    [Fact]
    public void ReadRules_binds_actions_and_supports_mutual_reference()
    {
        var hits = 0;
        var acts = new Dictionary<string, Action<Ctx>> { ["hit"] = _ => hits++ };
        var rules = Dialect.ReadRules(new Dictionary<string, string>
        {
            ["a"] = "\"a\" (@hit) opt b",
            ["b"] = "\"b\" opt a",
        }, acts);
        Assert.True(Parser.Parse("abab", Ref("a"), rules).Matched);
        Assert.Equal(2, hits);
    }

    [Fact]
    public void Parse_against_empty_rule_table_by_default()
    {
        Assert.Throws<KeyNotFoundException>(() => Parser.Parse("a", Ref("missing")));
        Assert.Throws<KeyNotFoundException>(() => Parser.Parse(new List<object>(), Ref("missing")));
    }
}
