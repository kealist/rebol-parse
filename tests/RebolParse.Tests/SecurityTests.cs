using System.Buffers;

namespace RebolParse.Tests;

/// <summary>
/// Hostile rule text and hostile input must fail with an exception the caller can catch, never take the process
/// down with a stack overflow; shared rules must stay correct across threads; edited text must not outlive the parse.
/// </summary>
public class SecurityTests
{
    private const int Deep = 200_000;

    // ---- the reader ----

    [Fact]
    public void Deeply_nested_blocks_are_rejected_by_the_reader()
    {
        var text = new string('[', Deep) + "skip" + new string(']', Deep);
        Assert.Throws<FormatException>(() => Dialect.Read(text));
    }

    [Fact]
    public void A_long_chain_of_keywords_is_rejected_by_the_reader()
    {
        var text = string.Concat(Enumerable.Repeat("some ", Deep)) + "skip";
        Assert.Throws<FormatException>(() => Dialect.Read(text));
        Assert.Throws<FormatException>(() => Dialect.ReadGrammar("a = " + text));
    }

    [Fact]
    public void Nesting_up_to_the_limit_still_reads()
    {
        var depth = Dialect.MaxDepth - 1;
        var text = string.Concat(Enumerable.Repeat("[\"a\" ", depth)) + "\"a\"" + new string(']', depth);
        Assert.True(Parser.IsMatch(new string('a', depth + 1), Dialect.Read(text)));
    }

    // ---- matching ----

    private static readonly Dictionary<string, Rule> Groups = Dialect.ReadGrammar("g = \"(\" any g \")\"");
    private static readonly string DeepGroups = new string('(', Deep) + new string(')', Deep);

    [Fact]
    public void Deeply_nested_input_throws_instead_of_overflowing_the_stack()
    {
        Assert.Throws<InsufficientExecutionStackException>(() => Parser.IsMatch(DeepGroups, new Ref("g"), Groups));
        Assert.Throws<InsufficientExecutionStackException>(() => Parser.Parse(DeepGroups, new Ref("g"), Groups));
        Assert.True(Parser.IsMatch("(()(()))", new Ref("g"), Groups));
    }

    [Fact]
    public void Deeply_nested_input_throws_in_compiled_rules_too()
    {
        var compiled = Groups.ToDictionary(d => d.Key, d => (Rule)d.Value.Compiled());
        Assert.Throws<InsufficientExecutionStackException>(() => Parser.IsMatch(DeepGroups, new Ref("g"), compiled));
        Assert.True(Parser.IsMatch("(()(()))", new Ref("g"), compiled));
    }

    [Fact]
    public void Left_recursion_throws_instead_of_overflowing_the_stack()
    {
        var grammar = Dialect.ReadGrammar("a = a \"x\"");
        Assert.Throws<InsufficientExecutionStackException>(() => Parser.IsMatch("x", new Ref("a"), grammar));
        var compiled = grammar.ToDictionary(d => d.Key, d => (Rule)d.Value.Compiled());
        Assert.Throws<InsufficientExecutionStackException>(() => Parser.IsMatch("x", new Ref("a"), compiled));
        Assert.Throws<InsufficientExecutionStackException>(() => Parser.IsMatch(new List<object> { 'x' }, new Ref("a"), grammar));
    }

    [Fact]
    public void Deeply_nested_blocks_throw_instead_of_overflowing_the_stack()
    {
        object block = new List<object>();
        for (var i = 0; i < Deep; i++) block = new List<object> { block };
        var grammar = Dialect.ReadGrammar("g = into [any g]");
        Assert.Throws<InsufficientExecutionStackException>(() => Parser.IsMatch(new List<object> { block }, new Ref("g"), grammar));
    }

    // ---- shared rules ----

    [Fact]
    public void A_rule_shared_by_threads_with_different_tables_uses_each_threads_table()
    {
        var x = new Dictionary<string, Rule> { ["a"] = new Lit("x") };
        var y = new Dictionary<string, Rule> { ["a"] = new Lit("y") };
        var shared = new Seq([new Lit("<"), new Ref("a")]);
        var wrong = 0;
        Parallel.For(0, 8, w =>
        {
            var (table, input) = w % 2 == 0 ? (x, "<x") : (y, "<y");
            for (var i = 0; i < 200_000; i++)
                if (!Parser.IsMatch(input, shared, table)) Interlocked.Increment(ref wrong);
        });
        Assert.Equal(0, wrong);
    }

    // ---- the step limit ----

    private static IEnumerable<Rule> Engines(Rule rule) => [rule, rule.Compiled()];

    [Fact]
    public void A_loop_that_seeks_back_and_forth_stops_at_the_default_limit()
    {
        // pure, no edits: from 0 the body seeks to 1, from 1 back to 0, forever
        var rule = Dialect.Read("d: skip c: :d any [\"x\" :c | \"y\" :d]");
        foreach (var r in Engines(rule))
            Assert.Throws<StepLimitExceededException>(() => Parser.IsMatch("xy", r, partial: true));
        var block = Dialect.Read("d: skip c: :d any [#\"x\" :c | #\"y\" :d]");
        Assert.Throws<StepLimitExceededException>(() => Parser.IsMatch(new List<object> { 'x', 'y' }, block, partial: true));
    }

    [Fact]
    public void A_loop_that_grows_the_text_stops_at_the_limit()
    {
        var rule = Dialect.Read("m: any [insert \"x\" :m]");
        foreach (var r in Engines(rule))
            Assert.Throws<StepLimitExceededException>(() => Parser.Parse("a", r, partial: true, maxSteps: 100_000));
        Assert.Throws<StepLimitExceededException>(() => Parser.Parse(new List<object> { 1 }, rule, partial: true, maxSteps: 100_000));
    }

    [Fact]
    public void Exponential_backtracking_stops_at_the_limit()
    {
        var grammar = Dialect.ReadGrammar("a = \"x\" a \"y\" | \"x\" a | \"x\"");
        var compiled = grammar.ToDictionary(d => d.Key, d => (Rule)d.Value.Compiled());
        var input = new string('x', 40) + "!";
        Assert.Throws<StepLimitExceededException>(() => Parser.IsMatch(input, new Ref("a"), grammar));
        Assert.Throws<StepLimitExceededException>(() => Parser.IsMatch(input, new Ref("a"), compiled));
    }

    [Fact]
    public void Scans_count_each_position_they_try()
    {
        var rule = Dialect.Read("to [\"a\" \"b\"]");
        var input = new string('a', 1000) + "b";
        foreach (var r in Engines(rule))
        {
            Assert.Throws<StepLimitExceededException>(() => Parser.IsMatch(input, r, partial: true, maxSteps: 100));
            Assert.True(Parser.IsMatch(input, r, partial: true, maxSteps: 10_000));
        }
        Assert.Throws<StepLimitExceededException>(() =>
            Parser.IsMatch(input.Select(c => (object)c).ToList(), Dialect.Read("to [#\"a\" #\"b\"]"), partial: true, maxSteps: 100));
    }

    [Fact]
    public void The_limit_can_be_raised_turned_off_or_rejected()
    {
        var rule = Dialect.Read("some [\"a\" | \"b\"]");
        var input = new string('a', 5000);
        var e = Assert.Throws<StepLimitExceededException>(() => Parser.Parse(input, rule, maxSteps: 10));
        Assert.Equal(10, e.Limit);
        Assert.True(Parser.Parse(input, rule, maxSteps: Parser.NoStepLimit).Matched);
        Assert.Throws<ArgumentOutOfRangeException>(() => Parser.IsMatch(input, rule, maxSteps: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Parser.Parse(new List<object>(), rule, maxSteps: -1));
    }

    [Fact]
    public void The_default_limit_scales_with_the_input()
    {
        Assert.Equal(10_000_000, Parser.DefaultMaxSteps(0));
        Assert.Equal(1_000_000_000, Parser.DefaultMaxSteps(1_000_000));
    }

    [Fact]
    public void The_0_2_signatures_still_work()
    {
        var rule = Dialect.Read("some digit");
        Assert.True(Parser.Parse("123", rule, null, false, false).Matched);
        Assert.True(Parser.Parse(new List<object> { '1' }, rule, null, false, false).Matched);
        Assert.True(Parser.IsMatch("123", rule, null, false, false));
        Assert.True(Parser.IsMatch(new List<object> { '1' }, rule, null, false, false));
    }

    [Fact]
    public void A_parse_stopped_by_the_limit_gives_its_edit_buffer_back_cleared()
    {
        const string secret = "token=hunter2-very-secret-value-xx";
        Assert.Throws<StepLimitExceededException>(() =>
            Parser.Parse(secret, Dialect.Read("remove \"token\" m: any [insert \"x\" :m]"), maxSteps: 1000));
        var rented = ArrayPool<char>.Shared.Rent(secret.Length);
        try { Assert.DoesNotContain("hunter2", new string(rented)); }
        finally { ArrayPool<char>.Shared.Return(rented); }
    }

    // ---- edited text ----

    [Fact]
    public void Edited_text_is_not_left_in_the_shared_array_pool()
    {
        const string secret = "password=hunter2-very-secret-value";
        Parser.Parse(secret, Dialect.Read("remove \"password\" to end"));
        var rented = ArrayPool<char>.Shared.Rent(secret.Length);   // the same bucket, on the same thread
        try { Assert.DoesNotContain("hunter2", new string(rented)); }
        finally { ArrayPool<char>.Shared.Return(rented); }
    }
}
