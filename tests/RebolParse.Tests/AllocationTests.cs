using static RebolParse.P;

namespace RebolParse.Tests;

public class AllocationTests
{
    /// <summary>Bytes allocated per call on this thread, after warm-up so one-time pooling and JIT are excluded.</summary>
    private static long BytesPerCall(Action call)
    {
        for (var i = 0; i < 300; i++) call();
        const int runs = 1000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < runs; i++) call();
        return (GC.GetAllocatedBytesForCurrentThread() - before) / runs;
    }

    private static Rule R(string text, IReadOnlyDictionary<string, Func<Ctx, object?>>? values = null,
        IReadOnlyDictionary<string, Action<Ctx>>? acts = null) => Dialect.Read(text, acts, values);

    [Theory]
    [InlineData("555-1234", "3 digit \"-\" 4 digit")]
    [InlineData("1,22,333,4444", "some digit any [\",\" some digit]")]
    [InlineData("abc", "\"x\" | \"y\" | some alpha")]
    [InlineData("key=value", "to \"=\" \"=\" to end")]
    [InlineData("key=value", "thru \"=\" some alpha")]
    [InlineData("ab", "not \"x\" ahead \"a\" skip skip")]
    [InlineData("abcabc", "3 skip :m")]
    [InlineData("abc", "m: \"abc\" :m \"abc\"")]
    [InlineData("HeLLo", "\"hello\"")]
    [InlineData("a b", "some alpha space some alpha")]
    [InlineData("aab", "any [\"b\" break | \"a\"] \"b\"")]
    [InlineData("abq", "some [\"q\" reject | alpha]")]
    public void IsMatch_allocates_nothing_for_text_rules(string input, string rule)
    {
        var r = R(rule.Replace(" :m", ""));
        Assert.Equal(0, BytesPerCall(() => Parser.IsMatch(input, r, partial: true, ignoreCase: true)));
    }

    [Fact]
    public void IsMatch_allocates_nothing_for_fluent_charsets_and_recursion()
    {
        var rule = Some(CharIn("+-*/") | CharRange('0', '9') | Is(_ => false, "never")) + End;
        Assert.Equal(0, BytesPerCall(() => Parser.IsMatch("12+3*4", rule)));

        var rules = Dialect.ReadRules(new Dictionary<string, string> { ["group"] = "\"(\" any group \")\"" });
        var group = Ref("group");
        Assert.True(Parser.IsMatch("(()(()))", group, rules));
        Assert.Equal(0, BytesPerCall(() => Parser.IsMatch("(()(()))", group, rules)));
    }

    [Fact]
    public void IsMatch_allocates_nothing_for_block_input_including_nested_into()
    {
        var input = new List<object> { new Word("add"), 1, new List<object> { 2, new List<object> { 3 } }, "x" };
        var rule = R("quote add integer! into [integer! into [integer!]] string!");
        Assert.True(Parser.IsMatch(input, rule));
        Assert.Equal(0, BytesPerCall(() => Parser.IsMatch(input, rule)));
    }

    [Fact]
    public void IsMatch_allocates_nothing_when_actions_and_conditions_run()
    {
        var total = 0;
        var acts = new Dictionary<string, Action<Ctx>> { ["tick"] = c => total += c.Pos };
        object yes = true; // boxed once here: a function that boxes a fresh bool per call would allocate, but that is the caller's cost
        var values = new Dictionary<string, Func<Ctx, object?>> { ["yes"] = _ => yes };
        var rule = R("some [alpha (@tick)] if (@yes)", values, acts);
        Assert.Equal(0, BytesPerCall(() => Parser.IsMatch("abc", rule)));
        Assert.True(total > 0);
    }

    [Fact]
    public void Parse_does_not_copy_input_it_does_not_edit()
    {
        var input = new string('a', 100_000);
        var rule = R("some \"a\"");
        var result = Parser.Parse(input, rule);
        Assert.True(result.Matched);
        Assert.Same(input, result.Output); // no copy, no new string

        var before = GC.GetAllocatedBytesForCurrentThread();
        Parser.Parse(input, rule);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 2_000, $"allocated {allocated} bytes for a 100 KB input");

        var items = new List<object>(Enumerable.Repeat<object>(1, 10_000));
        Assert.Same(items, Parser.Parse(items, R("some quote 1")).Output);
    }

    // ---- behaviour of the pooled, read-only path ----

    [Theory, MemberData(nameof(ParseTests.Cases), MemberType = typeof(ParseTests))]
    public void IsMatch_agrees_with_Parse(string input, Rule fluent, string text, bool expected)
    {
        Assert.Equal(expected, Parser.IsMatch(input, fluent));
        Assert.Equal(expected, Parser.IsMatch(input, Dialect.Read(text)));
    }

    [Fact]
    public void IsMatch_honours_partial_and_ignoreCase_and_rule_tables()
    {
        Assert.False(Parser.IsMatch("abc", (Rule)"a"));
        Assert.True(Parser.IsMatch("abc", (Rule)"a", partial: true));
        Assert.True(Parser.IsMatch("ABC", (Rule)"abc", ignoreCase: true));
        Assert.True(Parser.IsMatch("7", Ref("d"), new Dictionary<string, Rule> { ["d"] = Digit }));
        Assert.True(Parser.IsMatch(new List<object> { "A" }, (Rule)"a", ignoreCase: true));
        Assert.False(Parser.IsMatch(new List<object> { 1, 2 }, Integer, partial: false));
        Assert.True(Parser.IsMatch(new List<object> { 1, 2 }, Integer, partial: true));
    }

    [Fact]
    public void IsMatch_does_not_leak_state_between_calls()
    {
        Assert.True(Parser.IsMatch("ab", Dialect.Read("m: \"ab\"")));
        Assert.Throws<KeyNotFoundException>(() => Parser.IsMatch("ab", Seek("m")));
        Assert.True(Parser.IsMatch("ab", Dialect.Read("copy x \"ab\"")));
        Assert.Throws<KeyNotFoundException>(() => Parser.IsMatch("ab", Dialect.Read("if (@nope)")));
        // a break left over from one call must not cut the next call's loop short
        Assert.True(Parser.IsMatch("a", Dialect.Read("break"), partial: true));
        Assert.True(Parser.IsMatch("aaa", Some((Rule)"a")));
        // a keep stack left by an exception must not leak either
        Assert.Throws<InvalidOperationException>(() => Parser.IsMatch("a", Dialect.Read("keep \"a\"")));
        Assert.True(Parser.IsMatch("a", Dialect.Read("collect x keep \"a\"")));
    }

    [Fact]
    public void IsMatch_recovers_after_an_exception_inside_into()
    {
        var bad = new List<object> { new List<object> { 1 } };
        Assert.Throws<KeyNotFoundException>(() => Parser.IsMatch(bad, Dialect.Read("into [nope]")));
        Assert.True(Parser.IsMatch(bad, Dialect.Read("into [integer!]")));
    }

    [Fact]
    public void IsMatch_is_reentrant_from_an_action()
    {
        var inner = false;
        var acts = new Dictionary<string, Action<Ctx>> { ["nested"] = _ => inner = Parser.IsMatch("xyz", Dialect.Read("some alpha")) };
        Assert.True(Parser.IsMatch("ab", Dialect.Read("\"a\" (@nested) \"b\"", acts)));
        Assert.True(inner);
        Assert.True(Parser.IsMatch("ab", Dialect.Read("\"a\" \"b\"")));
    }

    [Fact]
    public void IsMatch_is_thread_safe()
    {
        var rule = Dialect.Read("some digit any [\",\" some digit]");
        var failures = 0;
        Parallel.For(0, 8, _ =>
        {
            for (var i = 0; i < 2_000; i++)
                if (!Parser.IsMatch("1,22,333", rule) || Parser.IsMatch("1,,2", rule)) Interlocked.Increment(ref failures);
        });
        Assert.Equal(0, failures);
    }

    [Theory]
    [InlineData("insert \"x\"")]
    [InlineData("insert \"\"")]
    [InlineData("change skip \"x\"")]
    [InlineData("remove skip")]
    [InlineData("remove none")]
    public void IsMatch_rejects_rules_that_edit_text(string rule)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Parser.IsMatch("ab", Dialect.Read(rule)));
        Assert.Contains("read-only", ex.Message);
    }

    [Fact]
    public void IsMatch_rejects_rules_that_edit_blocks()
    {
        foreach (var rule in new[] { "insert 1", "remove integer!", "remove none", "change integer! 2" })
            Assert.Throws<InvalidOperationException>(() => Parser.IsMatch(new List<object> { 1 }, Dialect.Read(rule)));
        Assert.Throws<InvalidOperationException>(() =>
            Parser.IsMatch(new List<object> { new List<object> { 1 } }, Dialect.Read("into [remove integer!]")));
        Assert.Throws<InvalidOperationException>(() => Parser.IsMatch(new List<object> { 1 }, Insert((object?)null)));
    }

    [Fact]
    public void Series_reuse_after_an_edit_starts_clean()
    {
        var b = new BlockSeries(new List<object> { 1 });
        b.Insert(0, 0);
        b.Reset(new List<object> { 7 }, false, false);
        Assert.False(b.Modified);
        b.Insert(1, 8);
        Assert.Equal(new object[] { 7, 8 }, Assert.IsAssignableFrom<IReadOnlyList<object>>(b.ToResult()));
    }

    [Fact]
    public void Character_rules_still_work_on_blocks_that_hold_chars()
    {
        Assert.True(Parser.Parse(new List<object> { 'a', 'b' }, Some(Alpha)).Matched);
        Assert.False(Parser.Parse(new List<object> { 'a', 1 }, Some(Alpha)).Matched);
        Assert.True(Parser.IsMatch(new List<object> { '1' }, Digit));
    }

    [Fact]
    public void Edited_result_is_a_copy_the_series_can_not_change_later()
    {
        var b = new BlockSeries(new List<object> { 1 });
        b.Insert(0, 0);
        var snapshot = Assert.IsAssignableFrom<IReadOnlyList<object>>(b.ToResult());
        b.Insert(0, -1);
        Assert.Equal(2, snapshot.Count);
    }
}
