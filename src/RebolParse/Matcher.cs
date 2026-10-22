namespace RebolParse;

/// <summary>
/// The state of a parse in progress, handed to actions and value functions: captures so far, saved marks, and
/// the current position. State is shared across a whole parse (including nested <c>into</c> blocks). During <see cref="Parser.IsMatch(string, Rule, IReadOnlyDictionary{string, Rule}?, bool, bool)"/>
/// one instance is reused for every call on a thread, so do not keep a reference to it.
/// </summary>
public sealed class Ctx
{
    private static readonly Dictionary<string, Rule> NoRules = [];
    private static readonly IReadOnlyDictionary<string, object?> NoVars = new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());

    // Created on first use so a parse that never captures, marks or collects allocates none of them.
    private Dictionary<string, object?>? vars;
    private Dictionary<string, int>? marks;
    private Stack<List<object>>? collected;
    private List<BlockSeries>? blockPool;
    private int blockDepth;

    /// <summary>Values stored by <c>copy</c>, <c>set</c> and <c>collect</c> so far.</summary>
    public Dictionary<string, object?> Vars => vars ??= [];
    /// <summary>Positions saved by <c>name:</c> marks.</summary>
    public Dictionary<string, int> Marks => marks ??= [];
    /// <summary>The named rules passed to the parse; what <c>Ref</c> looks names up in.</summary>
    public IReadOnlyDictionary<string, Rule> Rules { get; internal set; } = NoRules;

    /// <summary>Current position in the series being matched when an action runs.</summary>
    public int Pos { get; internal set; }
    internal bool BreakRequested;
    internal bool RejectRequested;
    internal int LoopDepth;
    internal bool IgnoreCase;
    internal bool ReadOnly;
    internal long StepsLeft;   // loop iterations, scan positions and rule calls still allowed
    internal long StepLimit;

    internal IReadOnlyDictionary<string, object?> VarsOrEmpty => vars ?? NoVars;
    internal Dictionary<string, int> MarksTable => Marks;
    internal Stack<List<object>>? Collected => collected;
    internal Stack<List<object>> CollectedStack => collected ??= new();

    /// <summary>The list the innermost <c>collect</c> is filling.</summary>
    internal List<object> KeepTarget() =>
        collected is { Count: > 0 } c ? c.Peek() : throw new InvalidOperationException("'keep' used outside 'collect'.");

    internal int MarkAt(string name) =>
        marks != null && marks.TryGetValue(name, out var at) ? at : throw new KeyNotFoundException($"Undefined mark '{name}'.");

    internal void Reset(IReadOnlyDictionary<string, Rule>? rules, bool ignoreCase, bool readOnly, long maxSteps)
    {
        StepsLeft = StepLimit = maxSteps;
        vars?.Clear();
        marks?.Clear();
        collected?.Clear();
        Rules = rules ?? NoRules;
        IgnoreCase = ignoreCase;
        ReadOnly = readOnly;
        Pos = 0;
        LoopDepth = 0;
        blockDepth = 0;
        BreakRequested = false;
        RejectRequested = false;
    }

    /// <summary>A series over a nested block, taken from a per-depth pool so repeated <c>into</c> allocates nothing.</summary>
    internal BlockSeries RentBlock(IReadOnlyList<object> block)
    {
        blockPool ??= [];
        if (blockDepth == blockPool.Count) blockPool.Add(new BlockSeries(block));
        var series = blockPool[blockDepth++];
        series.Reset(block, IgnoreCase, ReadOnly);
        return series;
    }

    internal void ReturnBlock() => blockDepth--;

    /// <summary>Count one unit of work that may repeat: a loop iteration, a scan position or a named-rule call.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void Step()
    {
        if (--StepsLeft < 0) TooManySteps();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    /// <summary>Count work that is not one step: characters inserted, or characters an edit had to move.</summary>
    internal void Charge(long steps)
    {
        StepsLeft -= steps;
        if (StepsLeft < 0) TooManySteps();
    }

    private void TooManySteps() => throw new StepLimitExceededException(StepLimit);
}

/// <summary>Thrown when a parse uses more steps than its limit: see <see cref="Parser"/>.</summary>
public sealed class StepLimitExceededException(long limit)
    : Exception($"The parse took more than {limit:N0} steps. The rule may backtrack exponentially or loop without end; pass a larger maxSteps if the input really needs it.")
{
    /// <summary>The limit that was reached.</summary>
    public long Limit { get; } = limit;
}

/// <param name="Matched">Whether the rule matched (and consumed all input, unless parsing partially).</param>
/// <param name="Position">Where matching stopped.</param>
/// <param name="Vars">Values stored by <c>copy</c>, <c>set</c> and <c>collect</c>.</param>
/// <param name="Output">The input after any <c>insert</c>/<c>change</c>/<c>remove</c> edits: a string, or a list for block input. The caller's input is never modified.</param>
public sealed record ParseResult(bool Matched, int Position, IReadOnlyDictionary<string, object?> Vars, object? Output = null)
{
    /// <summary>A result is true when it matched, so <c>if (Parser.Parse(...))</c> works.</summary>
    public static implicit operator bool(ParseResult r) => r.Matched;
}

/// <summary>Runs rules against input. <see cref="Parse(string, Rule, IReadOnlyDictionary{string, Rule}?, bool, bool, long?)"/> returns captures and edits; <see cref="IsMatch(string, Rule, IReadOnlyDictionary{string, Rule}?, bool, bool, long?)"/> only says whether the rule matched, and allocates nothing.</summary>
/// <remarks>
/// Every parse has a step limit, so a rule that backtracks exponentially or loops forever throws
/// <see cref="StepLimitExceededException"/> instead of hanging. A step is one loop iteration, one position tried by
/// <c>to</c>/<c>thru</c>, or one call of a named rule. Without <c>maxSteps</c> the limit is
/// <see cref="DefaultMaxSteps"/> of the input length; pass <see cref="NoStepLimit"/> to turn it off.
/// </remarks>
public static class Parser
{
    /// <summary>Pass as <c>maxSteps</c> to parse without a step limit.</summary>
    public const long NoStepLimit = long.MaxValue;

    /// <summary>
    /// The step limit used when no <c>maxSteps</c> is given: 1000 steps per item of input, and at least ten million.
    /// Real grammars use a few steps per item, so only runaway rules reach it, in about a second.
    /// </summary>
    public static long DefaultMaxSteps(int inputLength) => Math.Max(10_000_000L, 1000L * inputLength);

    private static long Limit(long? maxSteps, int inputLength) => maxSteps switch
    {
        null => DefaultMaxSteps(inputLength),
        <= 0 => throw new ArgumentOutOfRangeException(nameof(maxSteps), maxSteps, "The step limit must be positive; use Parser.NoStepLimit for none."),
        _ => maxSteps.Value,
    };

    /// <summary>
    /// Parse a string. By default the rule must consume all input, as in REBOL.
    /// <paramref name="ignoreCase"/> makes literal strings and chars match regardless of case.
    /// Build the rule once and reuse it: the first parse with a rule compiles it, like constructing a <c>Regex</c>.
    /// </summary>
    /// <remarks><c>maxSteps</c> is the step limit (see <see cref="Parser"/>); null means <see cref="DefaultMaxSteps"/>.</remarks>
    /// <exception cref="StepLimitExceededException">The parse used more steps than its limit.</exception>
    public static ParseResult Parse(string input, Rule rule, IReadOnlyDictionary<string, Rule>? rules = null,
        bool partial = false, bool ignoreCase = false, long? maxSteps = null)
    {
        var node = Compiler.Get(rule);
        var ctx = new Ctx();
        ctx.Reset(rules, ignoreCase, readOnly: false, Limit(maxSteps, input.Length));
        var engine = new TextEngine();
        var text = engine.Reset(input, ignoreCase, ctx, isReadOnly: false);
        var pos = 0;
        bool ok;
        try { ok = node.Match(engine, ref text, ref pos); }
        catch { engine.Clear(); throw; }   // give the edit buffer back, cleared
        return new ParseResult(ok && (partial || pos == text.Length), pos, ctx.VarsOrEmpty, engine.Result());
    }

    /// <summary>Parse a block of values (chars, strings, numbers, <see cref="Word"/>, nested lists).</summary>
    /// <remarks><c>maxSteps</c> is the step limit (see <see cref="Parser"/>); null means <see cref="DefaultMaxSteps"/>.</remarks>
    /// <exception cref="StepLimitExceededException">The parse used more steps than its limit.</exception>
    public static ParseResult Parse(IReadOnlyList<object> input, Rule rule, IReadOnlyDictionary<string, Rule>? rules = null,
        bool partial = false, bool ignoreCase = false, long? maxSteps = null)
    {
        var series = new BlockSeries(input, ignoreCase);
        var ctx = new Ctx();
        ctx.Reset(rules, ignoreCase, readOnly: false, Limit(maxSteps, input.Count));
        var pos = 0;
        var ok = new Matcher(series, ctx).Match(rule, ref pos);
        return new ParseResult(ok && (partial || pos == series.Length), pos, ctx.VarsOrEmpty, series.ToResult());
    }

    /// <summary>
    /// Does <paramref name="rule"/> match? Allocation-free once warm for rules that do not <c>copy</c>, <c>set</c>,
    /// <c>collect</c> or use <see cref="P.Is"/> on text: all working state is reused per thread. Because nothing is
    /// returned, rules that edit input (<c>insert</c>, <c>change</c>, <c>remove</c>) throw; use <see cref="Parse(string, Rule, IReadOnlyDictionary{string, Rule}?, bool, bool, long?)"/> for those.
    /// </summary>
    /// <remarks><c>maxSteps</c> is the step limit (see <see cref="Parser"/>); null means <see cref="DefaultMaxSteps"/>.</remarks>
    /// <exception cref="StepLimitExceededException">The parse used more steps than its limit.</exception>
    public static bool IsMatch(string input, Rule rule, IReadOnlyDictionary<string, Rule>? rules = null,
        bool partial = false, bool ignoreCase = false, long? maxSteps = null)
    {
        var node = Compiler.Get(rule);
        var scratch = Scratch.Acquire();
        try { return scratch.RunText(input, node, rules, partial, ignoreCase, Limit(maxSteps, input.Length)); }
        finally { scratch.Release(); }
    }

    /// <summary><see cref="IsMatch(string, Rule, IReadOnlyDictionary{string, Rule}?, bool, bool, long?)"/> for a block of values.</summary>
    /// <remarks><c>maxSteps</c> is the step limit (see <see cref="Parser"/>); null means <see cref="DefaultMaxSteps"/>.</remarks>
    /// <exception cref="StepLimitExceededException">The parse used more steps than its limit.</exception>
    public static bool IsMatch(IReadOnlyList<object> input, Rule rule, IReadOnlyDictionary<string, Rule>? rules = null,
        bool partial = false, bool ignoreCase = false, long? maxSteps = null)
    {
        var scratch = Scratch.Acquire();
        try { return scratch.RunBlock(input, rule, rules, partial, ignoreCase, Limit(maxSteps, input.Count)); }
        finally { scratch.Release(); }
    }

    // The 0.1 signatures, kept so code compiled against them still runs. They use the default step limit.

    /// <summary>Parse with the default step limit.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static ParseResult Parse(string input, Rule rule, IReadOnlyDictionary<string, Rule>? rules, bool partial, bool ignoreCase) =>
        Parse(input, rule, rules, partial, ignoreCase, maxSteps: null);

    /// <summary>Parse with the default step limit.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static ParseResult Parse(IReadOnlyList<object> input, Rule rule, IReadOnlyDictionary<string, Rule>? rules, bool partial, bool ignoreCase) =>
        Parse(input, rule, rules, partial, ignoreCase, maxSteps: null);

    /// <summary>IsMatch with the default step limit.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static bool IsMatch(string input, Rule rule, IReadOnlyDictionary<string, Rule>? rules, bool partial, bool ignoreCase) =>
        IsMatch(input, rule, rules, partial, ignoreCase, maxSteps: null);

    /// <summary>IsMatch with the default step limit.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static bool IsMatch(IReadOnlyList<object> input, Rule rule, IReadOnlyDictionary<string, Rule>? rules, bool partial, bool ignoreCase) =>
        IsMatch(input, rule, rules, partial, ignoreCase, maxSteps: null);

    /// <summary>Per-thread reusable parse state for <c>IsMatch</c>. A re-entrant call (an action calling IsMatch) gets its own.</summary>
    private sealed class Scratch
    {
        [ThreadStatic] private static Scratch? cached;

        private readonly Ctx ctx = new();
        private readonly TextEngine engine = new();
        private readonly Matcher matcher = new(null!, null!);
        private readonly BlockSeries block = new([]);
        private bool inUse;

        public static Scratch Acquire()
        {
            var scratch = cached ??= new Scratch();
            if (scratch.inUse) scratch = new Scratch();
            scratch.inUse = true;
            return scratch;
        }

        public void Release()
        {
            engine.Clear();   // do not keep the caller's input alive
            inUse = false;
        }

        public bool RunText(string input, Node node, IReadOnlyDictionary<string, Rule>? rules, bool partial, bool ignoreCase, long maxSteps)
        {
            ctx.Reset(rules, ignoreCase, readOnly: true, maxSteps);
            var text = engine.Reset(input, ignoreCase, ctx, isReadOnly: true);
            var pos = 0;
            return node.Match(engine, ref text, ref pos) && (partial || pos == text.Length);
        }

        public bool RunBlock(IReadOnlyList<object> input, Rule rule, IReadOnlyDictionary<string, Rule>? rules, bool partial, bool ignoreCase, long maxSteps)
        {
            ctx.Reset(rules, ignoreCase, readOnly: true, maxSteps);
            block.Reset(input, ignoreCase, newReadOnly: true);
            matcher.Reset(block, ctx);
            var pos = 0;
            return matcher.Match(rule, ref pos) && (partial || pos == block.Length);
        }
    }
}

/// <summary>The interpreter for block input. String input runs on the compiled nodes in Compiled.cs.</summary>
internal sealed class Matcher
{
    private ISeries s;
    private Ctx ctx;

    public Matcher(ISeries s, Ctx ctx)
    {
        this.s = s;
        this.ctx = ctx;
    }

    public void Reset(ISeries newSeries, Ctx newCtx)
    {
        s = newSeries;
        ctx = newCtx;
    }

    /// <summary>
    /// Match <paramref name="rule"/> at <paramref name="pos"/>. On failure pos is left unchanged, and any
    /// break/reject or <c>keep</c> from the failed attempt is dropped. Captures, actions and edits are not undone.
    /// </summary>
    public bool Match(Rule rule, ref int pos)
    {
        var start = pos;
        var wasBreaking = ctx.BreakRequested;
        var wasRejecting = ctx.RejectRequested;
        var collected = ctx.Collected;
        var kept = collected is { Count: > 0 } ? collected.Peek().Count : 0;
        if (Step(rule, ref pos)) return true;
        pos = start;
        ctx.BreakRequested = wasBreaking;
        ctx.RejectRequested = wasRejecting;
        if (collected is { Count: > 0 })
        {
            var top = collected.Peek();
            if (top.Count > kept) top.RemoveRange(kept, top.Count - kept);
        }
        return false;
    }

    private bool Step(Rule rule, ref int pos)
    {
        switch (rule)
        {
            case CompiledRule c:
                return Match(c.Body, ref pos);

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
                ctx.Charge((s.Length - pos) >> 4);   // the items after pos shift
                pos += s.Insert(pos, ins.Value(ctx));
                return true;

            case Change chg:
                var chStart = pos;
                if (!Match(chg.Body, ref pos)) return false;
                ctx.Charge((s.Length - chStart) >> 4);
                s.Remove(chStart, pos);
                pos = chStart + s.Insert(chStart, chg.Value(ctx));
                return true;

            case Remove rem:
                var rmStart = pos;
                if (!Match(rem.Body, ref pos)) return false;
                ctx.Charge((s.Length - pos) >> 4);
                s.Remove(rmStart, pos);
                pos = rmStart;
                return true;

            case Collect col:
                var list = new List<object>();
                ctx.CollectedStack.Push(list);
                var collectedOk = Match(col.Body, ref pos);
                ctx.CollectedStack.Pop();
                if (!collectedOk) return false;
                ctx.Vars[col.Var] = list;
                return true;

            case Keep keep:
                var keepTarget = ctx.KeepTarget();
                var kStart = pos;
                if (!Match(keep.Body, ref pos)) return false;
                var span = (IReadOnlyList<object>)s.Slice(kStart, pos);
                keepTarget.Add(span.Count == 1 ? span[0] : span);
                return true;

            case KeepPick pick:
                var pickTarget = ctx.KeepTarget();
                var pStart = pos;
                if (!Match(pick.Body, ref pos)) return false;
                if (pos > pStart) pickTarget.Add(s[pStart]);
                return true;

            case KeepValue kv:
                var valueTarget = ctx.KeepTarget();
                ctx.Pos = pos;
                if (kv.Value(ctx) is { } kept) valueTarget.Add(kept);
                return true;

            case If cond:
                ctx.Pos = pos;
                return cond.Condition(ctx);

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
                System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();   // deeply nested input
                if (pos >= s.Length || s[pos] is not IReadOnlyList<object> block) return false;
                var inner = ctx.RentBlock(block);
                var outer = s;
                s = inner;
                var ip = 0;
                var intoOk = Match(into.Body, ref ip) && ip == inner.Length;
                s = outer;
                var edited = intoOk && inner.Modified ? inner.ToResult() : null;
                ctx.ReturnBlock();
                if (!intoOk) return false;
                if (edited != null)
                {
                    s.Remove(pos, pos + 1);
                    s.Insert(pos, edited);
                }
                pos++;
                return true;

            case Ref r:
                System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();   // deep input or left recursion
                ctx.Step();
                if (!ctx.Rules.TryGetValue(r.Name, out var target))
                    throw new KeyNotFoundException(Errors.UndefinedRule(r.Name));
                return Match(target, ref pos);

            case Mark m:
                ctx.MarksTable[m.Name] = pos;
                return true;

            case Seek sk:
                pos = ctx.MarkAt(sk.Name);
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
            ctx.Step();
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
            ctx.Step();
            var q = p;
            if (!Match(body, ref q)) continue;
            pos = thru ? q : p;
            return true;
        }
        return false;
    }
}
