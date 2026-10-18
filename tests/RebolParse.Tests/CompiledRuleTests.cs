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
    };

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
                9 => Ref(new[] { "dig", "ab", "word", "cs" }[rng.Next(4)]),
                10 => Lit(""),
                11 => Lit(7),
                12 => Chars("a-b"),
                _ => Is(o => o is char ch && ch == 'b'),
            };
        var d = depth - 1;
        return rng.Next(16) switch
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
            _ => Choice(Seq(Gen(rng, d), Fail), Gen(rng, d)),
        };
    }

    private static string Describe(ParseResult r) =>
        $"{r.Matched} @{r.Position} v={r.Vars.GetValueOrDefault("v")} w={r.Vars.GetValueOrDefault("w")}";

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
            var compiled = rule.Compiled();
            if (((CompiledRule)compiled).IsNative) native++;
            var len = rng.Next(0, 10);
            var input = new string(Enumerable.Range(0, len).Select(_ => "abcAB-1x"[rng.Next(8)]).ToArray());
            var partial = rng.Next(2) == 0;
            var ignoreCase = rng.Next(2) == 0;
            string a, b;
            try { a = Describe(Parser.Parse(input, rule, Table, partial, ignoreCase)); } catch (Exception ex) { a = ex.GetType().Name; }
            try { b = Describe(Parser.Parse(input, compiled, compiledTable, partial, ignoreCase)); } catch (Exception ex) { b = ex.GetType().Name; }
            Assert.True(a == b, $"input '{input}' partial={partial} ignoreCase={ignoreCase}\n rule {rule}\n interpreter {a}\n compiled    {b}");
            Assert.Equal(Parser.IsMatch(input, rule, Table, partial, ignoreCase),
                         Parser.IsMatch(input, compiled, compiledTable, partial, ignoreCase));
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

    [Theory]
    [InlineData("some [\"a\" break | \"b\"]")]
    [InlineData("collect out some [keep alpha]")]
    [InlineData("any [remove digit | skip]")]
    [InlineData("any [change digit \"#\" | skip]")]
    [InlineData("insert \"x\" to end")]
    [InlineData("any [\"a\" reject | skip]")]
    [InlineData("collect out [keep pick skip] to end")]
    [InlineData("collect out [keep (@one)] to end")]
    public void Rules_with_unsupported_parts_fall_back_and_agree(string text)
    {
        var values = new Dictionary<string, Func<Ctx, object?>> { ["one"] = _ => 1 };
        var rule = Dialect.Read(text, null, values);
        var compiled = rule.Compiled();
        Assert.False(((CompiledRule)compiled).IsNative);
        foreach (var input in new[] { "", "ab", "a1b2", "aaab", "123" })
        {
            var x = Parser.Parse(input, rule, partial: true);
            var y = Parser.Parse(input, compiled, partial: true);
            Assert.Equal((x.Matched, x.Position, x.Output), (y.Matched, y.Position, y.Output));
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
    public void A_compiled_rule_reached_after_an_edit_is_refused()
    {
        var rule = Seq(Remove("a"), Ref("c"));
        var rules = new Dictionary<string, Rule> { ["c"] = Lit("b").Compiled() };
        Assert.Throws<InvalidOperationException>(() => Parser.Parse("ab", rule, rules));
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
}
