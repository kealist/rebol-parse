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
}

/// <summary>The generated matcher: returns the position after the match, or -1.</summary>
internal delegate int IlFn(string text, int pos, TextEngine e);

/// <summary>A rule compiled to IL, running inside the node engine.</summary>
internal sealed class IlNode(IlFn fn) : Node
{
    public readonly IlFn Fn = fn;   // Pure stays true: nothing a compiled rule does breaks, keeps or edits

    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        if (e.Edited) throw new InvalidOperationException("A compiled rule cannot run after another rule has edited the text.");
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

    /// <summary>Run the rule named at a <see cref="RefSite"/>: a compiled rule is called directly, a pure interpreted one through its node.</summary>
    public static int Call(RefSite site, string text, int pos, TextEngine e)
    {
        var target = site.Target(e);
        if (target is IlNode il) return il.Fn(text, pos, e);
        if (!target.Pure)
            throw new InvalidOperationException(
                $"Rule '{site.Name}' is used by a compiled rule but cannot be: it breaks, keeps, edits or refers to other rules without being compiled. Compile the whole rule table with Compiled().");
        var span = text.AsSpan();
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
    public static IlNode? TryCompile(Rule rule) => Supports(rule) ? new IlNode(new Gen().Build(rule)) : null;

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
        CompiledRule c => Supports(c.Body),
        _ => false,
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
    private sealed class Gen
    {
        private readonly ParameterExpression text = Expression.Parameter(typeof(string), "text");
        private readonly ParameterExpression start = Expression.Parameter(typeof(int), "start");
        private readonly ParameterExpression e = Expression.Parameter(typeof(TextEngine), "e");
        private readonly ParameterExpression len = Expression.Variable(typeof(int), "len");
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
                [len, ic, .. locals],
                Expression.Assign(pos, start),
                Expression.Assign(len, Expression.Property(text, nameof(string.Length))),
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
        private Expression CharAtPos(Expression pos) => Expression.Call(text, CharAt, pos);
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
                            Expression.Not(Expression.Call(Runtime(nameof(IlRuntime.StartsWith)), text, pos, Expression.Constant(s), ic)),
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
                    var saved = Local<int>("ahead");
                    return Expression.Block(Expression.Assign(saved, pos), Emit(a.Body, pos, fail), Expression.Assign(pos, saved));
                }
                case Not n:
                {
                    var saved = Local<int>("not");
                    var ok = Expression.Label("notOk");
                    return Expression.Block(
                        Expression.Assign(saved, pos),
                        Emit(n.Body, pos, ok),
                        Fail(fail),   // the body matched
                        Expression.Label(ok),
                        Expression.Assign(pos, saved));
                }
                case To t: return EmitScan(t.Body, thru: false, pos, fail);
                case Thru t: return EmitScan(t.Body, thru: true, pos, fail);
                case Copy c:
                {
                    var from = Local<int>("copy");
                    return Expression.Block(
                        Expression.Assign(from, pos),
                        Emit(c.Body, pos, fail),
                        Expression.Call(Runtime(nameof(IlRuntime.Copy)), e, Expression.Constant(c.Var), text, from, pos));
                }
                case Set s:
                {
                    var from = Local<int>("set");
                    return Expression.Block(
                        Expression.Assign(from, pos),
                        Emit(s.Body, pos, fail),
                        Expression.Call(Runtime(nameof(IlRuntime.Set)), e, Expression.Constant(s.Var), text, from, pos));
                }
                case Ref r:
                    return Expression.Block(
                        Expression.Assign(pos, Expression.Call(Runtime(nameof(IlRuntime.Call)), Expression.Constant(new RefSite(r.Name)), text, pos, e)),
                        Expression.IfThen(Expression.LessThan(pos, Int(0)), Fail(fail)));
                case Mark m: return Expression.Call(Runtime(nameof(IlRuntime.Mark)), e, Expression.Constant(m.Name), pos);
                case Seek s: return Expression.Assign(pos, Expression.Call(Runtime(nameof(IlRuntime.Seek)), e, Expression.Constant(s.Name)));
                case Act a: return Expression.Call(Runtime(nameof(IlRuntime.Act)), e, Expression.Constant(a.Run), pos);
                default:   // Supports admits nothing else
                    return Expression.IfThen(
                        Expression.Not(Expression.Call(Runtime(nameof(IlRuntime.If)), e, Expression.Constant(((If)rule).Condition), pos)),
                        Fail(fail));
            }
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
                if (i == alt.Items.Length - 1)
                {
                    parts.Add(Emit(alt.Items[i], pos, fail));   // the last alternative fails the whole choice
                    break;
                }
                var next = Expression.Label("altNext");
                parts.Add(Emit(alt.Items[i], pos, next));
                parts.Add(Expression.Goto(done));
                parts.Add(Expression.Label(next));
                parts.Add(Expression.Assign(pos, saved));
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
                        Expression.Assign(at, Expression.Call(Runtime(nameof(IlRuntime.IndexOf)), text, pos, Expression.Constant(s), ic)),
                        Expression.IfThen(Expression.LessThan(at, Int(0)), Fail(fail)),
                        Advance(pos, thru ? Expression.Add(at, Int(s.Length)) : at));
                }
                case Lit { Value: char c }:
                {
                    var at = Local<int>("scanAt");
                    return Expression.Block(
                        Expression.Assign(at, Expression.Call(Runtime(nameof(IlRuntime.IndexOfChar)), text, pos, Expression.Constant(c), ic)),
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
            return Expression.Block(
                typeof(void),
                Expression.Assign(from, pos),
                Expression.Label(top),
                Expression.IfThen(Expression.GreaterThan(from, len), Fail(fail)),
                Expression.Assign(q, from),
                Emit(body, q, next),
                Expression.Assign(pos, thru ? q : from),
                Expression.Goto(done),
                Expression.Label(next),
                Expression.PreIncrementAssign(from),
                Expression.Goto(top),
                Expression.Label(done));
        }
    }
}
