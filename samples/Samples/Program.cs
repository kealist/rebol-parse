using RebolParse;
using static RebolParse.P;

// 1. key=value config lines, captured with copy.
var pairs = new List<(string, string)>();
var kv = Dialect.Read(
    "any [copy k to \"=\" \"=\" copy v [to newline | to end] (@pair) [newline | end]]",
    new Dictionary<string, Action<Ctx>>
    {
        ["pair"] = c => pairs.Add(((string)c.Vars["k"]!, (string)c.Vars["v"]!)),
    });
var r1 = Parser.Parse("name=joshua\nlang=C#\nmode=parse", kv);
Console.WriteLine($"config matched={r1.Matched}: {string.Join(", ", pairs.Select(p => $"{p.Item1}->{p.Item2}"))}");

// 2. Recursive arithmetic grammar, built with the fluent API.
var rules = new Dictionary<string, Rule>
{
    ["expr"] = Ref("term") + Any(CharIn("+-") + Ref("term")),
    ["term"] = Ref("factor") + Any(CharIn("*/") + Ref("factor")),
    ["factor"] = Some(Digit) | ("(" + Ref("expr") + ")"),
};
foreach (var s in new[] { "1+2*3", "(1+2)*3", "2*(3+(4-1))", "1+*2", "(1+2" })
    Console.WriteLine($"expr {s,-12} -> {Parser.Parse(s, Ref("expr"), rules).Matched}");

// 3. Block mode: a tiny command dialect over values instead of characters.
var cmd = Dialect.Read("[quote add integer! integer!] | [quote say string!]");
var inputs = new List<List<object>>
{
    new() { new Word("add"), 1, 2 },
    new() { new Word("say"), "hello" },
    new() { new Word("say"), 42 },
};
foreach (var block in inputs)
    Console.WriteLine($"block [{string.Join(" ", block)}] -> {Parser.Parse(block, cmd).Matched}");
