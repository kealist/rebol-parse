namespace RebolParse;

/// <summary>
/// Chainable forms of the fluent builders, so a rule reads in the order it matches:
/// <c>Digit.Some().Copy("n")</c> instead of <c>Copy("n", Some(Digit))</c>.
/// </summary>
public static class RuleExtensions
{
    /// <summary>One or more of this rule (<c>some</c>). Also available as the unary <c>+rule</c>.</summary>
    public static Rule Some(this Rule r) => P.Some(r);

    /// <summary>Zero or more of this rule (<c>any</c>).</summary>
    public static Rule Any(this Rule r) => P.Any(r);

    /// <summary>This rule, or nothing (<c>opt</c>).</summary>
    public static Rule Opt(this Rule r) => P.Opt(r);

    /// <summary>Exactly <paramref name="n"/> of this rule. Also available as <c>rule * n</c> and <c>n * rule</c>.</summary>
    public static Rule Times(this Rule r, int n) => P.Rep(n, r);

    /// <summary>Between <paramref name="min"/> and <paramref name="max"/> of this rule.</summary>
    public static Rule Times(this Rule r, int min, int max) => P.Rep(min, max, r);

    /// <summary>Succeeds, consuming nothing, if this rule does not match (<c>not</c>). Also available as <c>!rule</c>.</summary>
    public static Rule Not(this Rule r) => P.Not(r);

    /// <summary>Succeeds, consuming nothing, if this rule matches (<c>ahead</c>).</summary>
    public static Rule Ahead(this Rule r) => P.Ahead(r);

    /// <summary>Skips forward to where this rule matches, without consuming it (<c>to</c>).</summary>
    public static Rule To(this Rule r) => P.To(r);

    /// <summary>Skips forward to where this rule matches, and consumes it (<c>thru</c>).</summary>
    public static Rule Thru(this Rule r) => P.Thru(r);

    /// <summary>Captures what this rule matches into <c>Vars[name]</c> (<c>copy</c>).</summary>
    public static Rule Copy(this Rule r, string name) => P.Copy(name, r);

    /// <summary>Captures the first item this rule matches into <c>Vars[name]</c> (<c>set</c>).</summary>
    public static Rule Set(this Rule r, string name) => P.Set(name, r);

    /// <summary>Adds what this rule matches to the enclosing <c>collect</c> (<c>keep</c>).</summary>
    public static Rule Keep(this Rule r) => P.Keep(r);

    /// <summary>Matches this rule against the nested list at this position (<c>into</c>).</summary>
    public static Rule Into(this Rule r) => P.Into(r);

    /// <summary>Matches this rule, then runs <paramref name="action"/>.</summary>
    public static Rule Then(this Rule r, Action<Ctx> action) => r + P.Do(action);

    /// <summary>Replaces what this rule matches with <paramref name="value"/> (<c>change</c>).</summary>
    public static Rule ChangeTo(this Rule r, object? value) => P.Change(r, value);

    /// <summary>Deletes what this rule matches (<c>remove</c>).</summary>
    public static Rule Remove(this Rule r) => P.Remove(r);

    /// <summary>
    /// This rule compiled to IL, like <c>RegexOptions.Compiled</c>: it costs a few milliseconds the first time it runs and
    /// then matches faster. It gives the same results as the interpreter; a rule that uses <c>break</c>, <c>reject</c>,
    /// <c>collect</c>, <c>keep</c> or an edit simply runs on the interpreter. Rules that refer to others by name only get
    /// the full speed when the whole table is compiled with <see cref="Compiled(IReadOnlyDictionary{string, Rule})"/>.
    /// </summary>
    public static Rule Compiled(this Rule r) => r as CompiledRule ?? new CompiledRule(r);

    /// <summary>A copy of this rule table with every rule compiled to IL. Pass it as the <c>rules</c> argument of <c>Parse</c>.</summary>
    public static Dictionary<string, Rule> Compiled(this IReadOnlyDictionary<string, Rule> rules) =>
        rules.ToDictionary(kv => kv.Key, kv => kv.Value.Compiled());
}
