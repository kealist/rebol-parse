using static RebolParse.P;

namespace RebolParse.Tests;

/// <summary>The IL back end must give exactly the interpreter's answers.</summary>
public class CompiledRuleTests
{
    private static readonly Dictionary<string, Rule> Table = new()
    {
        ["dig"] = Some(Digit),
        ["ab"] = Choice("a", "b"),
        ["word"] = Some(Alpha) + Opt("-" + Ref("word")),
        ["cs"] = Chars("a-c"),
        ["kw"] = Keep(Some(Alpha)),               // rules with side effects: calls to these use the careful variant
        ["brk"] = Seq(Alpha, Break),
        ["ed"] = Remove(Digit),
    };

    // No bare inserts: an insert that is followed by a failure stays in the text, so a choice or loop around one can
    // grow the text as fast as it advances and never end. Change still exercises the insert path, and never grows the text.
    private static Rule Gen(Random rng, int depth)
    {
        if (depth <= 0 || rng.Next(5) == 0)
            return rng.Next(14) switch
            {
                0 => Lit("a"),
                1 => Lit("b"),
                2 => Lit("abc"),
                3 => Lit('c'),
                4 => Lit('A'),
                5 => Lit("-"),
                6 => Skip,
                7 => Digit,
                8 => Alpha,
                9 => Ref(new[] { "dig", "ab", "word", "cs", "kw", "brk", "ed" }[rng.Next(7)]),
                10 => Lit(""),
                11 => Lit(7),
                12 => Chars("a-b"),
                _ => Is(o => o is char ch && ch == 'b'),
            };
        var d = depth - 1;
        return rng.Next(26) switch
        {
            0 => Seq(Gen(rng, d), Gen(rng, d)),
            1 => Seq(Gen(rng, d), Gen(rng, d), Gen(rng, d)),
            2 => Choice(Gen(rng, d), Gen(rng, d)),
            3 => Choice(Gen(rng, d), Gen(rng, d), Gen(rng, d)),
            4 => Any(Gen(rng, d)),
            5 => Some(Gen(rng, d)),
            6 => Opt(Gen(rng, d)),
            7 => Rep(rng.Next(0, 3), rng.Next(3, 5), Gen(rng, d)),
            8 => Not(Gen(rng, d)),
            9 => Ahead(Gen(rng, d)),
            10 => Copy("v", Gen(rng, d)),
            11 => Set("w", Gen(rng, d)),
            12 => rng.Next(2) == 0 ? To(Gen(rng, d)) : Thru(Gen(rng, d)),
            13 => Seq(Gen(rng, d), End),
            14 => Rep(rng.Next(0, 3), rng.Next(0, 4), Seq(Not(CharIn("ab")), Skip)),
            15 => Choice(Seq(Gen(rng, d), Fail), Gen(rng, d)),
            16 => Keep(Gen(rng, d)),
            17 => KeepPick(Gen(rng, d)),
            18 => Collect("c", Gen(rng, d)),
            19 => Collect("c", Any(Choice(Keep(Gen(rng, d)), Skip))),
            20 => Keep(ctx => ctx.Pos),
            21 => Any(Choice(Seq(Gen(rng, d), Break), Gen(rng, d))),
            22 => Any(Choice(Seq(Gen(rng, d), Reject), Gen(rng, d))),
            23 => Seq(Gen(rng, d), Rng(rng, d)),
            24 => Remove(Gen(rng, d)),
            _ => Change(Seq(Skip, Gen(rng, d)), "X"),
        };
    }

    private static Rule Rng(Random rng, int d) => rng.Next(3) switch
    {
        0 => Break,
        1 => Reject,
        _ => Gen(rng, d),
    };

    private static string Describe(ParseResult r) =>
        $"{r.Matched} @{r.Position} v={r.Vars.GetValueOrDefault("v")} w={r.Vars.GetValueOrDefault("w")} " +
        $"c={(r.Vars.GetValueOrDefault("c") is IEnumerable<object> c ? string.Join("|", c) : null)} out={r.Output}";

    [Fact]
    public void Random_rules_match_the_interpreter()
    {
        var rng = new Random(20261019);
        var compiledTable = Table.Compiled();
        var native = 0;
        for (var i = 0; i < 4000; i++)
        {
            var rule = Gen(rng, 4);
            if (rng.Next(4) == 0) rule = Seq(Mark("m"), rule, Seek("m"), Gen(rng, 2));
            var compiled = rule.Compiled(Table);
            if (((CompiledRule)compiled).IsNative) native++;
            var len = rng.Next(0, 10);
            var input = new string(Enumerable.Range(0, len).Select(_ => "abcAB-1x"[rng.Next(8)]).ToArray());
            var partial = rng.Next(2) == 0;
            var ignoreCase = rng.Next(2) == 0;
            string a, b;
            try { a = Describe(Parser.Parse(input, rule, Table, partial, ignoreCase)); } catch (Exception ex) { a = ex.GetType().Name; }
            try { b = Describe(Parser.Parse(input, compiled, compiledTable, partial, ignoreCase)); } catch (Exception ex) { b = ex.GetType().Name; }
            Assert.True(a == b, $"input '{input}' partial={partial} ignoreCase={ignoreCase}\n rule {rule}\n interpreter {a}\n compiled    {b}");
            string c, d;
            try { c = Parser.IsMatch(input, rule, Table, partial, ignoreCase).ToString(); } catch (Exception ex) { c = ex.GetType().Name; }
            try { d = Parser.IsMatch(input, compiled, compiledTable, partial, ignoreCase).ToString(); } catch (Exception ex) { d = ex.GetType().Name; }
            Assert.True(c == d, $"IsMatch on '{input}'\n rule {rule}\n interpreter {c}\n compiled    {d}");
        }
        Assert.Equal(4000, native);   // every generated rule is in the IL subset, so none quietly fell back
    }

    // the same rows the text and block engines are held to
    [Theory, MemberData(nameof(EngineParityTests.Both), MemberType = typeof(EngineParityTests))]
    public void Existing_parity_rows_hold_when_compiled(string input, string text, bool expected)
    {
        var values = new Dictionary<string, Func<Ctx, object?>>
        {
            ["yes"] = _ => true, ["no"] = _ => false, ["pos"] = c => c.Pos,
        };
        var rule = Dialect.Read(text, values: values).Compiled();
        Assert.Equal(expected, Parser.Parse(input, rule).Matched);
        Assert.Equal(expected, Parser.IsMatch(input, rule));
    }

    [Fact]
    public void Simple_rules_really_run_as_IL()
    {
        Assert.True((Dialect.Read("3 digit \"-\" 4 digit").Compiled() as CompiledRule)!.IsNative);
        Assert.True(Dialect.Read("some [alpha | digit]").Compiled().Compiled() is CompiledRule { IsNative: true });
    }

    // rules that edit and call other rules: the table is compiled together so the callers read the edit buffer too
    [Theory]
    [InlineData("any [remove digit | skip] word")]
    [InlineData("any [change dig \"#\" | skip]")]
    [InlineData("any [remove dig | word | skip]")]
    [InlineData("insert \"x\" word")]
    [InlineData("any [copy v dig (@note) | change ab \"Z\" | skip]")]
    public void Edits_that_call_other_rules_compile_and_agree(string text)
    {
        var acts = new Dictionary<string, Action<Ctx>> { ["note"] = _ => { } };
        var rule = Dialect.Read(text, acts);
        var compiled = rule.Compiled();
        Assert.True(((CompiledRule)compiled).IsNative);
        foreach (var input in new[] { "", "ab", "a1b2", "aaab", "123", "ab-cd" })
        {
            string x, y;
            try { x = Describe(Parser.Parse(input, rule, Table, partial: true)); } catch (Exception ex) { x = ex.GetType().Name; }
            try { y = Describe(Parser.Parse(input, compiled, Table.Compiled(), partial: true)); } catch (Exception ex) { y = ex.GetType().Name; }
            Assert.Equal(x, y);
        }
    }

    [Fact]
    public void A_rule_that_reads_the_string_cannot_call_a_rule_that_edits_unless_the_table_is_compiled_together()
    {
        var parts = new Dictionary<string, Rule>
        {
            ["strip"] = Dialect.Read("any [remove digit]"),
            ["main"] = Dialect.Read("\"x\" strip \"y\""),
        };
        var one = new Dictionary<string, Rule> { ["strip"] = parts["strip"].Compiled(), ["main"] = parts["main"].Compiled() };
        var error = Assert.Throws<InvalidOperationException>(() => Parser.Parse("x12y", Ref("main"), one));
        Assert.Contains("Compiled()", error.Message);
        var together = Parser.Parse("x12y", Ref("main"), parts.Compiled());
        Assert.True(together.Matched);
        Assert.Equal("xy", together.Output);
        Assert.Equal(Parser.Parse("x12y", Ref("main"), parts).Output, together.Output);
    }

    // edits compile when the rule does not call others
    [Theory]
    [InlineData("any [remove digit | skip]")]
    [InlineData("any [change some digit \"#\" | skip]")]
    [InlineData("insert \"x\" to end")]
    [InlineData("any [remove \"a\" skip change \"c\" \"CC\" | skip]")]
    [InlineData("to [skip remove \"b\"] \"a\" \"c\"")]
    [InlineData("ahead [skip remove \"b\"] \"a\" \"c\"")]
    [InlineData("not [skip remove \"x\"] skip skip skip")]
    [InlineData("m: skip remove \"b\" :m \"a\" \"c\"")]
    [InlineData("[\"a\" remove \"b\" \"z\" | \"a\" \"c\"]")]
    [InlineData("copy v [skip remove \"b\" skip]")]
    [InlineData("collect c [skip remove \"b\" keep skip]")]
    [InlineData("thru [remove \"b\" \"x\"] to end")]
    [InlineData("any [remove some digit | some alpha | skip]")]
    [InlineData("any [remove [some digit \",\"] | skip] to end")]
    [InlineData("any [\"x\" insert \"yy\" | skip]")]
    // a failed attempt edits further along than it started, and the text is read again from the start
    [InlineData("not [skip remove \"b\" \"z\"] \"a\" \"c\"")]
    [InlineData("any [skip remove \"b\" \"z\"] \"a\" \"c\"")]
    [InlineData("to [skip skip remove \"c\" \"z\"] to end")]
    [InlineData("thru [skip skip remove \"c\" \"z\"] to end")]
    [InlineData("collect c [keep [skip remove \"b\" skip]]")]
    [InlineData("collect c [keep pick [skip remove \"b\" skip]]")]
    [InlineData("set v [skip remove \"b\" skip]")]
    [InlineData("remove \"a\" to #\"C\" to end")]
    [InlineData("remove \"a\" thru #\"c\" to end")]
    [InlineData("remove \"a\" to \"BC\" to end")]
    [InlineData("any [remove \"abcab\" | skip]")]
    public void Edits_compile_and_agree(string text)
    {
        var rule = Dialect.Read(text);
        var compiled = rule.Compiled();
        Assert.True(((CompiledRule)compiled).IsNative);
        foreach (var input in new[] { "", "abc", "abxc", "a1b22c333", "xx", "ab,12,c", "1,2,3", "aaab", "abcd", "abcabc" })
            foreach (var partial in new[] { true, false })
                foreach (var ignoreCase in new[] { false, true })
                {
                    string x, y;
                    try { x = Describe(Parser.Parse(input, rule, partial: partial, ignoreCase: ignoreCase)); } catch (Exception ex) { x = ex.GetType().Name; }
                    try { y = Describe(Parser.Parse(input, compiled, partial: partial, ignoreCase: ignoreCase)); } catch (Exception ex) { y = ex.GetType().Name; }
                    Assert.Equal(x, y);
                }
    }

    // every small attempt-then-edit-then-fail shape inside to, thru, not, any and ahead, on every short input
    [Fact]
    public void Rewinding_past_edits_agrees_on_every_small_case()
    {
        var starts = new[] { "\"a\"", "\"b\"", "skip", "none" };
        var gaps = new[] { "", "skip", "skip skip" };
        var edits = new[] { "remove \"a\"", "remove \"b\"", "remove \"c\"", "change \"b\" \"Z\"" };
        var ends = new[] { "\"a\"", "\"b\"", "\"z\"", "end" };
        var wrappers = new[] { "to [{0}] to end", "thru [{0}] to end", "not [{0}] to end", "any [{0}] to end", "ahead [{0}] to end", "any [{0} | skip]" };
        var inputs = new List<string> { "" };
        for (var round = 0; round < 5; round++)
            inputs.AddRange(inputs.Where(i => i.Length == round).SelectMany(i => new[] { i + "a", i + "b", i + "c" }).ToList());
        var checkedCases = 0;
        foreach (var start in starts)
            foreach (var gap in gaps)
                foreach (var edit in edits)
                    foreach (var end in ends)
                        foreach (var wrapper in wrappers)
                        {
                            var rule = Dialect.Read(string.Format(wrapper, $"{start} {gap} {edit} {end}"));
                            var compiled = rule.Compiled();
                            foreach (var input in inputs)
                            {
                                var x = Describe(Parser.Parse(input, rule, partial: true));
                                var y = Describe(Parser.Parse(input, compiled, partial: true));
                                Assert.True(x == y, $"{string.Format(wrapper, $"{start} {gap} {edit} {end}")} on '{input}'\n interpreter {x}\n compiled    {y}");
                                checkedCases++;
                            }
                        }
        Assert.True(checkedCases > 100_000);
    }

    [Theory]
    [InlineData("some [\"a\" break | \"b\"]")]
    [InlineData("collect c some [keep alpha]")]
    [InlineData("any [\"a\" reject | skip]")]
    [InlineData("collect c [keep pick skip] to end")]
    [InlineData("collect c [keep (@one)] to end")]
    [InlineData("collect c any [keep some alpha | skip]")]
    [InlineData("collect c [any [[keep \"a\" \"z\"] | keep \"a\"] skip]")]
    [InlineData("reject")]
    [InlineData("keep skip")]
    // a failed attempt takes back what it kept: inside not, a scan, a loop body and a lookahead
    [InlineData("collect c [not [keep \"a\" \"z\"] some skip]")]
    [InlineData("collect c [thru [keep \"a\" \"z\"] skip]")]
    [InlineData("collect c [to [keep \"a\" \"z\"] to end]")]
    [InlineData("collect c [any [keep \"a\" \"z\"] to end]")]
    [InlineData("collect c [ahead [keep \"a\" \"z\"] | keep \"a\" to end]")]
    [InlineData("collect c [any [keep \"a\" [break | none]] keep skip]")]
    [InlineData("collect c some [keep \"a\" reject | keep skip]")]
    [InlineData("any [any [\"a\" reject] | \"a\"]")]
    // a flag that is already set when an attempt starts must survive the attempt failing
    [InlineData("collect c any [\"a\" break [keep \"z\" \"q\" | keep \"b\"] skip]")]
    [InlineData("collect c any [\"a\" reject [keep \"z\" \"q\" | keep \"b\"] skip]")]
    [InlineData("collect c any [\"a\" break not [keep \"z\" \"q\"] skip]")]
    public void Break_reject_and_collect_compile_and_agree(string text)
    {
        var values = new Dictionary<string, Func<Ctx, object?>> { ["one"] = _ => 1 };
        var rule = Dialect.Read(text, null, values);
        var compiled = rule.Compiled();
        Assert.True(((CompiledRule)compiled).IsNative);
        foreach (var input in new[] { "", "ab", "a1b2", "aaab", "abz", "123", "aaaz" })
            foreach (var partial in new[] { true, false })
            {
                string x, y;
                try { x = Describe(Parser.Parse(input, rule, partial: partial)); } catch (Exception ex) { x = ex.GetType().Name; }
                try { y = Describe(Parser.Parse(input, compiled, partial: partial)); } catch (Exception ex) { y = ex.GetType().Name; }
                Assert.Equal(x, y);
            }
    }

    [Fact]
    public void Captures_marks_actions_and_conditions()
    {
        var seen = new List<string>();
        var rule = Dialect.Read("m: copy a some digit (@note) :m copy b some digit if (@yes) set c skip to end",
            new Dictionary<string, Action<Ctx>> { ["note"] = c => seen.Add($"{c.Pos}:{c.Vars["a"]}") },
            new Dictionary<string, Func<Ctx, object?>> { ["yes"] = c => c.Pos == 3 });
        var r = Parser.Parse("123x45", rule.Compiled());
        Assert.True(r.Matched);
        Assert.Equal("123", r.Vars["b"]);
        Assert.Equal('x', r.Vars["c"]);
        Assert.Equal(["3:123"], seen);
        var empty = Parser.Parse("", Dialect.Read("set c opt skip").Compiled());
        Assert.Null(empty.Vars["c"]);
        Assert.False(Parser.Parse("12", Dialect.Read("some digit if (@no)", null, new Dictionary<string, Func<Ctx, object?>> { ["no"] = _ => false }).Compiled()).Matched);
    }

    [Fact]
    public void A_compiled_table_calls_itself_recursively()
    {
        var rules = Dialect.ReadGrammar("group = \"(\" any group \")\"").Compiled();
        Assert.True(Parser.Parse("(()(()))", Ref("group"), rules).Matched);
        Assert.False(Parser.Parse("(()", Ref("group"), rules).Matched);
        Assert.True(Parser.IsMatch("(())", Ref("group").Compiled(), rules));
        Assert.Throws<KeyNotFoundException>(() => Parser.Parse("x", Ref("nope").Compiled(), rules));
    }

    [Fact]
    public void A_compiled_rule_can_call_a_pure_interpreted_rule_but_not_an_impure_one()
    {
        var pure = new Dictionary<string, Rule> { ["d"] = Some(Digit) };
        Assert.True(Parser.Parse("123", Ref("d").Compiled(), pure).Matched);
        Assert.False(Parser.Parse("abc", Ref("d").Compiled(), pure).Matched);
        var impure = new Dictionary<string, Rule> { ["d"] = Remove(Some(Digit)) };
        Assert.Throws<InvalidOperationException>(() => Parser.Parse("123", Ref("d").Compiled(), impure));
    }

    [Fact]
    public void A_compiled_rule_can_run_after_another_rule_edited_the_text()
    {
        var rule = Seq(Remove("a"), Ref("c"));
        var rules = new Dictionary<string, Rule> { ["c"] = Lit("b").Compiled() };
        var r = Parser.Parse("ab", rule, rules);
        Assert.True(r.Matched);
        Assert.Equal("b", r.Output);
    }

    [Fact]
    public void Rule_tables_are_reused_across_different_tables()
    {
        var rule = Ref("x").Compiled();
        var first = new Dictionary<string, Rule> { ["x"] = Lit("a").Compiled() };
        var second = new Dictionary<string, Rule> { ["x"] = Lit("b").Compiled() };
        Assert.True(Parser.Parse("a", rule, first).Matched);
        Assert.True(Parser.Parse("b", rule, second).Matched);
        Assert.False(Parser.Parse("a", rule, second).Matched);
    }

    [Fact]
    public void Block_input_uses_the_interpreter()
    {
        var rule = Dialect.Read("integer! string!").Compiled();
        Assert.True(Parser.Parse(new List<object> { 1, "x" }, rule).Matched);
        Assert.False(Parser.Parse(new List<object> { "x", 1 }, rule).Matched);
    }

    [Fact]
    public void Compiled_rules_nest_inside_other_rules()
    {
        Assert.True(Parser.Parse("ab", Seq("a", Lit("b").Compiled()).Compiled()).Matched);
        Assert.Equal(2, Parser.Parse("xxby", To(Lit("b").Compiled()).Compiled(), partial: true).Position);
        Assert.True(Parser.Parse("123", Some(Digit.Compiled()).Compiled()).Matched);
        Assert.Equal(4, Parser.Parse("xxbx", Thru(Seq(Lit("b").Compiled(), Skip)).Compiled(), partial: true).Position);
        Assert.False(Parser.Parse("x", new Alt([]).Compiled()).Matched);
    }

    [Fact]
    public void Character_tests_may_be_lambdas_methods_or_sets()
    {
        var rule = Some(Seq(CharIf(c => c == 'x', "x"), Chars("y-z"), CharIn("12"), Digit)).Compiled();
        Assert.True(Parser.Parse("xy1" + "5" + "xz25", rule).Matched);
        Assert.False(Parser.Parse("xy35", rule).Matched);
        Assert.Equal(1, Parser.Parse("xa", Some(CharIf(c => c == 'x', "x")).Compiled(), partial: true).Position);

        // a static method closed over its first argument has a target but is still static
        var closed = (Func<char, bool>)Delegate.CreateDelegate(typeof(Func<char, bool>), "bc", typeof(CompiledRuleTests).GetMethod(nameof(InSet))!);
        Assert.Equal(2, Parser.Parse("bcd", Some(CharIf(closed, "in set")).Compiled(), partial: true).Position);
    }

    public static bool InSet(string set, char c) => set.Contains(c);

    [Fact]
    public void Non_ascii_characters_ignore_case_like_the_interpreter()
    {
        foreach (var rule in new[] { Lit('é'), Lit("é"), Lit("éa") })
            foreach (var input in new[] { "é", "É", "éA", "ÉA", "e" })
            {
                var a = Parser.Parse(input, rule, partial: true, ignoreCase: true);
                var b = Parser.Parse(input, rule.Compiled(), partial: true, ignoreCase: true);
                Assert.Equal((a.Matched, a.Position), (b.Matched, b.Position));
            }
    }

    [Fact]
    public void A_compiled_rule_that_edits_can_call_an_interpreted_rule()
    {
        // the called rule is interpreted and edits ahead of where it started, then puts the position back
        var rules = new Dictionary<string, Rule> { ["peek"] = Ahead(Seq(Skip, Remove("b"))) };
        var rule = Seq(Ref("peek"), "a", Remove("c"));
        var interpreted = Parser.Parse("abc", rule, rules);
        var compiled = Parser.Parse("abc", rule.Compiled(), rules);
        Assert.True(interpreted.Matched);
        Assert.Equal("a", interpreted.Output);
        Assert.Equal(Describe(interpreted), Describe(compiled));

        // and when the interpreted rule does not match
        var failing = new Dictionary<string, Rule> { ["peek"] = Ahead(Seq(Skip, Remove("q"))) };
        Assert.False(Parser.Parse("abc", rule, failing).Matched);
        Assert.False(Parser.Parse("abc", rule.Compiled(), failing).Matched);
    }

    private sealed record Custom : Rule;

    [Fact]
    public void A_rule_type_the_engine_does_not_know_is_rejected_compiled_or_not()
    {
        Assert.Throws<NotSupportedException>(() => Parser.IsMatch("x", new Custom()));
        Assert.Throws<NotSupportedException>(() => Parser.IsMatch("x", new Custom().Compiled()));
    }

    [Fact]
    public void Compiling_a_table_twice_keeps_it_compiled()
    {
        var once = Table.Compiled();
        var twice = once.Compiled();
        Assert.All(twice.Values, rule => Assert.IsType<CompiledRule>(rule));
        Assert.True(Parser.Parse("ab-cd", Ref("word"), twice).Matched);
    }

    [Fact]
    public void An_entry_rule_compiled_with_its_table_follows_edits_made_by_the_rules_it_calls()
    {
        var parts = new Dictionary<string, Rule>
        {
            ["strip"] = Dialect.Read("any [remove digit]"),
            ["mid"] = Dialect.Read("strip"),
        };
        var table = parts.Compiled();
        var entry = Dialect.Read("\"x\" mid \"y\"");
        var alone = entry.Compiled();
        Assert.Throws<InvalidOperationException>(() => Parser.Parse("x12y", alone, table));
        var withTable = entry.Compiled(table);
        var r = Parser.Parse("x12y", withTable, table);
        Assert.True(r.Matched);
        Assert.Equal("xy", r.Output);
        Assert.Same(withTable, withTable.Compiled(table));
        // a table with no edits needs nothing special
        var plain = new Dictionary<string, Rule> { ["d"] = Some(Digit) }.Compiled();
        Assert.True(Parser.Parse("12", Ref("d").Compiled(plain), plain).Matched);
    }

    [Fact]
    public void Side_effects_in_a_called_rule_keep_the_careful_variant()
    {
        // item keeps, so a loop over it must take back a failed iteration's kept items
        var parts = new Dictionary<string, Rule> { ["item"] = Dialect.Read("keep some alpha \",\"") };
        var rule = Dialect.Read("collect c any item");
        foreach (var input in new[] { "ab,cd,", "ab,cd", "ab,cd,e", "," })
        {
            var x = Describe(Parser.Parse(input, rule, parts, partial: true));
            var y = Describe(Parser.Parse(input, rule.Compiled(parts.Compiled()), parts.Compiled(), partial: true));
            Assert.Equal(x, y);
        }
        // the side effect can be further down: item calls the rule that keeps
        var deep = new Dictionary<string, Rule>
        {
            ["leaf"] = Dialect.Read("keep some alpha"),
            ["item"] = Dialect.Read("leaf \",\""),
        };
        foreach (var input in new[] { "ab,cd,", "ab,cd", "ab,cd,e" })
            Assert.Equal(Describe(Parser.Parse(input, rule, deep, partial: true)),
                         Describe(Parser.Parse(input, rule.Compiled(deep.Compiled()), deep.Compiled(), partial: true)));
        // and a table whose rules are free of side effects takes the fast one
        var free = new Dictionary<string, Rule> { ["item"] = Dialect.Read("some alpha \",\"") };
        var fast = free.Compiled();
        foreach (var input in new[] { "ab,cd,", "ab,cd", "" })
            Assert.Equal(Describe(Parser.Parse(input, Dialect.Read("any item"), free, partial: true)),
                         Describe(Parser.Parse(input, Dialect.Read("any item").Compiled(fast), fast, partial: true)));
    }

    [Fact]
    public void Short_literals_are_compared_inline_and_agree_with_the_interpreter()
    {
        foreach (var literal in new[] { "ab", "AbC", "abcd", "abcde", "é1", "1-2", "\U0001F600x", "ß" })
        {
            var rule = Lit(literal);
            foreach (var input in new[] { "", "a", "ab", "AB", "abc", "ABCD", "abcde", "ABCDE", "É1", "é1", "1-2", "\U0001F600x", "ss", "SS" })
                foreach (var ignoreCase in new[] { false, true })
                {
                    var a = Parser.Parse(input, rule, partial: true, ignoreCase: ignoreCase);
                    var b = Parser.Parse(input, rule.Compiled(), partial: true, ignoreCase: ignoreCase);
                    Assert.True((a.Matched, a.Position) == (b.Matched, b.Position), $"'{literal}' on '{input}' ignoreCase={ignoreCase}");
                }
        }
    }

    [Fact]
    public void The_edit_buffer_is_pooled_without_leaking_text_between_parses()
    {
        var rule = Dialect.Read("any [remove some digit | change \"x\" \"<>\" | skip]");
        var compiled = rule.Compiled();
        var rng = new Random(7);
        foreach (var length in new[] { 0, 1, 15, 16, 17, 100, 5000, 70_000, 1_200_000, 30, 3 })
        {
            var input = new string(Enumerable.Range(0, length).Select(_ => "ab12x"[rng.Next(5)]).ToArray());
            Assert.Equal(Parser.Parse(input, rule).Output, Parser.Parse(input, compiled).Output);
        }
        // a buffered rule under IsMatch rents and returns a buffer too, then refuses to edit
        var strip = Dialect.Read("any [remove digit | skip]").Compiled();
        foreach (var length in new[] { 5, 500, 50 })
            Assert.Throws<InvalidOperationException>(() => Parser.IsMatch(new string('1', length), strip));
        Assert.True(Parser.IsMatch("abc", strip));
    }

    [Fact]
    public void Rules_that_call_each_other_are_not_written_into_one_another_forever()
    {
        var table = Dialect.ReadGrammar("""
            a = "x" b | "y"
            b = "z" a | "w"
            self = "q" self | "r"
            """);
        var compiled = table.Compiled();
        foreach (var input in new[] { "y", "xw", "xzy", "xzxw", "xzxzy", "xz", "", "qqr", "qqqr", "q" })
            foreach (var start in new[] { "a", "b", "self" })
                Assert.Equal(Describe(Parser.Parse(input, Ref(start), table, partial: true)),
                             Describe(Parser.Parse(input, Ref(start), compiled, partial: true)));
    }

    [Fact]
    public void A_rule_too_big_to_write_into_another_is_still_called_correctly()
    {
        var big = Seq(Enumerable.Range(0, 80).Select(i => (Rule)(i % 2 == 0 ? "a" : "b")).ToArray());
        var table = new Dictionary<string, Rule> { ["big"] = big, ["small"] = Lit("c") };
        var compiled = table.Compiled();
        var rule = Seq(Ref("big"), Ref("small"), Ref("big"));
        var text = string.Concat(Enumerable.Repeat("ab", 40)) + "c" + string.Concat(Enumerable.Repeat("ab", 40));
        Assert.True(Parser.Parse(text, rule.Compiled(compiled), compiled).Matched);
        Assert.False(Parser.Parse(text + "x", rule.Compiled(compiled), compiled).Matched);
        // a long chain of small rules is written in only up to a budget, and the rest are called
        var chain = Enumerable.Range(0, 60).ToDictionary(i => $"r{i}", i => i == 59 ? (Rule)Lit("z") : Seq(Lit("a"), Ref($"r{i + 1}")));
        var chained = chain.Compiled();
        var long_ = new string('a', 59) + "z";
        Assert.True(Parser.Parse(long_, Ref("r0"), chained).Matched);
        Assert.False(Parser.Parse(long_[..^1], Ref("r0"), chained).Matched);
    }

    [Fact]
    public void Small_rules_written_into_a_caller_follow_the_table_in_use()
    {
        var hits = 0;
        var withNote = Dialect.Read("copy v item (@note) end", new Dictionary<string, Action<Ctx>> { ["note"] = _ => hits++ }).Compiled();
        var digits = new Dictionary<string, Rule> { ["item"] = Some(Digit) };
        var letters = new Dictionary<string, Rule> { ["item"] = Some(Alpha) };
        // the same compiled rule against two tables, several times over
        for (var round = 0; round < 3; round++)
        {
            Assert.True(Parser.Parse("123", withNote, digits.Compiled()).Matched);
            Assert.False(Parser.Parse("123", withNote, letters.Compiled()).Matched);
        }
        Assert.Equal(3, hits);
        var d = digits.Compiled();
        var l = letters.Compiled();
        for (var round = 0; round < 3; round++)
        {
            Assert.True(Parser.Parse("abc", withNote, l).Matched);
            Assert.False(Parser.Parse("abc", withNote, d).Matched);
        }
    }

    [Fact]
    public void Compiled_rules_call_interpreted_rules_that_are_too_awkward_to_write_in()
    {
        // recursive, so it is never written into the caller; and left uncompiled, so it runs on the interpreter
        var table = new Dictionary<string, Rule> { ["w"] = Some(Alpha) + Opt("-" + Ref("w")) };
        Assert.True(Parser.Parse("ab-cd", Ref("w").Compiled(), table).Matched);
        Assert.False(Parser.Parse("-", Ref("w").Compiled(), table).Matched);
        // and from code that reads the edit buffer
        var editing = Seq(Remove("x"), Ref("w")).Compiled();
        var ok = Parser.Parse("xab-cd", editing, table);
        Assert.True(ok.Matched);
        Assert.Equal("ab-cd", ok.Output);
        Assert.False(Parser.Parse("x-", editing, table).Matched);
    }

    [Fact]
    public void A_compiled_rule_nested_in_a_rule_that_calls_others_is_written_in_too()
    {
        var table = new Dictionary<string, Rule> { ["d"] = Some(Digit) }.Compiled();
        var rule = Seq(Ref("d"), Lit("b").Compiled(), Opt(Lit("c").Compiled())).Compiled(table);
        Assert.True(Parser.Parse("12bc", rule, table).Matched);
        Assert.True(Parser.Parse("12b", rule, table).Matched);
        Assert.False(Parser.Parse("12", rule, table).Matched);
    }

    [Fact]
    public void Writing_rules_into_callers_stays_within_a_budget_when_rules_call_each_other_many_times()
    {
        // each level calls the next one twice, so writing every call in would make 2^24 copies
        var table = Enumerable.Range(0, 24).ToDictionary(i => $"r{i}", i => i == 23 ? (Rule)Lit("a") : Seq(Ref($"r{i + 1}"), Ref($"r{i + 1}")));
        var compiled = table.Compiled();
        var text = new string('a', 1 << 10);
        Assert.False(Parser.Parse(text, Ref("r0"), compiled).Matched);   // 2^23 a's are needed; the point is that it returns
        var shallow = Enumerable.Range(0, 5).ToDictionary(i => $"r{i}", i => i == 4 ? (Rule)Lit("a") : Seq(Ref($"r{i + 1}"), Ref($"r{i + 1}"))).Compiled();
        Assert.True(Parser.Parse(new string('a', 16), Ref("r0"), shallow).Matched);
    }

    [Fact]
    public void A_rule_written_into_itself_agrees_at_every_nesting_depth()
    {
        var table = Dialect.ReadGrammar("""
            group = "(" any group ")"
            item  = some digit | list
            list  = "[" opt [item any ["," item]] "]"
            a     = "x" b | "y"
            b     = "z" a | "w"
            """);
        var compiled = table.Compiled();
        // balanced, unbalanced and almost balanced, from no nesting to well past the number of times a rule is written in
        var inputs = new List<string> { "", "(", ")", "()", ")(" };
        for (var depth = 1; depth <= 9; depth++)
        {
            var open = new string('(', depth);
            var close = new string(')', depth);
            inputs.Add(open + close);
            inputs.Add(open + close + "()");
            inputs.Add(open + close[1..]);
            inputs.Add(open[1..] + close);
            inputs.Add(open + "()" + close);
            inputs.Add(string.Concat(Enumerable.Repeat("[", depth)) + "7" + string.Concat(Enumerable.Repeat("]", depth)));
            inputs.Add(string.Concat(Enumerable.Repeat("[1,", depth)) + "[]" + string.Concat(Enumerable.Repeat("]", depth)));
            inputs.Add(string.Concat(Enumerable.Repeat("xz", depth)) + "y");
            inputs.Add(string.Concat(Enumerable.Repeat("xz", depth)) + "xw");
            inputs.Add(string.Concat(Enumerable.Repeat("xz", depth)) + "x");
        }
        foreach (var input in inputs)
            foreach (var start in new[] { "group", "list", "a", "b" })
                foreach (var partial in new[] { true, false })
                {
                    var x = Describe(Parser.Parse(input, Ref(start), table, partial));
                    var y = Describe(Parser.Parse(input, Ref(start), compiled, partial));
                    Assert.True(x == y, $"{start} on '{input}' partial={partial}\n interpreter {x}\n compiled    {y}");
                }
    }
}
