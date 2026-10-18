namespace RebolParse;

/// <summary>
/// Fluent builders for the parse dialect. <c>using static RebolParse.P;</c> makes rules read close to REBOL:
/// <c>Some(Digit) + "." + Some(Digit)</c>.
/// </summary>
public static class P
{
    // --- Keywords ---

    /// <summary>Matches any one item (<c>skip</c>).</summary>
    public static readonly Rule Skip = new SkipRule();

    /// <summary>Matches only at the end of the input (<c>end</c>).</summary>
    public static readonly Rule End = new EndRule();

    /// <summary>Always matches, consuming nothing (<c>none</c>).</summary>
    public static readonly Rule None = new Seq([]);

    /// <summary>Never matches (<c>fail</c>).</summary>
    public static readonly Rule Fail = new FailRule();

    /// <summary>Leaves the enclosing loop, successfully (<c>break</c>).</summary>
    public static readonly Rule Break = new BreakRule();

    /// <summary>Leaves the enclosing loop, failing it (<c>reject</c>). Outside a loop it is plain failure.</summary>
    public static readonly Rule Reject = new RejectRule();

    /// <summary>One or more repetitions (<c>some</c>).</summary>
    public static Rule Some(Rule r) => new Repeat(r, 1, -1);

    /// <summary>Zero or more repetitions (<c>any</c>).</summary>
    public static Rule Any(Rule r) => new Repeat(r, 0, -1);

    /// <summary>Zero or one occurrence (<c>opt</c>).</summary>
    public static Rule Opt(Rule r) => new Repeat(r, 0, 1);

    /// <summary>Exactly <paramref name="n"/> repetitions (<c>3 r</c>).</summary>
    public static Rule Rep(int n, Rule r) => new Repeat(r, n, n);

    /// <summary>Between <paramref name="min"/> and <paramref name="max"/> repetitions (<c>2 4 r</c>).</summary>
    public static Rule Rep(int min, int max, Rule r) => new Repeat(r, min, max);

    /// <summary>Succeeds if <paramref name="r"/> matches here, consuming nothing (<c>ahead</c>).</summary>
    public static Rule Ahead(Rule r) => new Ahead(r);

    /// <summary>Succeeds if <paramref name="r"/> does not match here, consuming nothing (<c>not</c>).</summary>
    public static Rule Not(Rule r) => new Not(r);

    /// <summary>Skips to where <paramref name="r"/> matches without consuming it (<c>to</c>).</summary>
    public static Rule To(Rule r) => new To(r);

    /// <summary>Skips to where <paramref name="r"/> matches and consumes it (<c>thru</c>).</summary>
    public static Rule Thru(Rule r) => new Thru(r);

    /// <summary>Captures what <paramref name="r"/> matches into <c>Vars[name]</c> (<c>copy</c>).</summary>
    public static Rule Copy(string name, Rule r) => new Copy(name, r);

    /// <summary>Captures the first item <paramref name="r"/> matches into <c>Vars[name]</c> (<c>set</c>).</summary>
    public static Rule Set(string name, Rule r) => new Set(name, r);

    /// <summary>Matches <paramref name="r"/> against the nested list at this position (<c>into</c>). Block input only.</summary>
    public static Rule Into(Rule r) => new Into(r);

    /// <summary>Refers to a named rule in the rule table given to <c>Parse</c>; this is how rules recurse.</summary>
    public static Rule Ref(string name) => new Ref(name);

    /// <summary>Saves the current position as <paramref name="name"/> (<c>name:</c>).</summary>
    public static Rule Mark(string name) => new Mark(name);

    /// <summary>Jumps to the position saved as <paramref name="name"/> (<c>:name</c>).</summary>
    public static Rule Seek(string name) => new Seek(name);

    /// <summary>Runs <paramref name="action"/> when the match reaches this point (<c>(@name)</c> in text).</summary>
    public static Rule Do(Action<Ctx> action) => new Act(action);

    /// <summary>Inserts a fixed value at the current position and moves past it (<c>insert</c>).</summary>
    public static Rule Insert(object? value) => new Insert(_ => value);

    /// <summary>Inserts a computed value at the current position and moves past it (<c>insert</c>).</summary>
    public static Rule Insert(Func<Ctx, object?> value) => new Insert(value);

    /// <summary>Replaces what <paramref name="r"/> matches with a fixed value (<c>change</c>).</summary>
    public static Rule Change(Rule r, object? value) => new Change(r, _ => value);

    /// <summary>Replaces what <paramref name="r"/> matches with a computed value (<c>change</c>).</summary>
    public static Rule Change(Rule r, Func<Ctx, object?> value) => new Change(r, value);

    /// <summary>Deletes what <paramref name="r"/> matches (<c>remove</c>).</summary>
    public static Rule Remove(Rule r) => new Remove(r);

    /// <summary>Runs <paramref name="r"/> and stores what it keeps as a list in <c>Vars[name]</c> (<c>collect</c>).</summary>
    public static Rule Collect(string name, Rule r) => new Collect(name, r);

    /// <summary>Matches <paramref name="r"/> and adds the match to the enclosing <see cref="Collect"/> (<c>keep</c>).</summary>
    public static Rule Keep(Rule r) => new Keep(r);

    /// <summary>Matches <paramref name="r"/> and adds only its first item to the enclosing <see cref="Collect"/> (<c>keep pick</c>).</summary>
    public static Rule KeepPick(Rule r) => new KeepPick(r);

    /// <summary>Adds a computed value to the enclosing <see cref="Collect"/> without consuming input; <c>null</c> adds nothing (<c>keep (@fn)</c>).</summary>
    public static Rule Keep(Func<Ctx, object?> value) => new KeepValue(value);

    /// <summary>Succeeds, consuming nothing, when <paramref name="condition"/> returns true (<c>if (@fn)</c>).</summary>
    public static Rule If(Func<Ctx, bool> condition) => new If(condition);

    /// <summary>Matches a literal value: a string or char in text, any equal item in a block (<c>quote</c>).</summary>
    public static Rule Lit(object value) => new Lit(value);

    /// <summary>Ordered choice among any number of rules. Unlike <c>|</c>, it also works on plain strings.</summary>
    public static Rule Choice(params Rule[] alts) => new Alt(alts);

    /// <summary>A sequence of any number of rules. Unlike <c>+</c>, it also works on plain strings.</summary>
    public static Rule Seq(params Rule[] items) => new Seq(items);

    // --- Character sets (string mode) ---

    /// <summary>Matches one character satisfying <paramref name="test"/>. Text is tested without boxing the character.</summary>
    /// <param name="test">The character predicate.</param>
    /// <param name="name">A name for diagnostics.</param>
    public static Rule CharIf(Func<char, bool> test, string name = "char") =>
        new Item(o => o is char c && test(c), name, test);

    /// <summary>Matches one character that appears in <paramref name="chars"/>.</summary>
    public static Rule CharIn(string chars) => CharIf(chars.Contains, $"[{chars}]");

    /// <summary>
    /// Matches one character from a set written like a regex class: <c>Chars("a-zA-Z_")</c>. A <c>-</c> between
    /// two characters is a range; first or last it is a hyphen. In rule text this is <c>charset "a-zA-Z_"</c>.
    /// </summary>
    public static Rule Chars(string spec) => CharIf(CharSet.Parse(spec).Contains, $"charset \"{spec}\"");

    /// <summary>Matches one character from <paramref name="lo"/> to <paramref name="hi"/>, inclusive.</summary>
    public static Rule CharRange(char lo, char hi) => CharIf(c => c >= lo && c <= hi, $"{lo}-{hi}");

    /// <summary>An ASCII digit, <c>0</c> to <c>9</c> (<c>digit</c>).</summary>
    public static readonly Rule Digit = CharIf(char.IsAsciiDigit, "digit");

    /// <summary>A letter, in any script (<c>alpha</c>).</summary>
    public static readonly Rule Alpha = CharIf(char.IsLetter, "alpha");

    /// <summary>A letter or digit (<c>alnum</c>).</summary>
    public static readonly Rule AlphaNum = CharIf(char.IsLetterOrDigit, "alnum");

    /// <summary>An uppercase letter (<c>upper</c>). Stays case-sensitive under <c>ignoreCase</c>.</summary>
    public static readonly Rule Upper = CharIf(char.IsUpper, "upper");

    /// <summary>A lowercase letter (<c>lower</c>). Stays case-sensitive under <c>ignoreCase</c>.</summary>
    public static readonly Rule Lower = CharIf(char.IsLower, "lower");

    /// <summary>Any whitespace character, including newlines (<c>space</c>).</summary>
    public static readonly Rule Space = CharIf(char.IsWhiteSpace, "space");

    /// <summary>A line feed, <c>\n</c> (<c>newline</c>).</summary>
    public static readonly Rule Newline = new Lit('\n');

    /// <summary>A tab, <c>\t</c> (<c>tab</c>).</summary>
    public static readonly Rule Tab = new Lit('\t');

    // --- Datatype tests (block mode) ---

    /// <summary>Matches one item satisfying <paramref name="test"/>. In text each character is boxed for the test; prefer <see cref="CharIf"/> there.</summary>
    /// <param name="test">The item predicate.</param>
    /// <param name="name">A name for diagnostics.</param>
    public static Rule Is(Func<object, bool> test, string name = "item") => new Item(test, name);

    /// <summary>An <see cref="int"/> or <see cref="long"/> item (<c>integer!</c>).</summary>
    public static readonly Rule Integer = new Item(o => o is int or long, "integer!");

    /// <summary>A <see cref="double"/>, <see cref="float"/> or <see cref="decimal"/> item (<c>decimal!</c>).</summary>
    public static readonly Rule Decimal = new Item(o => o is double or float or decimal, "decimal!");

    /// <summary>A <see cref="string"/> item (<c>string!</c>).</summary>
    public static readonly Rule Str = new Item(o => o is string, "string!");

    /// <summary>A <see cref="Word"/> item (<c>word!</c>).</summary>
    public static readonly Rule WordType = new Item(o => o is Word, "word!");

    /// <summary>A nested list item (<c>block!</c>).</summary>
    public static readonly Rule BlockType = new Item(o => o is IReadOnlyList<object>, "block!");
}
