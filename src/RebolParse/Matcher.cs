namespace RebolParse;

/// <summary>State shared across a whole parse (including nested <c>into</c> blocks).</summary>
public sealed class Ctx
{
    public Dictionary<string, object?> Vars { get; } = [];
    public Dictionary<string, int> Marks { get; } = [];
    public IReadOnlyDictionary<string, Rule> Rules { get; init; } = new Dictionary<string, Rule>();

    /// <summary>Current position in the series being matched when an action runs.</summary>
    public int Pos { get; internal set; }
    internal bool BreakRequested;
}

public sealed record ParseResult(bool Matched, int Position, IReadOnlyDictionary<string, object?> Vars)
{
    public static implicit operator bool(ParseResult r) => r.Matched;
}

public static class Parser
{
    /// <summary>Parse a string. By default the rule must consume all input, as in REBOL.</summary>
    public static ParseResult Parse(string input, Rule rule, IReadOnlyDictionary<string, Rule>? rules = null, bool partial = false) =>
        Run(new StringSeries(input), rule, rules, partial);

    /// <summary>Parse a block of values (chars, strings, numbers, <see cref="Word"/>, nested lists).</summary>
    public static ParseResult Parse(IReadOnlyList<object> input, Rule rule, IReadOnlyDictionary<string, Rule>? rules = null, bool partial = false) =>
        Run(new BlockSeries(input), rule, rules, partial);

    private static ParseResult Run(ISeries series, Rule rule, IReadOnlyDictionary<string, Rule>? rules, bool partial)
    {
        var ctx = new Ctx { Rules = rules ?? new Dictionary<string, Rule>() };
        var pos = 0;
        var ok = new Matcher(series, ctx).Match(rule, ref pos);
        return new ParseResult(ok && (partial || pos == series.Length), pos, ctx.Vars);
    }
}

internal sealed class Matcher(ISeries s, Ctx ctx)
{
    /// <summary>Match <paramref name="rule"/> at <paramref name="pos"/>. On failure pos is left unchanged.</summary>
    public bool Match(Rule rule, ref int pos)
    {
        var start = pos;
        var wasBreaking = ctx.BreakRequested;
        if (Step(rule, ref pos)) return true;
        pos = start;
        ctx.BreakRequested = wasBreaking; // a break inside a failed branch is abandoned with it
        return false;
    }

    private bool Step(Rule rule, ref int pos)
    {
        switch (rule)
        {
            case Lit l:
                var n = s.MatchLiteral(pos, l.Value);
                if (n < 0) return false;
                pos += n;
                return true;

            case Seq seq:
                foreach (var r in seq.Items)
                    if (!Match(r, ref pos)) return false;
                return true;

            case Alt alt:
                foreach (var r in alt.Items)
                    if (Match(r, ref pos)) return true;
                return false;

            case SkipRule:
                if (pos >= s.Length) return false;
                pos++;
                return true;

            case EndRule: return pos == s.Length;
            case FailRule: return false;

            case BreakRule:
                ctx.BreakRequested = true;
                return true;

            case Item it:
                if (pos >= s.Length || !it.Test(s[pos])) return false;
                pos++;
                return true;

            case Repeat rep:
                return Loop(rep, ref pos);

            case Ahead a:
                var ap = pos;
                return Match(a.Body, ref ap);

            case Not nt:
                var np = pos;
                return !Match(nt.Body, ref np);

            case To to:
                return Scan(to.Body, ref pos, thru: false);

            case Thru th:
                return Scan(th.Body, ref pos, thru: true);

            case Copy c:
                var cs = pos;
                if (!Match(c.Body, ref pos)) return false;
                ctx.Vars[c.Var] = s.Slice(cs, pos);
                return true;

            case Set st:
                var ss = pos;
                if (!Match(st.Body, ref pos)) return false;
                ctx.Vars[st.Var] = pos > ss ? s[ss] : null;
                return true;

            case Into into:
                if (pos >= s.Length || s[pos] is not IReadOnlyList<object> block) return false;
                var inner = new Matcher(new BlockSeries(block), ctx);
                var ip = 0;
                if (!inner.Match(into.Body, ref ip) || ip != block.Count) return false;
                pos++;
                return true;

            case Ref r:
                if (!ctx.Rules.TryGetValue(r.Name, out var target))
                    throw new KeyNotFoundException($"Undefined rule '{r.Name}'.");
                return Match(target, ref pos);

            case Mark m:
                ctx.Marks[m.Name] = pos;
                return true;

            case Seek sk:
                if (!ctx.Marks.TryGetValue(sk.Name, out var to2))
                    throw new KeyNotFoundException($"Undefined mark '{sk.Name}'.");
                pos = to2;
                return true;

            case Act act:
                ctx.Pos = pos;
                act.Run(ctx);
                return true;

            default:
                throw new NotSupportedException($"Unknown rule node {rule.GetType().Name}.");
        }
    }

    private bool Loop(Repeat rep, ref int pos)
    {
        var count = 0;
        while (rep.Max < 0 || count < rep.Max)
        {
            var before = pos;
            ctx.BreakRequested = false; // drop a stale break from outside this loop or a failed branch
            if (!Match(rep.Body, ref pos)) break;
            count++;
            if (ctx.BreakRequested) { ctx.BreakRequested = false; break; }
            if (pos == before) break; // zero-width match: stop to avoid looping forever
        }
        return count >= rep.Min;
    }

    /// <summary>Find the first position at or after <paramref name="pos"/> where the rule matches.</summary>
    private bool Scan(Rule body, ref int pos, bool thru)
    {
        for (var p = pos; p <= s.Length; p++)
        {
            var q = p;
            if (!Match(body, ref q)) continue;
            pos = thru ? q : p;
            return true;
        }
        return false;
    }
}
