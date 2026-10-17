namespace RebolParse;

/// <summary>Fluent builders for the parse dialect. <c>using static RebolParse.P;</c> reads close to REBOL.</summary>
public static class P
{
    // --- Keywords ---
    public static readonly Rule Skip = new SkipRule();
    public static readonly Rule End = new EndRule();
    public static readonly Rule None = new Seq([]);
    public static readonly Rule Fail = new FailRule();
    public static readonly Rule Break = new BreakRule();
    public static readonly Rule Reject = new RejectRule();

    public static Rule Some(Rule r) => new Repeat(r, 1, -1);
    public static Rule Any(Rule r) => new Repeat(r, 0, -1);
    public static Rule Opt(Rule r) => new Repeat(r, 0, 1);
    public static Rule Rep(int n, Rule r) => new Repeat(r, n, n);
    public static Rule Rep(int min, int max, Rule r) => new Repeat(r, min, max);
    public static Rule Ahead(Rule r) => new Ahead(r);
    public static Rule Not(Rule r) => new Not(r);
    public static Rule To(Rule r) => new To(r);
    public static Rule Thru(Rule r) => new Thru(r);
    public static Rule Copy(string name, Rule r) => new Copy(name, r);
    public static Rule Set(string name, Rule r) => new Set(name, r);
    public static Rule Into(Rule r) => new Into(r);
    public static Rule Ref(string name) => new Ref(name);
    public static Rule Mark(string name) => new Mark(name);
    public static Rule Seek(string name) => new Seek(name);
    public static Rule Do(Action<Ctx> action) => new Act(action);
    public static Rule Insert(object? value) => new Insert(_ => value);
    public static Rule Insert(Func<Ctx, object?> value) => new Insert(value);
    public static Rule Change(Rule r, object? value) => new Change(r, _ => value);
    public static Rule Change(Rule r, Func<Ctx, object?> value) => new Change(r, value);
    public static Rule Remove(Rule r) => new Remove(r);
    public static Rule Collect(string name, Rule r) => new Collect(name, r);
    public static Rule Keep(Rule r) => new Keep(r);
    public static Rule KeepPick(Rule r) => new KeepPick(r);
    public static Rule Keep(Func<Ctx, object?> value) => new KeepValue(value);
    public static Rule If(Func<Ctx, bool> condition) => new If(condition);
    public static Rule Lit(object value) => new Lit(value);
    public static Rule Choice(params Rule[] alts) => new Alt(alts);
    public static Rule Seq(params Rule[] items) => new Seq(items);

    // --- Character sets (string mode) ---
    public static Rule CharIf(Func<char, bool> test, string name = "char") =>
        new Item(o => o is char c && test(c), name, test);
    public static Rule CharIn(string chars) => CharIf(chars.Contains, $"[{chars}]");
    public static Rule CharRange(char lo, char hi) => CharIf(c => c >= lo && c <= hi, $"{lo}-{hi}");

    public static readonly Rule Digit = CharIf(char.IsAsciiDigit, "digit");
    public static readonly Rule Alpha = CharIf(char.IsLetter, "alpha");
    public static readonly Rule AlphaNum = CharIf(char.IsLetterOrDigit, "alnum");
    public static readonly Rule Upper = CharIf(char.IsUpper, "upper");
    public static readonly Rule Lower = CharIf(char.IsLower, "lower");
    public static readonly Rule Space = CharIf(char.IsWhiteSpace, "space");
    public static readonly Rule Newline = new Lit('\n');
    public static readonly Rule Tab = new Lit('\t');

    // --- Datatype tests (block mode) ---
    public static Rule Is(Func<object, bool> test, string name = "item") => new Item(test, name);
    public static readonly Rule Integer = new Item(o => o is int or long, "integer!");
    public static readonly Rule Decimal = new Item(o => o is double or float or decimal, "decimal!");
    public static readonly Rule Str = new Item(o => o is string, "string!");
    public static readonly Rule WordType = new Item(o => o is Word, "word!");
    public static readonly Rule BlockType = new Item(o => o is IReadOnlyList<object>, "block!");
}
