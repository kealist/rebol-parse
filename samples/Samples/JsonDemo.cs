using System.Globalization;
using System.Text;
using RebolParse;
using static RebolParse.P;

namespace Samples;

/// <summary>
/// A complete JSON reader. The grammar is the spec; actions build the values on a stack, with a marker pushed
/// when an array or object opens so the closing action knows how much to collect. Values are null, bool, double,
/// string, <c>List&lt;object?&gt;</c> and <c>Dictionary&lt;string, object?&gt;</c>.
/// </summary>
public sealed class JsonDemo : IDemo
{
    public string Name => "json";
    public string Title => "JSON: a full reader, a writer, and path queries";

    private static readonly object Marker = new();
    private readonly List<object?> stack = [];
    private readonly Dictionary<string, Rule> rules;

    /// <param name="compiled">Compile the grammar to IL (the default). Pass false to run it on the interpreter, for comparison.</param>
    public JsonDemo(bool compiled = true)
    {
        var actions = new Dictionary<string, Action<Ctx>>
        {
            ["open"] = _ => stack.Add(Marker),
            ["string"] = c => stack.Add(Unescape((string)c.Vars["s"]!)),
            ["number"] = c => stack.Add(double.Parse((string)c.Vars["n"]!, CultureInfo.InvariantCulture)),
            ["true"] = _ => stack.Add(true),
            ["false"] = _ => stack.Add(false),
            ["null"] = _ => stack.Add(null),
            ["array"] = _ => stack.Add(TakeItems()),
            ["object"] = _ =>
            {
                var items = TakeItems();
                var o = new Dictionary<string, object?>();
                for (var i = 0; i < items.Count; i += 2) o[(string)items[i]!] = items[i + 1];
                stack.Add(o);
            },
        };
        var grammar = Dialect.ReadGrammar("""
            json   = ws value ws end
            ws     = any space
            value  = object | array | string | number
                   | "true" (@true) | "false" (@false) | "null" (@null)
            array  = "[" (@open) ws opt [value ws any ["," ws value ws]] "]" (@array)
            object = "{" (@open) ws opt [member ws any ["," ws member ws]] "}" (@object)
            member = string ws ":" ws value
            string = "^"" copy s any [some [not ["^"" | "\"] skip] | "\" skip] "^"" (@string)
            number = copy n [opt "-" ["0" | some digit] opt ["." some digit]
                             opt [charset "eE" opt charset "+-" some digit]] (@number)
            """, actions);
        rules = compiled ? grammar.Compiled() : grammar;
    }

    private List<object?> TakeItems()
    {
        var at = stack.LastIndexOf(Marker);
        var items = stack.GetRange(at + 1, stack.Count - at - 1);
        stack.RemoveRange(at, stack.Count - at);
        return items;
    }

    /// <summary>Read a JSON document. Throws <see cref="FormatException"/> with the position where reading stopped.</summary>
    public object? Read(string json)
    {
        stack.Clear();
        if (!Parser.Parse(json, Ref("json"), rules).Matched)
        {
            stack.Clear();
            var at = Parser.Parse(json, Ref("value"), rules, partial: true).Position;
            throw new FormatException($"invalid JSON near position {at}");
        }
        return stack[0];
    }

    private static string Unescape(string raw)
    {
        if (!raw.Contains('\\')) return raw;
        var sb = new StringBuilder();
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '\\') { sb.Append(raw[i]); continue; }
            i++;
            switch (raw[i])
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u': sb.Append((char)Convert.ToInt32(raw.Substring(i + 1, 4), 16)); i += 4; break;
                default: sb.Append(raw[i]); break;   // \" \\ \/
            }
        }
        return sb.ToString();
    }

    // ---- writer ----

    public static string Compact(object? v) => v switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        double d => Number(d),
        string s => Quote(s),
        List<object?> l => "[" + string.Join(",", l.Select(Compact)) + "]",
        Dictionary<string, object?> o => "{" + string.Join(",", o.Select(kv => Quote(kv.Key) + ":" + Compact(kv.Value))) + "}",
        _ => throw new ArgumentException($"Cannot write {v.GetType().Name}."),
    };

    public static string Pretty(object? v, int indent = 0)
    {
        var pad = new string(' ', indent + 2);
        var end = new string(' ', indent);
        return v switch
        {
            List<object?> { Count: > 0 } l => "[\n" + string.Join(",\n", l.Select(x => pad + Pretty(x, indent + 2))) + "\n" + end + "]",
            Dictionary<string, object?> { Count: > 0 } o =>
                "{\n" + string.Join(",\n", o.Select(kv => pad + Quote(kv.Key) + ": " + Pretty(kv.Value, indent + 2))) + "\n" + end + "}",
            _ => Compact(v),
        };
    }

    private static string Number(double d) =>
        d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture);

    private static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
            sb.Append(c switch
            {
                '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                < ' ' => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        return sb.Append('"').ToString();
    }

    /// <summary>Follow a path like <c>author.name</c> or <c>tags[1]</c>. Returns null when any step is missing.</summary>
    public static object? Get(object? root, string path)
    {
        var step = Dialect.Read("any [copy key some [alnum | \"_\"] (@key) | \".\" | \"[\" copy index some digit \"]\" (@index)]",
            new Dictionary<string, Action<Ctx>>
            {
                ["key"] = c => root = root is Dictionary<string, object?> o && o.TryGetValue((string)c.Vars["key"]!, out var x) ? x : null,
                ["index"] = c => root = root is List<object?> l && int.Parse((string)c.Vars["index"]!) is var i && i < l.Count ? l[i] : null,
            });
        return Parser.Parse(path, step).Matched ? root : null;
    }

    private const string Sample = """
        {
          "name": "rebol-parse",
          "version": 1.5,
          "tags": ["parser", "dsl", "rebol"],
          "author": {"name": "joshua", "active": true, "email": null},
          "scores": [10, 20.5, -3e2, 0],
          "quote": "He said \"hi\"\nthen left é",
          "empty": {"list": [], "map": {}}
        }
        """;

    public void Run(Report r)
    {
        var doc = Read(Sample);
        r.Say(Pretty(doc));
        r.Expect("author.name", Get(doc, "author.name"), "joshua");
        r.Expect("author.active", Get(doc, "author.active"), true);
        r.Expect("author.email is null", Get(doc, "author.email") is null);
        r.Expect("tags[1]", Get(doc, "tags[1]"), "dsl");
        r.Expect("scores[1]", Get(doc, "scores[1]"), 20.5);
        r.Expect("scores[2] reads an exponent", Get(doc, "scores[2]"), -300.0);
        r.Expect("quote reads escapes", Get(doc, "quote"), "He said \"hi\"\nthen left é");
        r.Expect("a missing path is null", Get(doc, "author.age") is null);
        r.Expect("an out-of-range index is null", Get(doc, "tags[9]") is null);

        var compact = Compact(doc);
        r.Expect("write then read gives the same document", Compact(Read(compact)), compact);
        r.Expect("pretty output reads back the same", Compact(Read(Pretty(doc))), compact);
        r.Expect("scalars", Compact(Read(" [1, 2.5, \"x\", true, false, null] ")), "[1,2.5,\"x\",true,false,null]");

        string[] bad = ["[1,]", "{\"a\":}", "[1 2]", "{\"a\" 1}", "[", "\"abc", "01", "tru", "{\"a\":1,}", "-", "[1,,2]", ""];
        foreach (var text in bad)
            try { Read(text); r.Expect($"{text,-10} is rejected", false); }
            catch (FormatException) { r.Expect($"{text,-10} is rejected", true); }
        try { Read("{\"a\": [1, 2,, 3]}"); }
        catch (FormatException ex) { r.Say(ex.Message); }

        // a bigger document: 2,000 records
        var big = "[" + string.Join(",", Enumerable.Range(0, 2000).Select(i => $"{{\"id\":{i},\"name\":\"item {i}\",\"tags\":[\"a\",\"b\"],\"ok\":{(i % 2 == 0 ? "true" : "false")}}}")) + "]";
        var items = (List<object?>)Read(big)!;
        r.Expect("2,000 records read", items.Count, 2000);
        r.Expect("record 1999 name", Get(items, "[1999].name"), "item 1999");
        r.Expect("half the records are ok", items.Count(x => (bool)((Dictionary<string, object?>)x!)["ok"]!), 1000);
    }
}
