using System.Globalization;
using RebolParse;
using static RebolParse.P;

namespace Samples;

/// <summary>
/// RFC 4180 CSV: quoted fields can hold commas, doubled quotes and line breaks. Shows capturing into plain C#
/// collections through actions, then using the rows.
/// </summary>
public sealed class CsvDemo : IDemo
{
    public string Name => "csv";
    public string Title => "CSV: quoted fields, embedded newlines, and a report from the rows";

    private readonly Dictionary<string, Rule> rules;
    private List<List<string>> rows = [];
    private List<string> row = [];

    public CsvDemo()
    {
        var actions = new Dictionary<string, Action<Ctx>>
        {
            ["quoted"] = c => row.Add(((string)c.Vars["f"]!).Replace("\"\"", "\"")),
            ["plain"] = c => row.Add((string)c.Vars["f"]!),
            ["row"] = _ => { rows.Add(row); row = []; },
        };
        rules = Dialect.ReadGrammar("""
            file   = row any ["^/" row]
            row    = field any ["," field] (@row)
            field  = quoted | plain
            quoted = "^"" copy f any [not "^"" skip | "^"" "^""] "^"" (@quoted)
            plain  = copy f any [not ["," | "^"" | "^/"] skip] (@plain)
            """, actions).Compiled();
    }

    public List<List<string>> Read(string csv)
    {
        rows = [];
        row = [];
        var text = csv.ReplaceLineEndings("\n");
        if (!Parser.Parse(text, Ref("file"), rules).Matched)
            throw new FormatException("malformed CSV (an unterminated quote or a stray quote inside a field)");
        if (rows is [.., [""]]) rows.RemoveAt(rows.Count - 1);   // a final newline leaves one empty row
        return rows;
    }

    private const string Sample = "id,item,qty,price,note\r\n" +
        "1,Widget,4,2.50,\"plain, but with a comma\"\r\n" +
        "2,\"Gadget \"\"Pro\"\"\",1,19.99,\"two\nlines\"\r\n" +
        "3,Gizmo,10,0.75,\r\n" +
        "4,\"Thing, large\",2,12.00,ok\r\n";

    public void Run(Report r)
    {
        var table = Read(Sample);
        r.Expect("5 rows including the header", table.Count, 5);
        r.Expect("every row has 5 fields", table.All(x => x.Count == 5));
        r.Expect("quoted comma", table[1][4], "plain, but with a comma");
        r.Expect("doubled quotes", table[2][1], "Gadget \"Pro\"");
        r.Expect("embedded newline", table[2][4], "two\nlines");
        r.Expect("trailing empty field", table[3][4], "");
        r.Expect("quoted field with a comma", table[4][1], "Thing, large");

        var header = table[0];
        var data = table.Skip(1).Select(x => header.Zip(x).ToDictionary(p => p.First, p => p.Second)).ToList();
        var total = data.Sum(d => int.Parse(d["qty"]) * decimal.Parse(d["price"], CultureInfo.InvariantCulture));
        r.Expect("order total", total, 4 * 2.50m + 19.99m + 10 * 0.75m + 2 * 12.00m);

        var widths = Enumerable.Range(0, header.Count).Select(i => table.Max(x => x[i].Replace("\n", " ").Length)).ToArray();
        r.Say(string.Join("\n", table.Select(x => string.Join(" | ", x.Select((f, i) => f.Replace("\n", " ").PadRight(widths[i]))))));
        r.Say($"total = {total}");

        r.Expect("a single field", Read("hello").Single().Single(), "hello");
        r.Expect("empty fields", string.Join("|", Read(",,").Single()), "||");
        foreach (var bad in new[] { "a,\"unterminated", "a,b\"c\",d" })
            try { Read(bad); r.Expect($"{bad} is rejected", false); }
            catch (FormatException) { r.Expect($"{bad} is rejected", true); }

        // 20,000 rows
        var big = string.Join("\n", Enumerable.Range(0, 20000).Select(i => $"{i},\"name, {i}\",{i % 7}"));
        var parsed = Read(big);
        r.Expect("20,000 rows read", parsed.Count, 20000);
        r.Expect("last row", string.Join("|", parsed[^1]), "19999|name, 19999|0");
    }
}
