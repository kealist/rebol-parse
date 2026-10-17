using System.Runtime.CompilerServices;

namespace RebolParse;

/// <summary>
/// Matching state for string input. Nodes match against a <see cref="ReadOnlySpan{T}"/> of the text, so
/// literal matches and scans use the vectorized span helpers; an edit copies the text into a buffer once and
/// shifts it in place after that. One instance is reused per thread by
/// <see cref="Parser.IsMatch(string, Rule, IReadOnlyDictionary{string, Rule}?, bool, bool)"/>.
/// </summary>
internal sealed class TextEngine
{
    public bool IgnoreCase;
    public Ctx Ctx = null!;
    public bool Edited;
    private string original = "";
    private bool readOnly;
    private char[]? buffer;   // the text once edited; grows as needed
    private int length;

    /// <summary>Start a parse over <paramref name="text"/>. The returned span is what nodes match against.</summary>
    public ReadOnlySpan<char> Reset(string text, bool ignoreCase, Ctx ctx, bool isReadOnly)
    {
        original = text;
        IgnoreCase = ignoreCase;
        Ctx = ctx;
        readOnly = isReadOnly;
        Edited = false;
        return text.AsSpan();
    }

    public void Clear()
    {
        original = "";
        Ctx = null!;
    }

    /// <summary>The text after the parse: the original string when nothing was edited.</summary>
    public string Result() => Edited ? new string(buffer, 0, length) : original;

    /// <summary>
    /// Match a node at a point where its failure is absorbed (choice, loop, lookahead, scan). Every node
    /// restores <paramref name="pos"/> itself on failure; this also drops any break/reject or <c>keep</c>
    /// the failed attempt made. Pure nodes cannot have made any, so they skip the bookkeeping.
    /// </summary>
    public bool Try(Node node, ref ReadOnlySpan<char> text, ref int pos)
    {
        if (node.Pure) return node.Match(this, ref text, ref pos);
        var ctx = Ctx;
        var wasBreaking = ctx.BreakRequested;
        var wasRejecting = ctx.RejectRequested;
        var collected = ctx.Collected;
        var kept = collected is { Count: > 0 } ? collected.Peek().Count : 0;
        if (node.Match(this, ref text, ref pos)) return true;
        ctx.BreakRequested = wasBreaking;
        ctx.RejectRequested = wasRejecting;
        if (collected is { Count: > 0 })
        {
            var top = collected.Peek();
            if (top.Count > kept) top.RemoveRange(kept, top.Count - kept);
        }
        return false;
    }

    private void CheckWritable()
    {
        if (readOnly) throw new InvalidOperationException("This parse is read-only (IsMatch); use Parser.Parse to edit input.");
    }

    /// <summary>Copy the text into the edit buffer the first time it changes, with room for <paramref name="extra"/> more chars.</summary>
    private void BeginEdit(ReadOnlySpan<char> text, int extra)
    {
        var needed = text.Length + extra;
        if (!Edited)
        {
            buffer = new char[Math.Max(needed, 64) * 2];
            text.CopyTo(buffer);
            length = text.Length;
            Edited = true;
        }
        else if (buffer!.Length < needed)
        {
            Array.Resize(ref buffer, needed * 2);
        }
    }

    public void Remove(ref ReadOnlySpan<char> text, int start, int end)
    {
        CheckWritable();
        if (end <= start) return;
        BeginEdit(text, 0);
        Array.Copy(buffer!, end, buffer!, start, length - end);
        length -= end - start;
        text = new ReadOnlySpan<char>(buffer, 0, length);
    }

    public int Insert(ref ReadOnlySpan<char> text, int pos, object? value)
    {
        CheckWritable();
        var str = value?.ToString() ?? "";
        if (str.Length == 0) return 0;
        BeginEdit(text, str.Length);
        Array.Copy(buffer!, pos, buffer!, pos + str.Length, length - pos);
        str.CopyTo(0, buffer!, pos, str.Length);
        length += str.Length;
        text = new ReadOnlySpan<char>(buffer, 0, length);
        return str.Length;
    }
}

/// <summary>
/// A compiled rule node for string input. <c>text</c> is the current text, passed by reference so an edit can
/// replace it for everything upstream. Contract: when <see cref="Match"/> returns false, <c>pos</c> is unchanged.
/// </summary>
internal abstract class Node
{
    /// <summary>
    /// True when nothing in the subtree sets break/reject, keeps, or edits the text. <see cref="TextEngine.Try"/>
    /// skips its bookkeeping for pure nodes and loops over pure bodies skip the flag handling.
    /// </summary>
    public bool Pure = true;

    public abstract bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos);
}

/// <summary>Turns a <see cref="Rule"/> tree into <see cref="Node"/>s, once per rule instance.</summary>
internal static class Compiler
{
    private static readonly ConditionalWeakTable<Rule, Node> Cache = new();

    // The rule used last on this thread: a hot loop over one rule pays a reference compare, not a table lookup.
    [ThreadStatic] private static Rule? lastRule;
    [ThreadStatic] private static Node? lastNode;

    public static Node Get(Rule rule)
    {
        if (ReferenceEquals(rule, lastRule)) return lastNode!;
        var node = Cache.GetValue(rule, Compile);
        lastRule = rule;
        lastNode = node;
        return node;
    }

    private static Node[] CompileAll(Rule[] rules)
    {
        var nodes = new Node[rules.Length];
        for (var i = 0; i < rules.Length; i++) nodes[i] = Compile(rules[i]);
        return nodes;
    }

    private static bool AllPure(Node[] nodes)
    {
        foreach (var n in nodes) if (!n.Pure) return false;
        return true;
    }

    private static Node Compile(Rule rule)
    {
        switch (rule)
        {
            case Lit l:
                return l.Value switch
                {
                    char c => new LitChar(c),
                    string s => new LitString(s),
                    _ => new Never(),   // numbers and words never occur in text
                };
            case Seq q: { var items = CompileAll(q.Items); return new SeqNode(items) { Pure = AllPure(items) }; }
            case Alt a: { var items = CompileAll(a.Items); return new AltNode(items) { Pure = AllPure(items) }; }
            case SkipRule: return new SkipNode();
            case EndRule: return new EndNode();
            case FailRule: return new Never();
            case BreakRule: return new BreakNode { Pure = false };
            case RejectRule: return new RejectNode { Pure = false };
            case Item it: return it.CharTest is { } test ? new CharTestNode(test) : new ItemNode(it.Test);
            case Repeat r:
            {
                var body = Compile(r.Body);
                if (body is CharTestNode ct) return new RepeatChar(ct.Test, r.Min, r.Max);
                if (body.Pure) return new RepeatPure(body, r.Min, r.Max);
                return new RepeatNode(body, r.Min, r.Max) { Pure = false };
            }
            case Ahead a: { var body = Compile(a.Body); return new AheadNode(body) { Pure = body.Pure }; }
            case Not n: { var body = Compile(n.Body); return new NotNode(body) { Pure = body.Pure }; }
            case To t: return Scan(Compile(t.Body), thru: false);
            case Thru t: return Scan(Compile(t.Body), thru: true);
            case Copy c: { var body = Compile(c.Body); return new CopyNode(c.Var, body) { Pure = body.Pure }; }
            case Set s: { var body = Compile(s.Body); return new SetNode(s.Var, body) { Pure = body.Pure }; }
            case Collect c: { var body = Compile(c.Body); return new CollectNode(c.Var, body) { Pure = body.Pure }; }
            case Keep k: return new KeepNode(Compile(k.Body)) { Pure = false };
            case KeepPick k: return new KeepPickNode(Compile(k.Body)) { Pure = false };
            case KeepValue k: return new KeepValueNode(k.Value) { Pure = false };
            case If i: return new IfNode(i.Condition);
            case Ref r: return new RefNode(r.Name) { Pure = false };   // the target is not known until it runs
            case Mark m: return new MarkNode(m.Name);
            case Seek s: return new SeekNode(s.Name);
            case Act a: return new ActNode(a.Run);
            case Into: return new Never();   // text has no nested blocks
            case Insert i: return new InsertNode(i.Value) { Pure = false };
            case Change c: return new ChangeNode(Compile(c.Body), c.Value) { Pure = false };
            case Remove r: return new RemoveNode(Compile(r.Body)) { Pure = false };
            default:
                throw new NotSupportedException($"Unknown rule node {rule.GetType().Name}.");
        }
    }

    private static Node Scan(Node body, bool thru) => body switch
    {
        LitString s => new ScanString(s.Value, thru),
        LitChar c => new ScanChar(c.Value, thru),
        CharTestNode t => new ScanCharTest(t.Test, thru),
        EndNode => new ScanEnd(),
        _ => new ScanNode(body, thru) { Pure = body.Pure },
    };
}

// ---- Nodes ----

internal sealed class Never : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos) => false;
}

internal sealed class LitChar(char value) : Node
{
    public readonly char Value = value;

    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
                if (pos >= text.Length) return false;
        var c = text[pos];
        if (c != Value && !(e.IgnoreCase && char.ToUpperInvariant(c) == char.ToUpperInvariant(Value))) return false;
        pos++;
        return true;
    }
}

internal sealed class LitString(string value) : Node
{
    public readonly string Value = value;

    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        var comparison = e.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!text.Slice(pos).StartsWith(Value, comparison)) return false;
        pos += Value.Length;
        return true;
    }
}

internal sealed class SkipNode : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        if (pos >= text.Length) return false;
        pos++;
        return true;
    }
}

internal sealed class EndNode : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos) => pos == text.Length;
}

internal sealed class BreakNode : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        e.Ctx.BreakRequested = true;
        return true;
    }
}

internal sealed class RejectNode : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        var ctx = e.Ctx;
        if (ctx.LoopDepth == 0) return false;
        ctx.RejectRequested = true;
        return true;
    }
}

internal sealed class CharTestNode(Func<char, bool> test) : Node
{
    public readonly Func<char, bool> Test = test;

    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
                if (pos >= text.Length || !Test(text[pos])) return false;
        pos++;
        return true;
    }
}

/// <summary>An <see cref="Item"/> without a char test, e.g. <see cref="P.Is"/>: the character is boxed for the predicate.</summary>
internal sealed class ItemNode(Func<object, bool> test) : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
                if (pos >= text.Length || !test(text[pos])) return false;
        pos++;
        return true;
    }
}

internal sealed class SeqNode(Node[] items) : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        var start = pos;
        foreach (var item in items)
        {
            if (item.Match(e, ref text, ref pos)) continue;
            pos = start;
            return false;
        }
        return true;
    }
}

internal sealed class AltNode(Node[] items) : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        foreach (var item in items)
            if (e.Try(item, ref text, ref pos)) return true;
        return false;
    }
}

/// <summary>A loop over a single character test, such as <c>some digit</c>: one tight loop, no per-iteration calls.</summary>
internal sealed class RepeatChar(Func<char, bool> test, int min, int max) : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
                var start = pos;
        var limit = max < 0 || max > text.Length - pos ? text.Length : pos + max;
        while (pos < limit && test(text[pos])) pos++;
        if (pos - start < min) { pos = start; return false; }
        return true;
    }
}

/// <summary>A loop whose body has no break/reject/keep/edit, so no flag handling is needed.</summary>
internal sealed class RepeatPure(Node body, int min, int max) : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        var start = pos;
        var count = 0;
        while (max < 0 || count < max)
        {
            var before = pos;
            if (!body.Match(e, ref text, ref pos)) break;
            count++;
            if (pos == before) break;   // zero-width match: stop to avoid looping forever
        }
        if (count < min) { pos = start; return false; }
        return true;
    }
}

internal sealed class RepeatNode(Node body, int min, int max) : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        var ctx = e.Ctx;
        var start = pos;
        var count = 0;
        var rejected = false;
        ctx.LoopDepth++;
        while (max < 0 || count < max)
        {
            var before = pos;
            var lengthBefore = text.Length;
            ctx.BreakRequested = false;   // drop a stale break from outside this loop
            ctx.RejectRequested = false;
            if (!e.Try(body, ref text, ref pos)) break;
            count++;
            if (ctx.RejectRequested) { ctx.RejectRequested = false; rejected = true; break; }
            if (ctx.BreakRequested) { ctx.BreakRequested = false; break; }
            if (pos == before && text.Length == lengthBefore) break;   // no progress: stop
        }
        ctx.LoopDepth--;
        if (rejected || count < min) { pos = start; return false; }
        return true;
    }
}

internal sealed class AheadNode(Node body) : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        var p = pos;
        return e.Try(body, ref text, ref p);
    }
}

internal sealed class NotNode(Node body) : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        var p = pos;
        return !e.Try(body, ref text, ref p);
    }
}

internal sealed class ScanNode(Node body, bool thru) : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        for (var p = pos; p <= text.Length; p++)
        {
            var q = p;
            if (!e.Try(body, ref text, ref q)) continue;
            pos = thru ? q : p;
            return true;
        }
        return false;
    }
}

internal sealed class ScanString(string value, bool thru) : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        var comparison = e.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var at = text.Slice(pos).IndexOf(value, comparison);
        if (at < 0) return false;
        pos += thru ? at + value.Length : at;
        return true;
    }
}

internal sealed class ScanChar(char value, bool thru) : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
                int at;
        if (!e.IgnoreCase) at = text.Slice(pos).IndexOf(value);
        else
        {
            var upper = char.ToUpperInvariant(value);
            at = -1;
            for (var p = pos; p < text.Length; p++)
                if (char.ToUpperInvariant(text[p]) == upper) { at = p - pos; break; }
        }
        if (at < 0) return false;
        pos += thru ? at + 1 : at;
        return true;
    }
}

internal sealed class ScanCharTest(Func<char, bool> test, bool thru) : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
                for (var p = pos; p < text.Length; p++)
        {
            if (!test(text[p])) continue;
            pos = thru ? p + 1 : p;
            return true;
        }
        return false;
    }
}

internal sealed class ScanEnd : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        pos = text.Length;
        return true;
    }
}

internal sealed class CopyNode(string var, Node body) : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        var start = pos;
        if (!body.Match(e, ref text, ref pos)) return false;
        e.Ctx.Vars[var] = text.Slice(start, pos - start).ToString();
        return true;
    }
}

internal sealed class SetNode(string var, Node body) : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        var start = pos;
        if (!body.Match(e, ref text, ref pos)) return false;
        e.Ctx.Vars[var] = pos > start ? text[start] : null;
        return true;
    }
}

internal sealed class CollectNode(string var, Node body) : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        var ctx = e.Ctx;
        var list = new List<object>();
        ctx.CollectedStack.Push(list);
        var ok = body.Match(e, ref text, ref pos);
        ctx.CollectedStack.Pop();
        if (!ok) return false;
        ctx.Vars[var] = list;
        return true;
    }
}

internal sealed class KeepNode(Node body) : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        var target = e.Ctx.KeepTarget();
        var start = pos;
        if (!body.Match(e, ref text, ref pos)) return false;
        target.Add(text.Slice(start, pos - start).ToString());
        return true;
    }
}

internal sealed class KeepPickNode(Node body) : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        var target = e.Ctx.KeepTarget();
        var start = pos;
        if (!body.Match(e, ref text, ref pos)) return false;
        if (pos > start) target.Add(text[start]);
        return true;
    }
}

internal sealed class KeepValueNode(Func<Ctx, object?> value) : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        var ctx = e.Ctx;
        var target = ctx.KeepTarget();
        ctx.Pos = pos;
        if (value(ctx) is { } kept) target.Add(kept);
        return true;
    }
}

internal sealed class IfNode(Func<Ctx, bool> condition) : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        e.Ctx.Pos = pos;
        return condition(e.Ctx);
    }
}

/// <summary>A named rule, looked up in the parse's rule table the first time it runs (and again if the table changes).</summary>
internal sealed class RefNode(string name) : Node
{
    private IReadOnlyDictionary<string, Rule>? lastRules;
    private Node? target;

    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        var rules = e.Ctx.Rules;
        if (!ReferenceEquals(rules, lastRules))
        {
            if (!rules.TryGetValue(name, out var rule)) throw new KeyNotFoundException($"Undefined rule '{name}'.");
            target = Compiler.Get(rule);
            lastRules = rules;
        }
        return e.Try(target!, ref text, ref pos);
    }
}

internal sealed class MarkNode(string name) : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        e.Ctx.MarksTable[name] = pos;
        return true;
    }
}

internal sealed class SeekNode(string name) : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        pos = e.Ctx.MarkAt(name);
        return true;
    }
}

internal sealed class ActNode(Action<Ctx> run) : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        var ctx = e.Ctx;
        ctx.Pos = pos;
        run(ctx);
        return true;
    }
}

internal sealed class InsertNode(Func<Ctx, object?> value) : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        pos += e.Insert(ref text, pos, value(e.Ctx));
        return true;
    }
}

internal sealed class ChangeNode(Node body, Func<Ctx, object?> value) : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        var start = pos;
        if (!body.Match(e, ref text, ref pos)) return false;
        e.Remove(ref text, start, pos);
        pos = start + e.Insert(ref text, start, value(e.Ctx));
        return true;
    }
}

internal sealed class RemoveNode(Node body) : Node
{
    public override bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)
    {
        var start = pos;
        if (!body.Match(e, ref text, ref pos)) return false;
        e.Remove(ref text, start, pos);
        pos = start;
        return true;
    }
}
