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
    internal bool RejectRequested;
    internal int LoopDepth;
    internal bool IgnoreCase;
    internal Stack<List<object>> Collected { get; } = new();
}

/// <param name="Matched">Whether the rule matched (and consumed all input, unless parsing partially).</param>
/// <param name="Position">Where matching stopped.</param>
/// <param name="Vars">Values stored by <c>copy</c>, <c>set</c> and <c>collect</c>.</param>
/// <param name="Output">The input after any <c>insert</c>/<c>change</c>/<c>remove</c> edits: a string, or a list for block input. The caller's input is never modified.</param>
public sealed record ParseResult(bool Matched, int Position, IReadOnlyDictionary<string, object?> Vars, object? Output = null)
{
    public static implicit operator bool(ParseResult r) => r.Matched;
}

public static class Parser
{
    /// <summary>
    /// Parse a string. By default the rule must consume all input, as in REBOL.
    /// <paramref name="ignoreCase"/> makes literal strings and chars match regardless of case.
    /// </summary>
    public static ParseResult Parse(string input, Rule rule, IReadOnlyDictionary<string, Rule>? rules = null,
        bool partial = false, bool ignoreCase = false) =>
        Run(new StringSeries(input, ignoreCase), rule, rules, partial, ignoreCase);

    /// <summary>Parse a block of values (chars, strings, numbers, <see cref="Word"/>, nested lists).</summary>
    public static ParseResult Parse(IReadOnlyList<object> input, Rule rule, IReadOnlyDictionary<string, Rule>? rules = null,
        bool partial = false, bool ignoreCase = false) =>
        Run(new BlockSeries(input, ignoreCase), rule, rules, partial, ignoreCase);

    private static ParseResult Run(ISeries series, Rule rule, IReadOnlyDictionary<string, Rule>? rules, bool partial, bool ignoreCase)
    {
        var ctx = new Ctx { Rules = rules ?? new Dictionary<string, Rule>(), IgnoreCase = ignoreCase };
        var pos = 0;
        var ok = new Matcher(series, ctx).Match(rule, ref pos);
        return new ParseResult(ok && (partial || pos == series.Length), pos, ctx.Vars, series.ToResult());
    }
}

internal sealed class Matcher(ISeries s, Ctx ctx)
{
    /// <summary>
    /// Match <paramref name="rule"/> at <paramref name="pos"/>. On failure pos is left unchanged, and any
    /// break/reject or <c>keep</c> from the failed attempt is dropped. Captures, actions and edits are not undone.
    /// </summary>
    public bool Match(Rule rule, ref int pos)
    {
        var start = pos;
        var wasBreaking = ctx.BreakRequested;
        var wasRejecting = ctx.RejectRequested;
        var kept = ctx.Collected.Count > 0 ? ctx.Collected.Peek().Count : 0;
        if (Step(rule, ref pos)) return true;
        pos = start;
        ctx.BreakRequested = wasBreaking;
        ctx.RejectRequested = wasRejecting;
        if (ctx.Collected.Count > 0)
        {
            var top = ctx.Collected.Peek();
            if (top.Count > kept) top.RemoveRange(kept, top.Count - kept);
        }
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

            case RejectRule:
                if (ctx.LoopDepth == 0) return false;
                ctx.RejectRequested = true;
                return true;

            case Insert ins:
                pos += s.Insert(pos, ins.Value(ctx));
                return true;

            case Change chg:
                var chStart = pos;
                if (!Match(chg.Body, ref pos)) return false;
                s.Remove(chStart, pos);
                pos = chStart + s.Insert(chStart, chg.Value(ctx));
                return true;

            case Remove rem:
                var rmStart = pos;
                if (!Match(rem.Body, ref pos)) return false;
                s.Remove(rmStart, pos);
                pos = rmStart;
                return true;

            case Collect col:
                var list = new List<object>();
                ctx.Collected.Push(list);
                var collected = Match(col.Body, ref pos);
                ctx.Collected.Pop();
                if (!collected) return false;
                ctx.Vars[col.Var] = list;
                return true;

            case Keep keep:
                if (ctx.Collected.Count == 0) throw new InvalidOperationException("'keep' used outside 'collect'.");
                var kStart = pos;
                if (!Match(keep.Body, ref pos)) return false;
                var span = s.Slice(kStart, pos);
                ctx.Collected.Peek().Add(span is IReadOnlyList<object> { Count: 1 } one ? one[0] : span);
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
                var innerSeries = new BlockSeries(block, ctx.IgnoreCase);
                var inner = new Matcher(innerSeries, ctx);
                var ip = 0;
                if (!inner.Match(into.Body, ref ip) || ip != innerSeries.Length) return false;
                if (innerSeries.Modified)
                {
                    s.Remove(pos, pos + 1);
                    s.Insert(pos, innerSeries.ToResult());
                }
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
        var rejected = false;
        ctx.LoopDepth++;
        while (rep.Max < 0 || count < rep.Max)
        {
            var before = pos;
            var lengthBefore = s.Length;
            ctx.BreakRequested = false; // drop a stale break from outside this loop or a failed branch
            ctx.RejectRequested = false;
            if (!Match(rep.Body, ref pos)) break;
            count++;
            if (ctx.RejectRequested) { ctx.RejectRequested = false; rejected = true; break; }
            if (ctx.BreakRequested) { ctx.BreakRequested = false; break; }
            if (pos == before && s.Length == lengthBefore) break; // no progress: stop to avoid looping forever
        }
        ctx.LoopDepth--;
        return !rejected && count >= rep.Min;
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
