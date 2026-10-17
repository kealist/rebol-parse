namespace RebolParse;

/// <summary>
/// A node in a parse rule tree. Build trees with <see cref="P"/> and the operators on this type,
/// or read them from REBOL-style text with <see cref="Dialect"/>.
/// </summary>
public abstract record Rule
{
    /// <summary>Sequence: <c>a + b</c> matches a then b.</summary>
    public static Rule operator +(Rule a, Rule b) => new Seq(Flatten<Seq>(a, b, x => x.Items));

    /// <summary>Ordered choice: <c>a | b</c> tries a, then b if a fails.</summary>
    public static Rule operator |(Rule a, Rule b) => new Alt(Flatten<Alt>(a, b, x => x.Items));

    public static implicit operator Rule(char c) => new Lit(c);
    public static implicit operator Rule(string s) => new Lit(s);

    private static Rule[] Flatten<T>(Rule a, Rule b, Func<T, Rule[]> items) where T : Rule
    {
        var list = new List<Rule>();
        foreach (var r in new[] { a, b })
            if (r is T t) list.AddRange(items(t)); else list.Add(r);
        return [.. list];
    }
}

public sealed record Lit(object Value) : Rule;
public sealed record Seq(Rule[] Items) : Rule;
public sealed record Alt(Rule[] Items) : Rule;
public sealed record SkipRule : Rule;
public sealed record EndRule : Rule;
public sealed record FailRule : Rule;
public sealed record BreakRule : Rule;
public sealed record RejectRule : Rule;
/// <summary>
/// Matches a single item satisfying a predicate (charsets, datatype tests). When <paramref name="CharTest"/> is set,
/// string input is tested through it directly, so no character is boxed.
/// </summary>
public sealed record Item(Func<object, bool> Test, string Name = "item", Func<char, bool>? CharTest = null) : Rule;
/// <summary>Repeats <paramref name="Body"/> between Min and Max times (Max = -1 for unbounded).</summary>
public sealed record Repeat(Rule Body, int Min, int Max) : Rule;
public sealed record Ahead(Rule Body) : Rule;
public sealed record Not(Rule Body) : Rule;
public sealed record To(Rule Body) : Rule;
public sealed record Thru(Rule Body) : Rule;
public sealed record Copy(string Var, Rule Body) : Rule;
public sealed record Set(string Var, Rule Body) : Rule;
public sealed record Into(Rule Body) : Rule;
public sealed record Ref(string Name) : Rule;
public sealed record Mark(string Name) : Rule;
public sealed record Seek(string Name) : Rule;
public sealed record Act(Action<Ctx> Run) : Rule;
/// <summary>Inserts a value at the current position and moves past it.</summary>
public sealed record Insert(Func<Ctx, object?> Value) : Rule;
/// <summary>Matches <paramref name="Body"/>, replaces the matched input with a value, and moves past it.</summary>
public sealed record Change(Rule Body, Func<Ctx, object?> Value) : Rule;
/// <summary>Matches <paramref name="Body"/> and deletes the matched input.</summary>
public sealed record Remove(Rule Body) : Rule;
/// <summary>Runs <paramref name="Body"/> and stores the values it <see cref="Keep"/>s as a list in <paramref name="Var"/>.</summary>
public sealed record Collect(string Var, Rule Body) : Rule;
/// <summary>Matches <paramref name="Body"/> and appends the matched input to the enclosing <see cref="Collect"/>.</summary>
public sealed record Keep(Rule Body) : Rule;
/// <summary>Matches <paramref name="Body"/> and appends only the first matched item to the enclosing <see cref="Collect"/> (nothing if the match is empty).</summary>
public sealed record KeepPick(Rule Body) : Rule;
/// <summary>Appends a computed value to the enclosing <see cref="Collect"/> without consuming input. A null result keeps nothing.</summary>
public sealed record KeepValue(Func<Ctx, object?> Value) : Rule;
/// <summary>Succeeds, consuming nothing, when the condition holds.</summary>
public sealed record If(Func<Ctx, bool> Condition) : Rule;
