namespace RebolParse;

/// <summary>
/// A node in a parse rule tree. Build trees with <see cref="P"/> and the operators on this type,
/// or read them from REBOL-style text with <see cref="Dialect"/>. Rules are immutable and can be shared
/// between threads; build one once and reuse it, because the first parse with a rule compiles it.
/// </summary>
public abstract record Rule
{
    /// <summary>Sequence: <c>a + b</c> matches <c>a</c>, then <c>b</c>. At least one operand must already be a <see cref="Rule"/>, since <c>"a" + "b"</c> is just string concatenation.</summary>
    public static Rule operator +(Rule a, Rule b) => new Seq(Flatten<Seq>(a, b, x => x.Items));

    /// <summary>Ordered choice: <c>a | b</c> tries <c>a</c>, then <c>b</c> if <c>a</c> fails. The first alternative that matches wins; later ones are not tried unless something after the choice fails and backtracks into it.</summary>
    public static Rule operator |(Rule a, Rule b) => new Alt(Flatten<Alt>(a, b, x => x.Items));

    /// <summary>A character becomes a literal rule matching that character.</summary>
    public static implicit operator Rule(char c) => new Lit(c);

    /// <summary>A string becomes a literal rule matching that exact text.</summary>
    public static implicit operator Rule(string s) => new Lit(s);

    private static Rule[] Flatten<T>(Rule a, Rule b, Func<T, Rule[]> items) where T : Rule
    {
        var list = new List<Rule>();
        foreach (var r in new[] { a, b })
            if (r is T t) list.AddRange(items(t)); else list.Add(r);
        return [.. list];
    }
}

/// <summary>Matches a literal: a <see cref="string"/> or <see cref="char"/> in text; any equal item in a block.</summary>
/// <param name="Value">The value to match.</param>
public sealed record Lit(object Value) : Rule;

/// <summary>Matches each item in order. An empty sequence matches without consuming anything (<c>none</c>).</summary>
/// <param name="Items">The rules to match, in order.</param>
public sealed record Seq(Rule[] Items) : Rule;

/// <summary>Ordered choice: the first alternative that matches wins.</summary>
/// <param name="Items">The alternatives, in order of preference.</param>
public sealed record Alt(Rule[] Items) : Rule;

/// <summary>Matches any one item (<c>skip</c>). Fails at the end of the input.</summary>
public sealed record SkipRule : Rule;

/// <summary>Matches only at the end of the input (<c>end</c>), consuming nothing.</summary>
public sealed record EndRule : Rule;

/// <summary>Never matches (<c>fail</c>).</summary>
public sealed record FailRule : Rule;

/// <summary>Leaves the enclosing <c>some</c>/<c>any</c>/<c>opt</c>/counted loop, successfully (<c>break</c>).</summary>
public sealed record BreakRule : Rule;

/// <summary>Leaves the enclosing <c>some</c>/<c>any</c>/<c>opt</c>/counted loop, failing it (<c>reject</c>). Outside a loop it is plain failure.</summary>
public sealed record RejectRule : Rule;

/// <summary>
/// Matches a single item satisfying a predicate: character classes in text, datatype tests in blocks.
/// </summary>
/// <param name="Test">The predicate, applied to the item (a boxed <see cref="char"/> in text).</param>
/// <param name="Name">A name for diagnostics, such as <c>digit</c> or <c>integer!</c>.</param>
/// <param name="CharTest">When set, text input is tested through this directly, so no character is boxed. <see cref="P.CharIf"/> sets it.</param>
public sealed record Item(Func<object, bool> Test, string Name = "item", Func<char, bool>? CharTest = null) : Rule;

/// <summary>Repeats a rule: <c>some</c> is 1 to unbounded, <c>any</c> is 0 to unbounded, <c>opt</c> is 0 to 1, <c>3 r</c> is exactly 3. A loop stops when an iteration consumes nothing.</summary>
/// <param name="Body">The rule to repeat.</param>
/// <param name="Min">The fewest iterations that count as a match.</param>
/// <param name="Max">The most iterations to run; -1 for unbounded.</param>
public sealed record Repeat(Rule Body, int Min, int Max) : Rule;

/// <summary>Succeeds if <paramref name="Body"/> matches, without consuming input (<c>ahead</c>).</summary>
/// <param name="Body">The rule to look for.</param>
public sealed record Ahead(Rule Body) : Rule;

/// <summary>Succeeds if <paramref name="Body"/> does not match, without consuming input (<c>not</c>).</summary>
/// <param name="Body">The rule that must not match.</param>
public sealed record Not(Rule Body) : Rule;

/// <summary>Skips forward to the first position where <paramref name="Body"/> matches, without consuming the match (<c>to</c>). Scans across newlines and everything else.</summary>
/// <param name="Body">The rule to scan for.</param>
public sealed record To(Rule Body) : Rule;

/// <summary>Skips forward to the first position where <paramref name="Body"/> matches, and consumes the match (<c>thru</c>).</summary>
/// <param name="Body">The rule to scan for.</param>
public sealed record Thru(Rule Body) : Rule;

/// <summary>Matches <paramref name="Body"/> and stores the matched input in <see cref="Ctx.Vars"/> (<c>copy</c>): a string in text, a list in a block.</summary>
/// <param name="Var">The variable name.</param>
/// <param name="Body">The rule whose match is captured.</param>
public sealed record Copy(string Var, Rule Body) : Rule;

/// <summary>Matches <paramref name="Body"/> and stores its first matched item in <see cref="Ctx.Vars"/>, or <c>null</c> if it matched nothing (<c>set</c>).</summary>
/// <param name="Var">The variable name.</param>
/// <param name="Body">The rule whose first item is captured.</param>
public sealed record Set(string Var, Rule Body) : Rule;

/// <summary>Descends into a nested block (<c>into</c>): the current item must be a list, and <paramref name="Body"/> must match all of it. Block input only.</summary>
/// <param name="Body">The rule to match against the nested list.</param>
public sealed record Into(Rule Body) : Rule;

/// <summary>Refers to a named rule in the rule table passed to <see cref="Parser.Parse(string, Rule, IReadOnlyDictionary{string, Rule}?, bool, bool)"/>. This is how rules recurse.</summary>
/// <param name="Name">The rule's name in the table.</param>
public sealed record Ref(string Name) : Rule;

/// <summary>Saves the current position under a name (<c>name:</c>).</summary>
/// <param name="Name">The mark's name.</param>
public sealed record Mark(string Name) : Rule;

/// <summary>Jumps back (or forward) to a saved position (<c>:name</c>).</summary>
/// <param name="Name">The mark's name; it must have been set earlier in the parse.</param>
public sealed record Seek(string Name) : Rule;

/// <summary>Runs a C# action at this point in the match, consuming nothing. The text form is <c>(@name)</c>.</summary>
/// <param name="Run">The action; <see cref="Ctx.Pos"/> is the current position and <see cref="Ctx.Vars"/> holds the captures so far.</param>
public sealed record Act(Action<Ctx> Run) : Rule;

/// <summary>Inserts a value at the current position and moves past it (<c>insert</c>). Not allowed in <see cref="Parser.IsMatch(string, Rule, IReadOnlyDictionary{string, Rule}?, bool, bool)"/>.</summary>
/// <param name="Value">Computes the value to insert: text for a string parse, one item for a block.</param>
public sealed record Insert(Func<Ctx, object?> Value) : Rule;

/// <summary>Matches <paramref name="Body"/>, replaces the matched input with a value, and moves past it (<c>change</c>).</summary>
/// <param name="Body">The rule whose match is replaced.</param>
/// <param name="Value">Computes the replacement.</param>
public sealed record Change(Rule Body, Func<Ctx, object?> Value) : Rule;

/// <summary>Matches <paramref name="Body"/> and deletes the matched input (<c>remove</c>).</summary>
/// <param name="Body">The rule whose match is deleted.</param>
public sealed record Remove(Rule Body) : Rule;

/// <summary>Runs <paramref name="Body"/> and stores everything it <see cref="Keep"/>s as a list in <paramref name="Var"/> (<c>collect</c>).</summary>
/// <param name="Var">The variable that receives the list.</param>
/// <param name="Body">The rule that contains the <c>keep</c>s.</param>
public sealed record Collect(string Var, Rule Body) : Rule;

/// <summary>Matches <paramref name="Body"/> and appends the matched input to the enclosing <see cref="Collect"/> (<c>keep</c>).</summary>
/// <param name="Body">The rule whose match is kept.</param>
public sealed record Keep(Rule Body) : Rule;

/// <summary>Matches <paramref name="Body"/> and appends only the first matched item to the enclosing <see cref="Collect"/>; nothing if the match is empty (<c>keep pick</c>).</summary>
/// <param name="Body">The rule whose first item is kept.</param>
public sealed record KeepPick(Rule Body) : Rule;

/// <summary>Appends a computed value to the enclosing <see cref="Collect"/> without consuming input (<c>keep (@fn)</c>). A <c>null</c> result keeps nothing.</summary>
/// <param name="Value">Computes the value to keep.</param>
public sealed record KeepValue(Func<Ctx, object?> Value) : Rule;

/// <summary>Succeeds, consuming nothing, when the condition holds (<c>if (@fn)</c>).</summary>
/// <param name="Condition">The condition; <see cref="Ctx.Vars"/> holds the captures so far.</param>
public sealed record If(Func<Ctx, bool> Condition) : Rule;
