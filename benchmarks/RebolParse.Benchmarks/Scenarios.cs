using System.Linq.Dynamic.Core;
using System.Linq.Dynamic.Core.CustomTypeProviders;
using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using DynamicExpresso;
using Samples;

namespace RebolParse.Benchmarks;

// A deeper model than Person: lists, a dictionary, an enum, nullable links, an order that points at an earlier
// order, and a JsonDocument of loose metadata.
public enum Tier { Bronze, Silver, Gold }
public sealed record Money(decimal Amount, string Currency);
public sealed record Product(string Sku, string Category, Money Price, double? WeightKg);
public sealed record OrderLine(Product Product, int Qty);
public sealed record Company(string Name, Address? Address, Company? Parent);
public sealed record Customer(string Name, Tier Tier, Address? Address, Company? Employer);
public sealed record Order(int Id, Customer Customer, List<OrderLine> Lines, Dictionary<string, string> Tags, DateTime Placed,
    JsonDocument? Metadata, Order? Previous);

/// <summary>
/// The helper a team would write to reach into JSON from a library that has no JSON paths of its own, and
/// what the expected answers are computed with. A path is dotted names; a number is an array position.
/// </summary>
public static class MetaJson
{
    public static double? Num(JsonDocument? document, string path) =>
        Find(document, path) is { ValueKind: JsonValueKind.Number } n ? n.GetDouble() : null;

    public static string? Str(JsonDocument? document, string path) =>
        Find(document, path) is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;

    public static bool Flag(JsonDocument? document, string path) => Find(document, path) is { ValueKind: JsonValueKind.True };

    private static JsonElement? Find(JsonDocument? document, string path)
    {
        if (document == null) return null;
        var e = document.RootElement;
        foreach (var part in path.Split('.'))
        {
            if (e.ValueKind == JsonValueKind.Array && int.TryParse(part, out var at) && at < e.GetArrayLength()) e = e[at];
            else if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(part, out e)) return null;
        }
        return e;
    }
}

/// <summary>One rule, written the way each library wants it. A null text means that library cannot say it.</summary>
public sealed record Scenario(string Name, Func<Order, bool> Expected, string? Sample, string? Expresso, string? Linq)
{
    /// <summary>A rule every library reads as the same text.</summary>
    public Scenario(string name, Func<Order, bool> expected, string text) : this(name, expected, text, text, text) { }
}

/// <summary>
/// Runs every scenario on every engine against four differently filled orders and prints what happened:
/// <c>dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- scenarios</c>
/// </summary>
public static class Scenarios
{
    private const string Json = "Order.Metadata.RootElement";

    public static readonly Scenario[] All =
    [
        new("Deep path", o => o.Customer.Employer?.Parent?.Address?.City == "Oslo",
            "Order.Customer.Employer.Parent.Address.City == \"Oslo\""),
        new("Deep path, guarded against nulls", o => o.Customer.Employer?.Parent?.Address?.City == "Oslo",
            "Order.Customer.Employer.Parent.Address.City == \"Oslo\"",
            "Order.Customer.Employer?.Parent?.Address?.City == \"Oslo\"",
            "np(Order.Customer.Employer.Parent.Address.City) == \"Oslo\""),
        new("Enum by name", o => o.Customer.Tier == Tier.Gold,
            "Order.Customer.Tier == \"Gold\"", "Order.Customer.Tier == Tier.Gold", "Order.Customer.Tier == \"Gold\""),
        new("List item", o => o.Lines.Count > 0 && o.Lines[0].Product.Price.Amount > 100,
            "Order.Lines[0].Product.Price.Amount > 100"),
        new("List count, then two items", o => o.Lines.Count >= 2 && o.Lines[0].Qty > o.Lines[1].Qty,
            "Order.Lines.Count >= 2 && Order.Lines[0].Qty > Order.Lines[1].Qty"),
        new("Nullable number", o => o.Lines.Count > 0 && o.Lines[0].Product.WeightKg > 1.5,
            "Order.Lines[0].Product.WeightKg > 1.5"),
        new("Dictionary key", o => o.Tags.GetValueOrDefault("channel") == "web",
            "Order.Tags[\"channel\"] == \"web\""),
        new("Dates on two objects", o => o.Previous != null && o.Placed > o.Previous.Placed,
            "Order.Placed > Order.Previous.Placed"),
        new("Any item matches", o => o.Lines.Any(l => l.Qty > 10),
            null, "Order.Lines.Any(l => l.Qty > 10)", "Order.Lines.Any(Qty > 10)"),
        new("Method call", o => o.Customer.Name.StartsWith('A'),
            null, "Order.Customer.Name.StartsWith(\"A\")", "Order.Customer.Name.StartsWith(\"A\")"),
        new("Arithmetic", o => o.Lines.Count > 0 && o.Lines[0].Qty * o.Lines[0].Product.Price.Amount > 1000,
            null, "Order.Lines[0].Qty * Order.Lines[0].Product.Price.Amount > 1000", "Order.Lines[0].Qty * Order.Lines[0].Product.Price.Amount > 1000"),

        new("JSON number", o => MetaJson.Num(o.Metadata, "priority") >= 3,
            "Order.Metadata.priority >= 3",
            $"{Json}.GetProperty(\"priority\").GetInt32() >= 3", $"{Json}.GetProperty(\"priority\").GetInt32() >= 3"),
        new("JSON nested string", o => MetaJson.Str(o.Metadata, "customer.segment") == "enterprise",
            "Order.Metadata.customer.segment == \"enterprise\"",
            $"{Json}.GetProperty(\"customer\").GetProperty(\"segment\").GetString() == \"enterprise\"",
            $"{Json}.GetProperty(\"customer\").GetProperty(\"segment\").GetString() == \"enterprise\""),
        new("JSON true or false", o => MetaJson.Flag(o.Metadata, "gift"),
            "Order.Metadata.gift", $"{Json}.GetProperty(\"gift\").GetBoolean()", $"{Json}.GetProperty(\"gift\").GetBoolean()"),
        new("JSON array item", o => MetaJson.Str(o.Metadata, "tags.0") == "rush",
            "Order.Metadata.tags[0] == \"rush\"",
            $"{Json}.GetProperty(\"tags\")[0].GetString() == \"rush\"", $"{Json}.GetProperty(\"tags\")[0].GetString() == \"rush\""),
        new("JSON key with a dash", o => MetaJson.Str(o.Metadata, "ship-to.city") == "Oslo",
            "Order.Metadata[\"ship-to\"].city == \"Oslo\"",
            $"{Json}.GetProperty(\"ship-to\").GetProperty(\"city\").GetString() == \"Oslo\"",
            $"{Json}.GetProperty(\"ship-to\").GetProperty(\"city\").GetString() == \"Oslo\""),
        new("JSON and properties together",
            o => MetaJson.Num(o.Metadata, "customer.score") > 80 && o.Customer.Tier == Tier.Gold && o.Lines.Count > 1,
            "Order.Metadata.customer.score > 80 && Order.Customer.Tier == \"Gold\" && Order.Lines.Count > 1",
            $"{Json}.GetProperty(\"customer\").GetProperty(\"score\").GetDouble() > 80 && Order.Customer.Tier == Tier.Gold && Order.Lines.Count > 1",
            $"{Json}.GetProperty(\"customer\").GetProperty(\"score\").GetDouble() > 80 && Order.Customer.Tier == \"Gold\" && Order.Lines.Count > 1"),
        new("JSON through a registered helper",
            o => MetaJson.Num(o.Metadata, "customer.score") > 80 && MetaJson.Str(o.Metadata, "source") == "web",
            "Order.Metadata.customer.score > 80 && Order.Metadata.source == \"web\"",
            "MetaJson.Num(Order.Metadata, \"customer.score\") > 80 && MetaJson.Str(Order.Metadata, \"source\") == \"web\"",
            "MetaJson.Num(Order.Metadata, \"customer.score\") > 80 && MetaJson.Str(Order.Metadata, \"source\") == \"web\""),
    ];

    public static Scenario Named(string name) => All.Single(s => s.Name == name);

    // ---- the four orders ----

    private static JsonDocument Doc(string json) => JsonDocument.Parse(json);

    /// <summary>Everything is there and every rule is true.</summary>
    public static readonly Order Full = new(2,
        new Customer("Ada", Tier.Gold, new Address("London", 1815), new Company("Initech", new Address("Bergen", 5003), new Company("Globex", new Address("Oslo", 150), null))),
        [new(new Product("desk", "furniture", new Money(150m, "EUR"), 2.0), 12), new(new Product("pen", "office", new Money(2m, "EUR"), null), 1)],
        new() { ["channel"] = "web" }, new DateTime(2026, 3, 1),
        Doc("""
            {"priority": 5, "source": "web", "gift": true, "note": null,
             "customer": {"segment": "enterprise", "score": 87.5},
             "tags": ["rush", "fragile"], "ship-to": {"city": "Oslo"}}
            """),
        new Order(1, new Customer("Ada", Tier.Gold, null, null), [], [], new DateTime(2026, 1, 1), null, null));

    /// <summary>Everything is there and every rule is false.</summary>
    public static readonly Order Other = new(3,
        new Customer("Bob", Tier.Silver, new Address("Leeds", 2000), new Company("Hooli", new Address("Leeds", 2000), new Company("Umbrella", new Address("Leeds", 2000), null))),
        [new(new Product("cup", "kitchen", new Money(5m, "EUR"), 0.3), 2), new(new Product("mat", "kitchen", new Money(9m, "EUR"), 0.1), 3)],
        new() { ["channel"] = "phone" }, new DateTime(2026, 3, 1),
        Doc("""
            {"priority": 1, "source": "phone", "gift": false,
             "customer": {"segment": "smb", "score": 20},
             "tags": ["slow"], "ship-to": {"city": "Leeds"}}
            """),
        new Order(1, new Customer("Bob", Tier.Silver, null, null), [], [], new DateTime(2026, 6, 1), null, null));

    /// <summary>As little as the types allow: no employer, no lines, no tags, no earlier order, no metadata.</summary>
    public static readonly Order Sparse = new(4, new Customer("Cy", Tier.Bronze, null, null), [], [], new DateTime(2026, 3, 1), null, null);

    /// <summary>The full order, but the metadata holds the wrong type under every name.</summary>
    public static readonly Order Odd = Full with
    {
        Metadata = Doc("""{"priority": "high", "gift": "yes", "customer": "n/a", "tags": {}, "ship-to": [], "source": 7}"""),
    };

    public static readonly (string Name, Order Order)[] Orders = [("full", Full), ("other", Other), ("sparse", Sparse), ("odd", Odd)];

    // ---- the engines ----

    private static readonly RuleDemo Demo = new();

    public static Func<Order, bool> Sample(string text) => Demo.Compile<Order>(text);

    /// <summary>The sample's tree without the wrapper that refuses a null order, as the libraries have none.</summary>
    public static Func<Order, bool> SampleBare(string text) => Demo.ToExpression<Order>(text).Compile();

    /// <summary>DynamicExpresso needs lambdas switched on for <c>Any(l => ...)</c>, and to be told about types a rule names.</summary>
    private static readonly Interpreter Interpreter =
        new Interpreter(InterpreterOptions.Default | InterpreterOptions.LambdaExpressions).Reference(typeof(Tier)).Reference(typeof(MetaJson));

    public static Func<Order, bool> Expresso(string text) => Interpreter.ParseAsDelegate<Func<Order, bool>>(text, "Order");

    /// <summary>Dynamic.Core refuses method calls on types it was not told about, so JSON needs them registered.</summary>
    private static readonly ParsingConfig Registered = MakeRegistered();

    private static ParsingConfig MakeRegistered()
    {
        var config = new ParsingConfig();
        config.CustomTypeProvider = new DefaultDynamicLinqCustomTypeProvider(config,
            [typeof(JsonDocument), typeof(JsonElement), typeof(MetaJson), typeof(Tier)]);
        return config;
    }

    private static Func<Order, bool> Linq(ParsingConfig config, string text) =>
        (Func<Order, bool>)DynamicExpressionParser.ParseLambda(config, [Expression.Parameter(typeof(Order), "Order")], typeof(bool), text).Compile();

    public static Func<Order, bool> LinqDefault(string text) => Linq(ParsingConfig.Default, text);
    public static Func<Order, bool> LinqRegistered(string text) => Linq(Registered, text);

    private static readonly (string Name, Func<Scenario, string?> Text, Func<string, Func<Order, bool>> Compile)[] Engines =
    [
        ("RebolParse rules sample", s => s.Sample, Sample),
        ("DynamicExpresso", s => s.Expresso, Expresso),
        ("Dynamic.Core, default", s => s.Linq, LinqDefault),
        ("Dynamic.Core, types registered", s => s.Linq, LinqRegistered),
    ];

    // ---- the report ----

    private static string Outcome(Scenario scenario, string? text, Func<string, Func<Order, bool>> compile, List<string> notes, string engine)
    {
        if (text == null) return "cannot say it";
        Func<Order, bool> check;
        try { check = compile(text); }
        catch (Exception ex)
        {
            notes.Add($"- **{scenario.Name}**, {engine}: does not compile. {ex.GetType().Name}: {OneLine(ex.Message)}");
            return "does not compile";
        }

        var wrong = new List<string>();
        foreach (var (name, order) in Orders)
        {
            var expected = scenario.Expected(order);
            try
            {
                var got = check(order);
                if (got != expected) wrong.Add($"{name}: **{got.ToString().ToLowerInvariant()}, should be {expected.ToString().ToLowerInvariant()}**");
            }
            catch (Exception ex) { wrong.Add($"{name}: {ex.GetType().Name}"); }
        }
        return wrong.Count == 0 ? "ok" : string.Join("; ", wrong);
    }

    private static string OneLine(string message)
    {
        message = message.ReplaceLineEndings(" ");
        return message.Length > 160 ? message[..160] + "..." : message;
    }

    /// <summary>
    /// Rule text an author should not be able to write. Each is only compiled, never run, and each is harmless
    /// even if it were: the question is whether the engine lets rule text reach beyond the object it was given.
    /// </summary>
    private static readonly (string What, string Rule)[] Hostile =
    [
        ("Arithmetic", "Order.Id + 1 > 2"),
        ("Method on a value", "Order.Customer.Name.ToUpper() == \"ADA\""),
        ("Method that changes the object", "Order.Tags.Remove(\"channel\")"),
        ("Assignment", "(Order.Customer = null) == null"),
        ("Static method by full name", "System.IO.File.Exists(\"x\")"),
        ("Static property by full name", "System.Environment.MachineName == \"x\""),
        ("Static method, short name", "File.Exists(\"x\")"),
        ("Start from a static type", "System.Diagnostics.Process.GetCurrentProcess().Id > 0"),
        ("Create an object", "new System.Text.StringBuilder().Length == 0"),
        ("GetType()", "Order.GetType().Name == \"Order\""),
        ("Reflection through GetType()", "Order.GetType().Assembly.GetType(\"System.IO.File\") != null"),
        ("typeof", "typeof(System.IO.File) != null"),
        ("A lambda", "Order.Lines.Any(l => l.Qty > 10)"),
    ];

    /// <summary><c>-- safety</c>: which engines accept which kinds of rule text, in the setups a team would use.</summary>
    public static void PrintSafety()
    {
        var plain = new Interpreter();
        var locked = new Interpreter(InterpreterOptions.PrimitiveTypes | InterpreterOptions.SystemKeywords).EnableAssignment(AssignmentOperators.None);
        (string Name, Func<string, object> Compile)[] engines =
        [
            ("RebolParse rules sample", text => Sample(text)),
            ("DynamicExpresso, default", text => plain.ParseAsDelegate<Func<Order, bool>>(text, "Order")),
            ("DynamicExpresso, lambdas on", text => Expresso(text)),
            ("DynamicExpresso, locked down", text => locked.ParseAsDelegate<Func<Order, bool>>(text, "Order")),
            ("Dynamic.Core, default", text => LinqDefault(text)),
            ("Dynamic.Core, JSON types registered", text => LinqRegistered(text)),
        ];
        var notes = new List<string>();
        Console.WriteLine("| Rule text | " + string.Join(" | ", engines.Select(e => e.Name)) + " |");
        Console.WriteLine("|---|" + string.Concat(engines.Select(_ => "---|")));
        foreach (var (what, rule) in Hostile)
        {
            var cells = engines.Select(engine =>
            {
                try { engine.Compile(rule); return "**accepted**"; }
                catch (Exception ex) { notes.Add($"- {what}, {engine.Name}: {ex.GetType().Name}: {OneLine(ex.Message)}"); return "refused"; }
            }).ToList();
            Console.WriteLine($"| {what}: `{rule}` | " + string.Join(" | ", cells) + " |");
        }
        Console.WriteLine();
        Console.WriteLine(string.Join("\n", notes));
    }

    public static void Print()
    {
        var notes = new List<string>();
        var table = new StringBuilder();
        table.AppendLine("| Rule | " + string.Join(" | ", Engines.Select(e => e.Name)) + " |");
        table.AppendLine("|---|" + string.Concat(Engines.Select(_ => "---|")));
        foreach (var scenario in All)
            table.AppendLine($"| {scenario.Name} | " + string.Join(" | ", Engines.Select(e => Outcome(scenario, e.Text(scenario), e.Compile, notes, e.Name))) + " |");

        Console.WriteLine("Each rule is checked against four orders: full (every rule true), other (every rule false),");
        Console.WriteLine("sparse (nulls and empty lists everywhere, no metadata) and odd (metadata of the wrong types).");
        Console.WriteLine("'ok' means all four answers matched a null-safe C# lambda.\n");
        Console.WriteLine(table);
        Console.WriteLine(string.Join("\n", notes));
        Console.WriteLine("\nThe rule texts:\n");
        foreach (var s in All)
        {
            Console.WriteLine($"{s.Name}");
            Console.WriteLine($"    sample:          {s.Sample ?? "-"}");
            Console.WriteLine($"    DynamicExpresso: {s.Expresso ?? "-"}");
            Console.WriteLine($"    Dynamic.Core:    {s.Linq ?? "-"}");
        }
    }
}
