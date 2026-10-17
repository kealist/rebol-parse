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
/// <summary>Matches a single item satisfying a predicate (charsets, datatype tests).</summary>
public sealed record Item(Func<object, bool> Test, string Name = "item") : Rule;
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
