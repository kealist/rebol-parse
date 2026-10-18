using System.Text;

namespace RebolParse;

/// <summary>
/// Reads REBOL-style parse rule text into a <see cref="Rule"/> tree.
/// <code>Dialect.Read("some [digit | #\"x\"] copy rest to end")</code>
/// Paren actions are bound by name: <c>(@log)</c> calls <c>actions["log"]</c>.
/// <c>keep (@name)</c> and <c>if (@name)</c> call <c>values["name"]</c>, a function of the parse context.
/// Bare words that are not keywords or built-in sets become <see cref="Ref"/>s into the rule table.
/// </summary>
public static class Dialect
{
    private static readonly Dictionary<string, Rule> Builtins = new(StringComparer.OrdinalIgnoreCase)
    {
        ["skip"] = P.Skip, ["end"] = P.End, ["none"] = P.None, ["fail"] = P.Fail, ["break"] = P.Break, ["reject"] = P.Reject,
        ["digit"] = P.Digit, ["alpha"] = P.Alpha, ["alnum"] = P.AlphaNum, ["upper"] = P.Upper,
        ["lower"] = P.Lower, ["space"] = P.Space, ["newline"] = P.Newline, ["tab"] = P.Tab,
        ["integer!"] = P.Integer, ["decimal!"] = P.Decimal, ["string!"] = P.Str,
        ["word!"] = P.WordType, ["block!"] = P.BlockType,
    };

    /// <summary>
    /// Read a rule from REBOL-style text. The outer brackets are optional: <c>a b | c</c> and <c>[a b | c]</c> are
    /// equivalent. See docs/Reference.md for the full syntax.
    /// </summary>
    /// <param name="text">The rule text.</param>
    /// <param name="actions">Named actions for <c>(@name)</c>.</param>
    /// <param name="values">Named value functions for <c>keep (@name)</c> and <c>if (@name)</c>.</param>
    /// <exception cref="FormatException">The text is malformed.</exception>
    /// <exception cref="KeyNotFoundException">A <c>(@name)</c> has no entry in <paramref name="actions"/> or <paramref name="values"/>.</exception>
    public static Rule Read(string text, IReadOnlyDictionary<string, Action<Ctx>>? actions = null,
        IReadOnlyDictionary<string, Func<Ctx, object?>>? values = null)
    {
        var bindings = new Bindings(actions, values);
        var tokens = Tokenize(text);
        var i = 0;
        return ReadAlt(tokens, ref i, bindings, closer: null); // a stray ']' throws inside ReadAlt
    }

    /// <summary>Read several named rules, e.g. for recursive grammars. Pass the result as the <c>rules</c> argument of <c>Parse</c>.</summary>
    /// <param name="defs">Rule names mapped to rule text.</param>
    /// <param name="actions">Named actions for <c>(@name)</c>.</param>
    /// <param name="values">Named value functions for <c>keep (@name)</c> and <c>if (@name)</c>.</param>
    public static Dictionary<string, Rule> ReadRules(
        IEnumerable<KeyValuePair<string, string>> defs, IReadOnlyDictionary<string, Action<Ctx>>? actions = null,
        IReadOnlyDictionary<string, Func<Ctx, object?>>? values = null) =>
        defs.ToDictionary(d => d.Key, d => Read(d.Value, actions, values));

    // ---- Reader ----

    private static Rule ReadAlt(List<Token> t, ref int i, Bindings acts, string? closer)
    {
        var alts = new List<Rule>();
        var seq = new List<Rule>();
        void Flush() { alts.Add(seq.Count == 1 ? seq[0] : new Seq([.. seq])); seq.Clear(); }

        while (i < t.Count)
        {
            var tok = t[i];
            if (tok.Kind == K.Close)
            {
                if (closer == null) throw new FormatException("Unmatched ']'.");
                break;
            }
            if (tok.Kind == K.Bar) { i++; Flush(); continue; }
            seq.Add(ReadItem(t, ref i, acts));
        }
        if (closer != null && (i >= t.Count || t[i].Kind != K.Close)) throw new FormatException("Missing ']'.");
        Flush();
        return alts.Count == 1 ? alts[0] : new Alt([.. alts]);
    }

    private static Rule ReadItem(List<Token> t, ref int i, Bindings acts)
    {
        if (i >= t.Count) throw new FormatException("Rule ended where an item was expected.");
        var tok = t[i++];
        switch (tok.Kind)
        {
            case K.Open:
                var inner = ReadAlt(t, ref i, acts, closer: "]");
                i++; // consume ]
                return inner;
            case K.Str: return new Lit(tok.Text);
            case K.Char: return new Lit(tok.Text[0]);
            case K.Paren: return BindAction(tok.Text, acts);
            case K.SetWord: return new Mark(tok.Text);
            case K.GetWord: return new Seek(tok.Text);
            case K.Int:
                // "n rule" or "n m rule"
                var min = int.Parse(tok.Text);
                var max = min;
                if (i < t.Count && t[i].Kind == K.Int) max = int.Parse(t[i++].Text);
                return new Repeat(ReadItem(t, ref i, acts), min, max);
            case K.Word:
                return ReadWord(tok.Text, t, ref i, acts);
            default:
                throw new FormatException($"Unexpected '{tok.Text}'.");
        }
    }

    private static Rule ReadWord(string w, List<Token> t, ref int i, Bindings acts)
    {
        switch (w.ToLowerInvariant())
        {
            case "some": return new Repeat(ReadItem(t, ref i, acts), 1, -1);
            case "any": return new Repeat(ReadItem(t, ref i, acts), 0, -1);
            case "opt": return new Repeat(ReadItem(t, ref i, acts), 0, 1);
            case "ahead": return new Ahead(ReadItem(t, ref i, acts));
            case "not": return new Not(ReadItem(t, ref i, acts));
            case "to": return new To(ReadItem(t, ref i, acts));
            case "thru": return new Thru(ReadItem(t, ref i, acts));
            case "into": return new Into(ReadItem(t, ref i, acts));
            case "quote": return ReadQuoted(t, ref i);
            case "copy": return new Copy(ReadVarName(t, ref i, "copy"), ReadItem(t, ref i, acts));
            case "set": return new Set(ReadVarName(t, ref i, "set"), ReadItem(t, ref i, acts));
            case "insert": return new Insert(ReadValue(t, ref i, "insert"));
            case "remove": return new Remove(ReadItem(t, ref i, acts));
            case "keep":
                if (i < t.Count && t[i].Kind == K.Paren) return new KeepValue(BindValue(t[i++].Text, acts));
                if (i < t.Count && t[i].Kind == K.Word && t[i].Text.Equals("pick", StringComparison.OrdinalIgnoreCase))
                {
                    i++;
                    return new KeepPick(ReadItem(t, ref i, acts));
                }
                return new Keep(ReadItem(t, ref i, acts));
            case "if":
                if (i >= t.Count || t[i].Kind != K.Paren) throw new FormatException("'if' needs a condition like (@name).");
                var cond = BindValue(t[i++].Text, acts);
                return new If(ctx => cond(ctx) switch { bool b => b, null => false, _ => true });
            case "collect": return new Collect(ReadVarName(t, ref i, "collect"), ReadItem(t, ref i, acts));
            case "change":
                var body = ReadItem(t, ref i, acts);
                return new Change(body, ReadValue(t, ref i, "change"));
        }
        return Builtins.TryGetValue(w, out var b) ? b : new Ref(w);
    }

    private static Rule ReadQuoted(List<Token> t, ref int i)
    {
        if (i >= t.Count) throw new FormatException("'quote' needs a value.");
        var v = t[i++];
        return v.Kind switch
        {
            K.Str => new Lit(v.Text),
            K.Char => new Lit(v.Text[0]),
            K.Int => new Lit(int.Parse(v.Text)),
            K.Word => new Lit(new Word(v.Text)),
            _ => throw new FormatException($"'quote' cannot take '{v.Text}'."),
        };
    }

    /// <summary>A literal (string, char, integer) or a word naming a captured variable, read when the rule runs.</summary>
    private static Func<Ctx, object?> ReadValue(List<Token> t, ref int i, string kw)
    {
        if (i >= t.Count) throw new FormatException($"'{kw}' needs a value.");
        var v = t[i++];
        switch (v.Kind)
        {
            case K.Str: { var str = v.Text; return _ => str; }
            case K.Char: { var ch = v.Text[0]; return _ => ch; }
            case K.Int: { var n = int.Parse(v.Text); return _ => n; }
            case K.Word:
                var name = v.Text;
                return ctx => ctx.Vars.TryGetValue(name, out var val)
                    ? val
                    : throw new KeyNotFoundException($"Undefined variable '{name}'.");
            default: throw new FormatException($"'{kw}' cannot take '{v.Text}'.");
        }
    }

    private static string ReadVarName(List<Token> t, ref int i, string kw)
    {
        if (i >= t.Count || t[i].Kind != K.Word) throw new FormatException($"'{kw}' needs a variable name.");
        return t[i++].Text;
    }

    private static Func<Ctx, object?> BindValue(string body, Bindings acts)
    {
        body = body.Trim();
        if (!body.StartsWith('@'))
            throw new FormatException($"Paren code '({body})' is not evaluated; bind a named function with '(@name)'.");
        var name = body[1..];
        if (acts.Values == null || !acts.Values.TryGetValue(name, out var fn))
            throw new KeyNotFoundException($"No value function registered for '(@{name})'.");
        return fn;
    }

    private static Rule BindAction(string body, Bindings acts)
    {
        body = body.Trim();
        if (!body.StartsWith('@'))
            throw new FormatException($"Paren code '({body})' is not evaluated; bind a named action with '(@name)'.");
        var name = body[1..];
        if (acts.Actions == null || !acts.Actions.TryGetValue(name, out var act))
            throw new KeyNotFoundException($"No action registered for '(@{name})'.");
        return new Act(act);
    }

    // ---- Tokenizer ----

    private readonly record struct Bindings(
        IReadOnlyDictionary<string, Action<Ctx>>? Actions, IReadOnlyDictionary<string, Func<Ctx, object?>>? Values);

    private enum K { Open, Close, Bar, Str, Char, Int, Word, SetWord, GetWord, Paren }
    private readonly record struct Token(K Kind, string Text);

    private static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == ';') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (c == '[') { tokens.Add(new(K.Open, "[")); i++; continue; }
            if (c == ']') { tokens.Add(new(K.Close, "]")); i++; continue; }
            if (c == '|') { tokens.Add(new(K.Bar, "|")); i++; continue; }
            if (c == '"') { tokens.Add(new(K.Str, ReadString(s, ref i))); continue; }
            if (c == '#' && i + 1 < s.Length && s[i + 1] == '"')
            {
                i++;
                var ch = ReadString(s, ref i);
                if (ch.Length != 1) throw new FormatException($"Char literal #\"{ch}\" must be one character.");
                tokens.Add(new(K.Char, ch));
                continue;
            }
            if (c == '(')
            {
                var depth = 0;
                var start = ++i;
                for (; i < s.Length; i++)
                {
                    if (s[i] == '(') depth++;
                    else if (s[i] == ')' && depth-- == 0) break;
                }
                if (i >= s.Length) throw new FormatException("Missing ')'.");
                tokens.Add(new(K.Paren, s[start..i]));
                i++;
                continue;
            }
            var wStart = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && "[]|\"();".IndexOf(s[i]) < 0) i++;
            var word = s[wStart..i];
            if (word.All(char.IsAsciiDigit)) tokens.Add(new(K.Int, word));
            else if (word.StartsWith(':') && word.Length > 1) tokens.Add(new(K.GetWord, word[1..]));
            else if (word.EndsWith(':') && word.Length > 1) tokens.Add(new(K.SetWord, word[..^1]));
            else tokens.Add(new(K.Word, word));
        }
        return tokens;
    }

    /// <summary>Reads a quoted string at s[i] == '"'. Escapes use REBOL's caret: ^" ^^ ^/ ^-.</summary>
    private static string ReadString(string s, ref int i)
    {
        var sb = new StringBuilder();
        i++; // opening quote
        while (i < s.Length && s[i] != '"')
        {
            if (s[i] == '^' && i + 1 < s.Length)
            {
                i++;
                sb.Append(s[i] switch { '/' => '\n', '-' => '\t', var o => o });
            }
            else sb.Append(s[i]);
            i++;
        }
        if (i >= s.Length) throw new FormatException("Unterminated string.");
        i++; // closing quote
        return sb.ToString();
    }
}
