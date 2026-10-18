using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;
using Samples;

namespace RebolParse.Benchmarks;

/// <summary>
/// Pull three properties out of a JSON document: <c>System.Text.Json</c> (document, bytes, node tree) against
/// RebolParse reading the whole document into objects, and against RebolParse rules that skip straight to
/// the property and copy only its text.
/// </summary>
[MemoryDiagnoser]
public class JsonLookup
{
    private const string SmallDoc = """
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

    private readonly record struct Step(string? Key, int Index);

    private static readonly Dictionary<string, Rule> SkipRules = Dialect.ReadGrammar("""
        ws     = any space
        value  = object | array | string | number | "true" | "false" | "null"
        array  = "[" ws opt [value ws any ["," ws value ws]] "]"
        object = "{" ws opt [member ws any ["," ws member ws]] "}"
        member = string ws ":" ws value
        string = "^"" any [some [not ["^"" | "\"] skip] | "\" skip] "^""
        number = opt "-" ["0" | some digit] opt ["." some digit] opt [charset "eE" opt charset "+-" some digit]
        """);

    private readonly JsonDemo reader = new(compiled: false);
    private readonly JsonDemo readerIl = new(compiled: true);
    private string json = "";
    private byte[] utf8 = [];
    private string[] paths = [];
    private Step[][] steps = [];
    private Rule[] targeted = [];
    private Rule[] targetedIl = [];
    private static readonly Dictionary<string, Rule> SkipRulesIl = SkipRules.Compiled();

    /// <summary>
    /// Small is a 400-byte document. Large is 2,000 records, about 150 KB, read at the start, middle and end.
    /// LargeEarly is the same document but reads only properties near the start, where stopping early pays.
    /// Huge and HugeEarly are the same shapes with 200,000 records, about 11 MB.
    /// </summary>
    [Params("Small", "Large", "LargeEarly", "Huge", "HugeEarly")] public string Size = "Small";

    [GlobalSetup]
    public void Setup()
    {
        if (Size == "Small")
        {
            json = SmallDoc;
            paths = ["author.name", "tags[1]", "version"];
        }
        else
        {
            var records = Size.StartsWith("Huge") ? 200_000 : 2000;
            json = "[" + string.Join(",", Enumerable.Range(0, records).Select(i =>
                $"{{\"id\":{i},\"name\":\"item {i}\",\"tags\":[\"a\",\"b\"],\"ok\":{(i % 2 == 0 ? "true" : "false")}}}")) + "]";
            paths = Size.EndsWith("Early") ? ["[0].name", "[1].tags[1]", "[2].id"]
                : [$"[0].name", $"[{records / 2}].tags[1]", $"[{records - 1}].id"];
        }
        utf8 = Encoding.UTF8.GetBytes(json);
        steps = paths.Select(ParsePath).ToArray();
        targeted = steps.Select(BuildRule).ToArray();
        targetedIl = targeted.Select(r => r.Compiled()).ToArray();

        var expected = JsonDocumentString();
        Guard.Same("JsonDocument bytes", JsonDocumentBytes(), expected);
        Guard.Same("JsonNode", JsonNodeTree(), expected);
        Guard.Same("RebolParse tree", RebolParseTree(), expected);
        Guard.Same("RebolParse tree IL", RebolParseTreeIl(), expected);
        Guard.Same("RebolParse targeted", RebolParseTargeted(), expected);
        Guard.Same("RebolParse targeted IL", RebolParseTargetedIl(), expected);
    }

    // "author.name" and "tags[1]" as steps
    private static Step[] ParsePath(string path)
    {
        var list = new List<Step>();
        foreach (var part in path.Split('.'))
        {
            var bracket = part.IndexOf('[');
            var key = bracket < 0 ? part : part[..bracket];
            if (key.Length > 0) list.Add(new Step(key, 0));
            for (var at = bracket; at >= 0 && at < part.Length; at = part.IndexOf('[', at + 1))
                list.Add(new Step(null, int.Parse(part[(at + 1)..part.IndexOf(']', at)])));
        }
        return [.. list];
    }

    // a rule that skips to the property and copies its text into r
    private static Rule BuildRule(Step[] path)
    {
        var text = new StringBuilder("ws ");
        foreach (var step in path)
            text.Append(step.Key is { } key
                ? $"\"{{\" ws any [not [\"^\"{key}^\"\" ws \":\"] member ws \",\" ws] \"^\"{key}^\"\" ws \":\" ws "
                : $"\"[\" ws {step.Index} [value ws \",\" ws] ");
        text.Append("copy r value");
        return Dialect.Read(text.ToString());
    }

    private static string Text(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString()!,
        JsonValueKind.Number => e.GetDouble().ToString(CultureInfo.InvariantCulture),
        _ => e.GetRawText(),
    };

    private string FromDocument(JsonDocument doc)
    {
        var sb = new StringBuilder();
        foreach (var path in steps)
        {
            var e = doc.RootElement;
            foreach (var step in path) e = step.Key is { } k ? e.GetProperty(k) : e[step.Index];
            sb.Append(Text(e)).Append('|');
        }
        return sb.ToString();
    }

    [Benchmark(Baseline = true, Description = "System.Text.Json JsonDocument (string)")]
    public string JsonDocumentString()
    {
        using var doc = JsonDocument.Parse(json);
        return FromDocument(doc);
    }

    [Benchmark(Description = "System.Text.Json JsonDocument (UTF-8 bytes)")]
    public string JsonDocumentBytes()
    {
        using var doc = JsonDocument.Parse(utf8);
        return FromDocument(doc);
    }

    [Benchmark(Description = "System.Text.Json JsonNode")]
    public string JsonNodeTree()
    {
        var root = JsonNode.Parse(json)!;
        var sb = new StringBuilder();
        foreach (var path in steps)
        {
            var n = root;
            foreach (var step in path) n = step.Key is { } k ? n[k]! : n[step.Index]!;
            var v = n.GetValue<JsonElement>();
            sb.Append(Text(v)).Append('|');
        }
        return sb.ToString();
    }

    [Benchmark(Description = "RebolParse: read the whole document, then look up")]
    public string RebolParseTree() => Lookup(reader);

    [Benchmark(Description = "RebolParse compiled to IL: read the whole document, then look up")]
    public string RebolParseTreeIl() => Lookup(readerIl);

    private string Lookup(JsonDemo demo)
    {
        var root = demo.Read(json);
        var sb = new StringBuilder();
        foreach (var path in steps)
        {
            var v = root;
            foreach (var step in path)
                v = step.Key is { } k ? ((Dictionary<string, object?>)v!)[k] : ((List<object?>)v!)[step.Index];
            sb.Append(v is double d ? d.ToString(CultureInfo.InvariantCulture) : v?.ToString()).Append('|');
        }
        return sb.ToString();
    }

    [Benchmark(Description = "RebolParse: skip to each property, copy its text")]
    public string RebolParseTargeted()
    {
        var sb = new StringBuilder();
        foreach (var rule in targeted)
        {
            var raw = (string)Parser.Parse(json, rule, SkipRules, partial: true).Vars["r"]!;
            sb.Append(raw.Length > 1 && raw[0] == '"' ? raw[1..^1] : raw).Append('|');
        }
        return sb.ToString();
    }

    [Benchmark(Description = "RebolParse compiled to IL: skip to each property, copy its text")]
    public string RebolParseTargetedIl()
    {
        var sb = new StringBuilder();
        foreach (var rule in targetedIl)
        {
            var raw = (string)Parser.Parse(json, rule, SkipRulesIl, partial: true).Vars["r"]!;
            sb.Append(raw.Length > 1 && raw[0] == '"' ? raw[1..^1] : raw).Append('|');
        }
        return sb.ToString();
    }
}
