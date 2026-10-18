using System.Linq.Expressions;
using System.Reflection;

namespace RebolParse;

/// <summary>
/// A rule compiled to IL, like <c>RegexOptions.Compiled</c>: building it costs a few milliseconds, and it then
/// matches without the per-node calls of the interpreter. Make one with <see cref="RuleExtensions.Compiled(Rule)"/>.
/// A rule that uses something the IL compiler does not handle (<c>break</c>, <c>reject</c>, <c>collect</c>,
/// <c>keep</c>, <c>insert</c>, <c>change</c>, <c>remove</c>) runs on the interpreter instead, with the same results.
/// Block input always uses the interpreter.
/// </summary>
/// <param name="Body">The rule to compile.</param>
public sealed record CompiledRule(Rule Body) : Rule
{
    /// <summary>True when this rule really runs as IL, false when it fell back to the interpreter.</summary>
    public bool IsNative => Compiler.Get(this) is IlNode;

    // Set by Compiled(rule table) for rules that can reach an editing rule: they read the engine's edit buffer, not the string.
    internal bool Buffered { get; init; }
}

/// <summary>The generated matcher: returns the position after the match, or -1.</summary>
internal delegate int IlFn(string text, int pos, TextEngine e);

/// <summary>A rule compiled to IL, running inside the node engine.</summary>
internal sealed class IlNode : Node
{
    private readonly Rule rule;
    private IlFn? bufferedFn;

    /// <summary>The generated code. For a buffered rule it reads the engine's edit buffer; otherwise the string.</summary>
    public readonly IlFn Fn;

    /// <summary>True when <see cref="Fn"/> reads the engine's edit buffer, which is the case for any rule that edits.</summary>
    public readonly bool Buffered;

    // pure when nothing in the rule breaks, keeps, edits or refers to other rules, as with the interpreter's nodes
    public IlNode(Rule rule, IlFn fn, bool pure, bool buffered)
    {
        this.rule = rule;
        Fn = fn;
        Pure = pure;
        Buffered = buffered;
    }

    /// <summary>A version that reads the edit buffer, made the first time it is needed (a rule that runs after the text was edited).</summary>
    public IlFn BufferedFn => Buffered ? Fn : bufferedFn ??= IlCompiler.CompileBuffered(rule);

    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        if (Buffered || e.Edited)
        {
            e.EnsureBuffer(text);
            var done = BufferedFn(e.Original, pos, e);
            text = e.CurrentText();   // the rule may have edited it
            if (done < 0) return false;
            pos = done;
            return true;
        }
        var end = Fn(e.Original, pos, e);
        if (end < 0) return false;
        pos = end;
        return true;
    }
}

/// <summary>Where a compiled rule calls another by name. Looks the target up once per rule table.</summary>
internal sealed class RefSite(string name)
{
    private sealed class Bound(IReadOnlyDictionary<string, Rule> rules, Node target)
    {
        public readonly IReadOnlyDictionary<string, Rule> Rules = rules;
        public readonly Node Target = target;
    }

    private Bound? bound;
    public string Name => name;

    public Node Target(TextEngine e)
    {
        var rules = e.Ctx.Rules;
        var b = bound;
        if (b == null || !ReferenceEquals(b.Rules, rules))
        {
            if (!rules.TryGetValue(name, out var rule)) throw new KeyNotFoundException($"Undefined rule '{name}'.");
            bound = b = new Bound(rules, Compiler.Get(rule));
        }
        return b.Target;
    }
}

/// <summary>Helpers the generated code calls for anything that is not a plain comparison.</summary>
internal static class IlRuntime
{
    private static StringComparison Comparison(bool ignoreCase) =>
        ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool StartsWith(string text, int pos, string value, bool ignoreCase) =>
        text.AsSpan(pos).StartsWith(value, Comparison(ignoreCase));

    public static int IndexOf(string text, int pos, string value, bool ignoreCase) =>
        text.AsSpan(pos).IndexOf(value, Comparison(ignoreCase));

    public static int IndexOfChar(string text, int pos, char value, bool ignoreCase)
    {
        if (!ignoreCase) return text.AsSpan(pos).IndexOf(value);
        var upper = char.ToUpperInvariant(value);
        for (var p = pos; p < text.Length; p++)
            if (char.ToUpperInvariant(text[p]) == upper) return p - pos;
        return -1;
    }

    public static void Copy(TextEngine e, string name, string text, int start, int end) =>
        e.Ctx.Vars[name] = text.Substring(start, end - start);

    public static void Set(TextEngine e, string name, string text, int start, int end) =>
        e.Ctx.Vars[name] = end > start ? text[start] : null;

    // the same helpers for code that reads the engine's edit buffer: char i is buf[off + i]

    public static bool StartsWithBuf(char[] buf, int off, int len, int pos, string value, bool ignoreCase) =>
        new ReadOnlySpan<char>(buf, off + pos, len - pos).StartsWith(value, Comparison(ignoreCase));

    public static int IndexOfBuf(char[] buf, int off, int len, int pos, string value, bool ignoreCase) =>
        new ReadOnlySpan<char>(buf, off + pos, len - pos).IndexOf(value, Comparison(ignoreCase));

    public static int IndexOfCharBuf(char[] buf, int off, int len, int pos, char value, bool ignoreCase)
    {
        var span = new ReadOnlySpan<char>(buf, off + pos, len - pos);
        if (!ignoreCase) return span.IndexOf(value);
        var upper = char.ToUpperInvariant(value);
        for (var p = 0; p < span.Length; p++)
            if (char.ToUpperInvariant(span[p]) == upper) return p;
        return -1;
    }

    public static void CopyBuf(TextEngine e, string name, char[] buf, int off, int start, int end) =>
        e.Ctx.Vars[name] = new string(buf, off + start, end - start);

    public static void SetBuf(TextEngine e, string name, char[] buf, int off, int start, int end) =>
        e.Ctx.Vars[name] = end > start ? buf[off + start] : null;

    public static void KeepBuf(List<object> target, char[] buf, int off, int start, int end) =>
        target.Add(new string(buf, off + start, end - start));

    public static void KeepPickBuf(List<object> target, char[] buf, int off, int start, int end)
    {
        if (end > start) target.Add(buf[off + start]);
    }

    public static void RemoveAt(TextEngine e, int start, int end) => e.RemoveAt(start, end);

    /// <summary>Insert what <paramref name="value"/> returns at <paramref name="pos"/>; the result is how many characters went in.</summary>
    public static int InsertValue(TextEngine e, Func<Ctx, object?> value, int pos) => e.InsertAt(pos, value(e.Ctx));

    public static void Mark(TextEngine e, string name, int pos) => e.Ctx.MarksTable[name] = pos;

    public static int Seek(TextEngine e, string name) => e.Ctx.MarkAt(name);

    public static void Act(TextEngine e, Action<Ctx> run, int pos)
    {
        e.Ctx.Pos = pos;
        run(e.Ctx);
    }

    public static bool If(TextEngine e, Func<Ctx, bool> condition, int pos)
    {
        e.Ctx.Pos = pos;
        return condition(e.Ctx);
    }

    // The engine's Try bookkeeping, for the places where a failed attempt must leave no trace in the flags or the collect list.

    /// <summary>The flags and the size of the innermost collect list, packed in one number.</summary>
    public static long Snap(TextEngine e)
    {
        var ctx = e.Ctx;
        var collected = ctx.Collected;
        long kept = collected is { Count: > 0 } ? collected.Peek().Count : 0;
        return (kept << 2) | (ctx.BreakRequested ? 2L : 0L) | (ctx.RejectRequested ? 1L : 0L);
    }

    /// <summary>Undo what a failed attempt did to the flags and the collect list.</summary>
    public static void Restore(TextEngine e, long snap)
    {
        var ctx = e.Ctx;
        ctx.BreakRequested = (snap & 2) != 0;
        ctx.RejectRequested = (snap & 1) != 0;
        var kept = (int)(snap >> 2);
        if (ctx.Collected is { Count: > 0 } collected)
        {
            var top = collected.Peek();
            if (top.Count > kept) top.RemoveRange(kept, top.Count - kept);
        }
    }

    public static void Break(TextEngine e) => e.Ctx.BreakRequested = true;

    /// <summary>Outside a loop <c>reject</c> is plain failure.</summary>
    public static bool Reject(TextEngine e)
    {
        if (e.Ctx.LoopDepth == 0) return false;
        e.Ctx.RejectRequested = true;
        return true;
    }

    public static void LoopEnter(TextEngine e) => e.Ctx.LoopDepth++;
    public static void LoopExit(TextEngine e) => e.Ctx.LoopDepth--;

    public static void ClearFlags(TextEngine e)
    {
        e.Ctx.BreakRequested = false;
        e.Ctx.RejectRequested = false;
    }

    public static bool Rejected(TextEngine e) => e.Ctx.RejectRequested;
    public static bool Broke(TextEngine e) => e.Ctx.BreakRequested;

    public static List<object> CollectBegin(TextEngine e)
    {
        var list = new List<object>();
        e.Ctx.CollectedStack.Push(list);
        return list;
    }

    public static void CollectEnd(TextEngine e) => e.Ctx.CollectedStack.Pop();

    public static void StoreList(TextEngine e, string name, List<object> list) => e.Ctx.Vars[name] = list;

    public static List<object> KeepTarget(TextEngine e) => e.Ctx.KeepTarget();

    public static void Keep(List<object> target, string text, int start, int end) =>
        target.Add(text.Substring(start, end - start));

    public static void KeepPick(List<object> target, string text, int start, int end)
    {
        if (end > start) target.Add(text[start]);
    }

    public static void KeepValue(TextEngine e, Func<Ctx, object?> value, List<object> target, int pos)
    {
        e.Ctx.Pos = pos;
        if (value(e.Ctx) is { } kept) target.Add(kept);
    }

    private const string EditsMessage =
        "Rule '{0}' edits the text, which a compiled rule that reads the original string cannot follow. " +
        "Compile the whole rule table with Compiled(), which finds the rules that reach an edit, or leave the calling rule uncompiled.";

    /// <summary>Run the rule named at a <see cref="RefSite"/> from code that reads the original string.</summary>
    public static int Call(RefSite site, string text, int pos, TextEngine e)
    {
        var target = site.Target(e);
        int end;
        if (target is IlNode il)
        {
            if (il.Buffered) throw new InvalidOperationException(string.Format(EditsMessage, site.Name));
            end = il.Fn(text, pos, e);
        }
        else
        {
            var span = text.AsSpan();
            end = target.Match(e, ref span, ref pos) ? pos : -1;
        }
        if (e.Edited) throw new InvalidOperationException(string.Format(EditsMessage, site.Name));
        return end;
    }

    /// <summary>Run the rule named at a <see cref="RefSite"/> from code that reads the edit buffer; the target may edit it.</summary>
    public static int CallBuf(RefSite site, int pos, TextEngine e)
    {
        var target = site.Target(e);
        if (target is IlNode il) return il.BufferedFn(e.Original, pos, e);
        var span = e.CurrentText();
        return target.Match(e, ref span, ref pos) ? pos : -1;
    }
}

/// <summary>Turns a rule tree into a delegate with expression trees, which the framework compiles to IL.</summary>
internal static class IlCompiler
{
    private static readonly MethodInfo CharAt = typeof(string).GetMethod("get_Chars")!;
    private static readonly MethodInfo ToUpper = typeof(char).GetMethod(nameof(char.ToUpperInvariant), [typeof(char)])!;

    private static MethodInfo Runtime(string name) => typeof(IlRuntime).GetMethod(name)!;

    /// <summary>Compile <paramref name="rule"/>, or return null when it uses something only the interpreter handles.</summary>
public static IlNode? TryCompile(Rule rule, bool buffered = false)
    {
        if (!Supports(rule)) return null;
        // a rule that edits reads the engine's buffer, since the string no longer matches the text after an edit
        buffered |= Parts(rule).Any(r => r is Insert or Change or Remove);
        return new IlNode(rule, new Gen(buffered).Build(rule), IsPure(rule), buffered);
    }

    /// <summary>Generate the buffer-reading version of a rule that otherwise reads the string.</summary>
    public static IlFn CompileBuffered(Rule rule) => new Gen(buffered: true).Build(rule);

    /// <summary>The names of the rules in the table that can reach an edit, through the rules they call.</summary>
    public static HashSet<string> RulesReachingEdits(IReadOnlyDictionary<string, Rule> rules)
    {
        var reaching = new HashSet<string>();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (name, rule) in rules)
                if (!reaching.Contains(name)
                    && Parts(rule).Any(r => r is Insert or Change or Remove || (r is Ref call && reaching.Contains(call.Name))))
                {
                    reaching.Add(name);
                    changed = true;
                }
        }
        return reaching;
    }

    /// <summary>The rule and every rule inside it, in order, produced lazily. The bodies of edits are not visited: callers stop at the edit itself.</summary>
    private static IEnumerable<Rule> Parts(Rule rule)
    {
        yield return rule;
        var inner = rule switch
        {
            Seq s => s.Items,
            Alt a => a.Items,
            Repeat r => [r.Body],
            Ahead a => [a.Body],
            Not n => [n.Body],
            To t => [t.Body],
            Thru t => [t.Body],
            Copy c => [c.Body],
            Set s => [s.Body],
            Collect c => [c.Body],
            Keep k => [k.Body],
            KeepPick k => [k.Body],
            CompiledRule c => [c.Body],
            _ => Array.Empty<Rule>(),
        };
        foreach (var part in inner)
            foreach (var r in Parts(part))
                yield return r;
    }

    private static bool Supports(Rule rule) => rule switch
    {
        Lit or SkipRule or EndRule or FailRule or Item or Ref or Mark or Seek or Act or If or Into => true,
        Seq s => s.Items.All(Supports),
        Alt a => a.Items.All(Supports),
        Repeat r => Supports(r.Body),
        Ahead a => Supports(a.Body),
        Not n => Supports(n.Body),
        To t => Supports(t.Body),
        Thru t => Supports(t.Body),
        Copy c => Supports(c.Body),
        Set s => Supports(s.Body),
        Collect c => Supports(c.Body),
        Keep k => Supports(k.Body),
        KeepPick k => Supports(k.Body),
        Change c => Supports(c.Body),
        Remove r => Supports(r.Body),
        BreakRule or RejectRule or KeepValue or Insert => true,
        CompiledRule c => Supports(c.Body),
        _ => false,
    };

    /// <summary>The interpreter's <c>Node.Pure</c> for the same rule: nothing in it breaks, rejects, keeps or refers to other rules.</summary>
    private static bool IsPure(Rule rule) => rule switch
    {
        BreakRule or RejectRule or Keep or KeepPick or KeepValue or Ref or Insert or Change or Remove => false,
        Seq s => s.Items.All(IsPure),
        Alt a => a.Items.All(IsPure),
        Repeat r => IsPure(r.Body),
        Ahead a => IsPure(a.Body),
        Not n => IsPure(n.Body),
        To t => IsPure(t.Body),
        Thru t => IsPure(t.Body),
        Copy c => IsPure(c.Body),
        Set s => IsPure(s.Body),
        Collect c => IsPure(c.Body),
        CompiledRule c => IsPure(c.Body),
        _ => true,
    };

    private static Rule Unwrap(Rule rule) => rule is CompiledRule c ? Unwrap(c.Body) : rule;

    /// <summary>A call to a character test: direct when the delegate points at an ordinary method, through the delegate otherwise.</summary>
    private static Expression CharTest(Func<char, bool> test, Expression ch)
    {
        var method = test.Method;
        if (method.IsPublic && method.IsStatic == (test.Target is null))
            return method.IsStatic ? Expression.Call(method, ch) : Expression.Call(Expression.Constant(test.Target), method, ch);
        return Expression.Invoke(Expression.Constant(test), ch);
    }

    /// <summary>
    /// Emits code for one rule. On success the code leaves the new position in <c>pos</c> and falls through; on failure
    /// it jumps to <c>fail</c> with <c>pos</c> in an unspecified state, so whoever backtracks restores it.
    /// </summary>
    private sealed class Gen(bool buffered)
    {
        private readonly ParameterExpression text = Expression.Parameter(typeof(string), "text");
        private readonly ParameterExpression start = Expression.Parameter(typeof(int), "start");
        private readonly ParameterExpression e = Expression.Parameter(typeof(TextEngine), "e");
        private readonly ParameterExpression len = Expression.Variable(typeof(int), "len");
        private readonly ParameterExpression buf = Expression.Variable(typeof(char[]), "buf");   // buffered code only
        private readonly ParameterExpression off = Expression.Variable(typeof(int), "off");
        private readonly ParameterExpression ic = Expression.Variable(typeof(bool), "ignoreCase");
        private readonly List<ParameterExpression> locals = [];
        private int counter;

        public IlFn Build(Rule rule)
        {
            var pos = Local<int>("pos");
            var fail = Expression.Label("fail");
            var ret = Expression.Label(typeof(int), "result");
            var code = Emit(rule, pos, fail);
            var body = Expression.Block(
                typeof(int),
                [len, ic, buf, off, .. locals],
                Expression.Assign(pos, start),
                buffered ? Reload() : Expression.Assign(len, Expression.Property(text, nameof(string.Length))),
                Expression.Assign(ic, Expression.Field(e, nameof(TextEngine.IgnoreCase))),
                code,
                Expression.Return(ret, pos),
                Expression.Label(fail),
                Expression.Return(ret, Expression.Constant(-1)),
                Expression.Label(ret, Expression.Constant(0)));
            return Expression.Lambda<IlFn>(body, text, start, e).Compile();
        }

        private ParameterExpression Local<T>(string name)
        {
            var v = Expression.Variable(typeof(T), name + counter++);
            locals.Add(v);
            return v;
        }

        private static Expression Int(int n) => Expression.Constant(n);
        private static Expression Fail(LabelTarget fail) => Expression.Goto(fail);
        private Expression CharAtPos(Expression pos) =>
            buffered ? Expression.ArrayIndex(buf, Expression.Add(off, pos)) : Expression.Call(text, CharAt, pos);

        private static readonly MethodInfo NeedsSettle = typeof(TextEngine).GetMethod(nameof(TextEngine.NeedsSettle))!;
        private static readonly MethodInfo SettleNow = typeof(TextEngine).GetMethod(nameof(TextEngine.SettleNow))!;

        // the engine's buffer, where the text starts and how long it is, after an edit may have changed them
        private Expression Reload() => Expression.Block(
            Expression.Assign(buf, Expression.Property(e, nameof(TextEngine.Buffer))),
            Expression.Assign(off, Expression.Property(e, nameof(TextEngine.ViewOffset))),
            Expression.Assign(len, Expression.Property(e, nameof(TextEngine.ViewLength))));

        // Edited text is read from the end of a gap, so reading before the gap needs it closed first. Anything that
        // puts the position back before an edit calls this, as TextEngine.Settle is called in the node engine.
        private Expression SettleAt(Expression pos) => !buffered
            ? Expression.Empty()
            : Expression.IfThen(Expression.Call(e, NeedsSettle, pos), Expression.Block(Expression.Call(e, SettleNow), Reload()));
        private static Expression Advance(ParameterExpression pos, Expression by) => Expression.AddAssign(pos, by);

        private Expression Emit(Rule rule, ParameterExpression pos, LabelTarget fail)
        {
            switch (rule)
            {
                case CompiledRule c: return Emit(c.Body, pos, fail);
                case Lit { Value: char c }: return EmitChar(c, pos, fail);
                case Lit { Value: string { Length: 0 } }: return Expression.Empty();
                case Lit { Value: string { Length: 1 } one } when !HasCase(one[0]): return EmitChar(one[0], pos, fail);
                case Lit { Value: string s }:
                    return Expression.Block(
                        Expression.IfThen(
                            Expression.Not(buffered
                                ? Expression.Call(Runtime(nameof(IlRuntime.StartsWithBuf)), buf, off, len, pos, Expression.Constant(s), ic)
                                : Expression.Call(Runtime(nameof(IlRuntime.StartsWith)), text, pos, Expression.Constant(s), ic)),
                            Fail(fail)),
                        Advance(pos, Int(s.Length)));
                case Lit or FailRule or Into: return Fail(fail);   // numbers and words never occur in text; text has no nested blocks
                case SkipRule:
                    return Expression.Block(
                        Expression.IfThen(Expression.GreaterThanOrEqual(pos, len), Fail(fail)),
                        Expression.PreIncrementAssign(pos));
                case EndRule: return Expression.IfThen(Expression.NotEqual(pos, len), Fail(fail));
                case Item item: return EmitItem(item, pos, fail);
                case Seq seq: return Expression.Block(typeof(void), seq.Items.Select(i => Emit(i, pos, fail)).DefaultIfEmpty(Expression.Empty()));
                case Alt alt: return EmitAlt(alt, pos, fail);
                case Repeat r: return EmitRepeat(r, pos, fail);
                case Ahead a:
                {
                    // when the body fails so does this rule, and whoever absorbs that failure takes back its effects
                    var saved = Local<int>("ahead");
                    return Expression.Block(Expression.Assign(saved, pos), Emit(a.Body, pos, fail), Expression.Assign(pos, saved), SettleAt(saved));
                }
                case Not n:
                {
                    var saved = Local<int>("not");
                    var ok = Expression.Label("notOk");
                    var parts = new List<Expression> { Expression.Assign(saved, pos) };
                    ParameterExpression? snap = null;
                    if (!IsPure(n.Body))
                    {
                        snap = Local<long>("notSnap");
                        parts.Add(Expression.Assign(snap, Snap()));
                    }
                    parts.Add(Emit(n.Body, pos, ok));
                    parts.Add(Fail(fail));   // the body matched
                    parts.Add(Expression.Label(ok));
                    if (snap != null) parts.Add(Restore(snap));
                    parts.Add(Expression.Assign(pos, saved));
                    parts.Add(SettleAt(saved));
                    return Expression.Block(typeof(void), parts);
                }
                case BreakRule: return Expression.Call(Runtime(nameof(IlRuntime.Break)), e);
                case RejectRule: return Expression.IfThen(Expression.Not(Expression.Call(Runtime(nameof(IlRuntime.Reject)), e)), Fail(fail));
                case Collect c:
                {
                    var list = Local<List<object>>("collect");
                    var failed = Expression.Label("collectFailed");
                    var done = Expression.Label("collectDone");
                    return Expression.Block(
                        typeof(void),
                        Expression.Assign(list, Expression.Call(Runtime(nameof(IlRuntime.CollectBegin)), e)),
                        Emit(c.Body, pos, failed),
                        Expression.Call(Runtime(nameof(IlRuntime.CollectEnd)), e),
                        Expression.Call(Runtime(nameof(IlRuntime.StoreList)), e, Expression.Constant(c.Var), list),
                        Expression.Goto(done),
                        Expression.Label(failed),
                        Expression.Call(Runtime(nameof(IlRuntime.CollectEnd)), e),
                        Fail(fail),
                        Expression.Label(done));
                }
                case Keep k: return EmitKeep(k.Body, nameof(IlRuntime.Keep), pos, fail);
                case KeepPick k: return EmitKeep(k.Body, nameof(IlRuntime.KeepPick), pos, fail);
                case KeepValue k:
                {
                    var target = Local<List<object>>("keep");
                    return Expression.Block(
                        Expression.Assign(target, Expression.Call(Runtime(nameof(IlRuntime.KeepTarget)), e)),
                        Expression.Call(Runtime(nameof(IlRuntime.KeepValue)), e, Expression.Constant(k.Value), target, pos));
                }
                case To t: return EmitScan(t.Body, thru: false, pos, fail);
                case Thru t: return EmitScan(t.Body, thru: true, pos, fail);
                case Copy c:
                {
                    var from = Local<int>("copy");
                    return Expression.Block(
                        Expression.Assign(from, pos),
                        Emit(c.Body, pos, fail),
                        SettleAt(from),
                        buffered
                            ? Expression.Call(Runtime(nameof(IlRuntime.CopyBuf)), e, Expression.Constant(c.Var), buf, off, from, pos)
                            : Expression.Call(Runtime(nameof(IlRuntime.Copy)), e, Expression.Constant(c.Var), text, from, pos));
                }
                case Set s:
                {
                    var from = Local<int>("set");
                    return Expression.Block(
                        Expression.Assign(from, pos),
                        Emit(s.Body, pos, fail),
                        SettleAt(from),
                        buffered
                            ? Expression.Call(Runtime(nameof(IlRuntime.SetBuf)), e, Expression.Constant(s.Var), buf, off, from, pos)
                            : Expression.Call(Runtime(nameof(IlRuntime.Set)), e, Expression.Constant(s.Var), text, from, pos));
                }
                case Insert i:
                    return Expression.Block(
                        Expression.AddAssign(pos, Expression.Call(Runtime(nameof(IlRuntime.InsertValue)), e, Expression.Constant(i.Value), pos)),
                        Reload());
                case Remove r:
                {
                    var from = Local<int>("remove");
                    return Expression.Block(
                        Expression.Assign(from, pos),
                        Emit(r.Body, pos, fail),
                        Expression.Call(Runtime(nameof(IlRuntime.RemoveAt)), e, from, pos),
                        Reload(),
                        Expression.Assign(pos, from));
                }
                case Change c:
                {
                    var from = Local<int>("change");
                    return Expression.Block(
                        Expression.Assign(from, pos),
                        Emit(c.Body, pos, fail),
                        Expression.Call(Runtime(nameof(IlRuntime.RemoveAt)), e, from, pos),
                        Expression.Assign(pos, Expression.Add(from, Expression.Call(Runtime(nameof(IlRuntime.InsertValue)), e, Expression.Constant(c.Value), from))),
                        Reload());
                }
                case Ref r when buffered:
                    // the rule called may edit the text, so the buffer and the length are read again afterwards
                    return Expression.Block(
                        Expression.Assign(pos, Expression.Call(Runtime(nameof(IlRuntime.CallBuf)), Expression.Constant(new RefSite(r.Name)), pos, e)),
                        Reload(),
                        Expression.IfThen(Expression.LessThan(pos, Int(0)), Fail(fail)));   // a rule that rewinds settles the text itself
                case Ref r:
                    return Expression.Block(
                        Expression.Assign(pos, Expression.Call(Runtime(nameof(IlRuntime.Call)), Expression.Constant(new RefSite(r.Name)), text, pos, e)),
                        Expression.IfThen(Expression.LessThan(pos, Int(0)), Fail(fail)));
                case Mark m: return Expression.Call(Runtime(nameof(IlRuntime.Mark)), e, Expression.Constant(m.Name), pos);
                case Seek s:
                    return Expression.Block(
                        Expression.Assign(pos, Expression.Call(Runtime(nameof(IlRuntime.Seek)), e, Expression.Constant(s.Name))),
                        SettleAt(pos));
                case Act a: return Expression.Call(Runtime(nameof(IlRuntime.Act)), e, Expression.Constant(a.Run), pos);
                default:   // Supports admits nothing else
                    return Expression.IfThen(
                        Expression.Not(Expression.Call(Runtime(nameof(IlRuntime.If)), e, Expression.Constant(((If)rule).Condition), pos)),
                        Fail(fail));
            }
        }

        private Expression Snap() => Expression.Call(Runtime(nameof(IlRuntime.Snap)), e);
        private Expression Restore(ParameterExpression snap) => Expression.Call(Runtime(nameof(IlRuntime.Restore)), e, snap);

        private Expression EmitKeep(Rule body, string helper, ParameterExpression pos, LabelTarget fail)
        {
            var target = Local<List<object>>("keep");
            var from = Local<int>("keepFrom");
            return Expression.Block(
                Expression.Assign(target, Expression.Call(Runtime(nameof(IlRuntime.KeepTarget)), e)),   // checks that a collect is active
                Expression.Assign(from, pos),
                Emit(body, pos, fail),
                SettleAt(from),
                buffered
                    ? Expression.Call(Runtime(helper + "Buf"), target, buf, off, from, pos)
                    : Expression.Call(Runtime(helper), target, text, from, pos));
        }

        // ASCII digits and punctuation have no other-case spelling, so they need no ignore-case comparison
        private static bool HasCase(char c) => c >= 128 || char.IsAsciiLetter(c);

        private Expression EmitChar(char c, ParameterExpression pos, LabelTarget fail)
        {
            var ch = CharAtPos(pos);
            Expression same = Expression.Equal(ch, Expression.Constant(c));
            if (HasCase(c))
                same = Expression.OrElse(same, Expression.AndAlso(ic,
                    Expression.Equal(Expression.Call(ToUpper, CharAtPos(pos)), Expression.Constant(char.ToUpperInvariant(c)))));
            return Expression.Block(
                Expression.IfThen(Expression.Not(Expression.AndAlso(Expression.LessThan(pos, len), same)), Fail(fail)),
                Expression.PreIncrementAssign(pos));
        }

        private Expression EmitItem(Item item, ParameterExpression pos, LabelTarget fail)
        {
            var ch = CharAtPos(pos);
            var matches = item.CharTest is { } test
                ? CharTest(test, ch)
                : Expression.Invoke(Expression.Constant(item.Test), Expression.Convert(ch, typeof(object)));
            return Expression.Block(
                Expression.IfThen(Expression.Not(Expression.AndAlso(Expression.LessThan(pos, len), matches)), Fail(fail)),
                Expression.PreIncrementAssign(pos));
        }

        private Expression EmitAlt(Alt alt, ParameterExpression pos, LabelTarget fail)
        {
            if (alt.Items.Length == 0) return Fail(fail);
            var saved = Local<int>("alt");
            var done = Expression.Label("altDone");
            var parts = new List<Expression> { Expression.Assign(saved, pos) };
            for (var i = 0; i < alt.Items.Length; i++)
            {
                var item = alt.Items[i];
                var last = i == alt.Items.Length - 1;
                if (IsPure(item))
                {
                    if (last)
                    {
                        parts.Add(Emit(item, pos, fail));   // the last alternative fails the whole choice
                        break;
                    }
                    var next = Expression.Label("altNext");
                    parts.Add(Emit(item, pos, next));
                    parts.Add(Expression.Goto(done));
                    parts.Add(Expression.Label(next));
                    parts.Add(Expression.Assign(pos, saved));
                    continue;
                }

                // an alternative that may break or keep: a failure takes back what it did to the flags and the collect list
                var snap = Local<long>("altSnap");
                var failed = Expression.Label("altFailed");
                parts.Add(Expression.Assign(snap, Snap()));
                parts.Add(Emit(item, pos, failed));
                parts.Add(Expression.Goto(done));
                parts.Add(Expression.Label(failed));
                parts.Add(Restore(snap));
                parts.Add(Expression.Assign(pos, saved));
                parts.Add(SettleAt(saved));
                if (last) parts.Add(Fail(fail));
            }
            parts.Add(Expression.Label(done));
            return Expression.Block(typeof(void), parts);
        }

        private Expression EmitRepeat(Repeat r, ParameterExpression pos, LabelTarget fail)
        {
            var from = Local<int>("repFrom");
            if (Unwrap(r.Body) is Item { CharTest: { } test })
            {
                // a loop over one character test: no per-iteration bookkeeping
                var limit = Local<int>("repLimit");
                var brk = Expression.Label("repEnd");
                var parts = new List<Expression>
                {
                    Expression.Assign(from, pos),
                    Expression.Assign(limit, r.Max < 0
                        ? len
                        : Expression.Condition(Expression.GreaterThan(Int(r.Max), Expression.Subtract(len, pos)), len, Expression.Add(pos, Int(r.Max)))),
                    Expression.Loop(
                        Expression.IfThenElse(
                            Expression.AndAlso(Expression.LessThan(pos, limit), CharTest(test, CharAtPos(pos))),
                            Expression.PreIncrementAssign(pos),
                            Expression.Break(brk)),
                        brk),
                };
                if (r.Min > 0) parts.Add(CheckMin(r.Min, from, pos, fail, counted: false, count: null));
                return Expression.Block(typeof(void), parts);
            }

            if (!IsPure(r.Body)) return EmitLoop(r, from, pos, fail);

            var count = Local<int>("repCount");
            var before = Local<int>("repBefore");
            var top = Expression.Label("repTop");
            var bodyFailed = Expression.Label("repBodyFailed");
            var done = Expression.Label("repDone");
            var code = new List<Expression>
            {
                Expression.Assign(from, pos),
                Expression.Assign(count, Int(0)),
                Expression.Label(top),
            };
            if (r.Max >= 0) code.Add(Expression.IfThen(Expression.GreaterThanOrEqual(count, Int(r.Max)), Expression.Goto(done)));
            code.Add(Expression.Assign(before, pos));
            code.Add(Emit(r.Body, pos, bodyFailed));
            code.Add(Expression.PreIncrementAssign(count));
            // a zero-width match ends the loop, or it would never stop
            code.Add(Expression.IfThen(Expression.Equal(pos, before), Expression.Goto(done)));
            code.Add(Expression.Goto(top));
            code.Add(Expression.Label(bodyFailed));
            code.Add(Expression.Assign(pos, before));
            code.Add(Expression.Label(done));
            if (r.Min > 0) code.Add(CheckMin(r.Min, from, pos, fail, counted: true, count));
            return Expression.Block(typeof(void), code);
        }

        // A loop whose body may break, reject or keep. Each iteration starts with clear flags, a break or reject
        // ends the loop, and a failed iteration takes back what it did.
        private Expression EmitLoop(Repeat r, ParameterExpression from, ParameterExpression pos, LabelTarget fail)
        {
            var count = Local<int>("loopCount");
            var before = Local<int>("loopBefore");
            var snap = Local<long>("loopSnap");
            var rejected = Local<bool>("loopRejected");
            var lenBefore = Local<int>("loopLen");
            var top = Expression.Label("loopTop");
            var bodyFailed = Expression.Label("loopBodyFailed");
            var done = Expression.Label("loopDone");
            var code = new List<Expression>
            {
                Expression.Assign(from, pos),
                Expression.Assign(count, Int(0)),
                Expression.Assign(rejected, Expression.Constant(false)),
                Expression.Call(Runtime(nameof(IlRuntime.LoopEnter)), e),
                Expression.Label(top),
            };
            if (r.Max >= 0) code.Add(Expression.IfThen(Expression.GreaterThanOrEqual(count, Int(r.Max)), Expression.Goto(done)));
            code.Add(Expression.Assign(before, pos));
            code.Add(Expression.Assign(lenBefore, len));
            code.Add(Expression.Call(Runtime(nameof(IlRuntime.ClearFlags)), e));
            code.Add(Expression.Assign(snap, Snap()));
            code.Add(Emit(r.Body, pos, bodyFailed));
            code.Add(Expression.PreIncrementAssign(count));
            code.Add(Expression.IfThen(
                Expression.Call(Runtime(nameof(IlRuntime.Rejected)), e),
                Expression.Block(
                    Expression.Assign(rejected, Expression.Constant(true)),   // the loop fails, and whoever absorbs that clears the flag
                    Expression.Goto(done))));
            code.Add(Expression.IfThen(
                Expression.Call(Runtime(nameof(IlRuntime.Broke)), e),
                Expression.Block(Expression.Call(Runtime(nameof(IlRuntime.ClearFlags)), e), Expression.Goto(done))));
            // no progress: the position did not move and the text did not change length
            code.Add(Expression.IfThen(Expression.AndAlso(Expression.Equal(pos, before), Expression.Equal(len, lenBefore)), Expression.Goto(done)));
            code.Add(Expression.Goto(top));
            code.Add(Expression.Label(bodyFailed));
            code.Add(Restore(snap));
            code.Add(Expression.Assign(pos, before));
            code.Add(SettleAt(before));
            code.Add(Expression.Label(done));
            code.Add(Expression.Call(Runtime(nameof(IlRuntime.LoopExit)), e));
            code.Add(Expression.IfThen(
                Expression.OrElse(rejected, Expression.LessThan(count, Int(r.Min))),
                Expression.Block(Expression.Assign(pos, from), Fail(fail))));
            return Expression.Block(typeof(void), code);
        }

        private Expression CheckMin(int min, ParameterExpression from, ParameterExpression pos, LabelTarget fail, bool counted, ParameterExpression? count)
        {
            var short_ = counted
                ? Expression.LessThan(count!, Int(min))
                : Expression.LessThan(Expression.Subtract(pos, from), Int(min));
            return Expression.IfThen(short_, Fail(fail));
        }

        private Expression EmitScan(Rule bodyRule, bool thru, ParameterExpression pos, LabelTarget fail)
        {
            var body = Unwrap(bodyRule);
            switch (body)
            {
                case Lit { Value: string s }:
                {
                    var at = Local<int>("scanAt");
                    return Expression.Block(
                        Expression.Assign(at, buffered
                            ? Expression.Call(Runtime(nameof(IlRuntime.IndexOfBuf)), buf, off, len, pos, Expression.Constant(s), ic)
                            : Expression.Call(Runtime(nameof(IlRuntime.IndexOf)), text, pos, Expression.Constant(s), ic)),
                        Expression.IfThen(Expression.LessThan(at, Int(0)), Fail(fail)),
                        Advance(pos, thru ? Expression.Add(at, Int(s.Length)) : at));
                }
                case Lit { Value: char c }:
                {
                    var at = Local<int>("scanAt");
                    return Expression.Block(
                        Expression.Assign(at, buffered
                            ? Expression.Call(Runtime(nameof(IlRuntime.IndexOfCharBuf)), buf, off, len, pos, Expression.Constant(c), ic)
                            : Expression.Call(Runtime(nameof(IlRuntime.IndexOfChar)), text, pos, Expression.Constant(c), ic)),
                        Expression.IfThen(Expression.LessThan(at, Int(0)), Fail(fail)),
                        Advance(pos, thru ? Expression.Add(at, Int(1)) : at));
                }
                case EndRule: return Expression.Assign(pos, len);
                case Item { CharTest: { } test }:
                {
                    var p = Local<int>("scanP");
                    var found = Expression.Label("scanFound");
                    var brk = Expression.Label("scanEnd");
                    return Expression.Block(
                        Expression.Assign(p, pos),
                        Expression.Loop(
                            Expression.Block(
                                Expression.IfThen(Expression.GreaterThanOrEqual(p, len), Expression.Break(brk)),
                                Expression.IfThen(CharTest(test, CharAtPos(p)), Expression.Goto(found)),
                                Expression.PreIncrementAssign(p)),
                            brk),
                        Fail(fail),
                        Expression.Label(found),
                        Expression.Assign(pos, thru ? Expression.Add(p, Int(1)) : p));
                }
            }

            // any other body: try it at each position until it matches
            var from = Local<int>("scanFrom");
            var q = Local<int>("scanQ");
            var top = Expression.Label("scanTop");
            var next = Expression.Label("scanNext");
            var done = Expression.Label("scanDone");
            var impure = !IsPure(body);
            var snap = impure ? Local<long>("scanSnap") : null;
            var parts = new List<Expression>
            {
                Expression.Assign(from, pos),
                Expression.Label(top),
                Expression.IfThen(Expression.GreaterThan(from, len), Fail(fail)),
                Expression.Assign(q, from),
            };
            if (snap != null) parts.Add(Expression.Assign(snap, Snap()));
            parts.Add(Emit(body, q, next));
            parts.Add(Expression.Assign(pos, thru ? q : from));
            parts.Add(SettleAt(pos));
            parts.Add(Expression.Goto(done));
            parts.Add(Expression.Label(next));
            if (snap != null) parts.Add(Restore(snap));
            parts.Add(SettleAt(from));
            parts.Add(Expression.PreIncrementAssign(from));
            parts.Add(Expression.Goto(top));
            parts.Add(Expression.Label(done));
            return Expression.Block(typeof(void), parts);
        }
    }
}
