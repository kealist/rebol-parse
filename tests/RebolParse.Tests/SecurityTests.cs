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
