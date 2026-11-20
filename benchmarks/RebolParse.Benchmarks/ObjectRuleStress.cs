using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace RebolParse.Benchmarks;

/// <summary>
/// A stress run: one pass over 20,000 different orders, each with its own <see cref="JsonDocument"/>, for each
/// of the six rules timed in <see cref="ObjectRuleShapes"/> and for all six together. The time is for the
/// whole pass, so it reads directly as "how long does 20,000 objects take".
/// </summary>
[MemoryDiagnoser]
public abstract class ObjectRuleStress
{
    public const int Count = 20_000;

    private static readonly (string Key, string Scenario, string MessyExpresso, string MessyLinq)[] Rules =
    [
        ("DeepGuarded", "Deep path, guarded against nulls", "", ""),
        ("ListItems", "List count, then two items", "", ""),
        ("JsonNumber", "JSON number",
            "MetaJson.Num(Order.Metadata, \"priority\") >= 3", "MetaJson.Num(Order.Metadata, \"priority\") >= 3"),
        ("JsonString", "JSON nested string",
            "MetaJson.Str(Order.Metadata, \"customer.segment\") == \"enterprise\"", "MetaJson.Str(Order.Metadata, \"customer.segment\") == \"enterprise\""),
        ("JsonMixed", "JSON and properties together",
            "MetaJson.Num(Order.Metadata, \"customer.score\") > 80 && Order.Customer.Tier == Tier.Gold && Order.Lines.Count > 1",
            "MetaJson.Num(Order.Metadata, \"customer.score\") > 80 && Order.Customer.Tier == \"Gold\" && Order.Lines.Count > 1"),
        ("JsonHelper", "JSON through a registered helper", "", ""),
    ];

    /// <summary>One of the six rules, or all six checked against every order (120,000 checks).</summary>
    [Params("DeepGuarded", "ListItems", "JsonNumber", "JsonString", "JsonMixed", "JsonHelper", "AllSix")] public string Rule = "AllSix";

    /// <summary>
    /// Clean data has every field filled, so the libraries can call JsonElement methods directly. Messy data has
    /// nulls, empty lists, missing metadata and wrong types, which would make those calls throw, so there the
    /// libraries read JSON through the registered helper instead. The sample runs the same rule text on both.
    /// </summary>
    protected abstract bool Messy { get; }

    private Order[] orders = [];
    private Func<Order, bool>[] handwritten = [], sample = [], expresso = [], linq = [];

    [GlobalSetup]
    public void Setup()
    {
        orders = MakeOrders(Messy);
        var chosen = Rules.Where(r => Rule == "AllSix" || r.Key == Rule).ToArray();
        var scenarios = chosen.Select(r => Scenarios.Named(r.Scenario)).ToArray();
        handwritten = [.. scenarios.Select(s => s.Expected)];
        sample = [.. scenarios.Select(s => Scenarios.SampleBare(s.Sample!))];
        expresso = [.. chosen.Zip(scenarios, (r, s) => Scenarios.Expresso(Messy && r.MessyExpresso != "" ? r.MessyExpresso : s.Expresso!))];
        linq = [.. chosen.Zip(scenarios, (r, s) => Scenarios.LinqRegistered(Messy && r.MessyLinq != "" ? r.MessyLinq : s.Linq!))];

        // every engine must agree with the C# lambda about every order, rule by rule, and no rule may be trivially true or false
        for (var i = 0; i < chosen.Length; i++)
        {
            var passed = 0;
            foreach (var order in orders)
            {
                var expected = handwritten[i](order);
                if (expected) passed++;
                if (sample[i](order) != expected || expresso[i](order) != expected || linq[i](order) != expected)
                    throw new InvalidOperationException($"the engines disagree about order {order.Id} on {chosen[i].Key}");
            }
            if (passed is 0 or Count) throw new InvalidOperationException($"{chosen[i].Key} gives every order the same answer");
            Console.WriteLine($"// {(Messy ? "messy" : "clean")} {chosen[i].Key}: {passed} of {Count} orders pass");
        }
    }

    private int Pass(Func<Order, bool>[] checks)
    {
        var passed = 0;
        foreach (var order in orders)
            foreach (var check in checks)
                if (check(order)) passed++;
        return passed;
    }

    [Benchmark(Baseline = true)] public int Handwritten() => Pass(handwritten);
    [Benchmark] public int RebolParseRules() => Pass(sample);
    [Benchmark] public int DynamicExpresso() => Pass(expresso);
    [Benchmark] public int DynamicLinq() => Pass(linq);

    /// <summary>20,000 orders, the same every run. Each has its own parsed JSON document, as rows read from a database would.</summary>
    private static Order[] MakeOrders(bool messy)
    {
        var random = new Random(7);
        string[] cities = ["Oslo", "Leeds", "Bergen", "Tampa"], names = ["Ada", "Bob", "Cy", "Di", "Alma"], skus = ["desk", "pen", "cup", "mat"],
            sources = ["web", "phone", "store"], segments = ["enterprise", "smb", "consumer"];
        bool Rarely(int percent) => messy && random.Next(100) < percent;
        T Pick<T>(T[] from) => from[random.Next(from.Length)];
        Address Place() => new(Pick(cities), random.Next(100, 40_000));

        var earlier = new Order(0, new Customer("Old", Tier.Bronze, null, null), [], [], new DateTime(2025, 1, 1), null, null);
        var orders = new Order[Count];
        for (var id = 0; id < Count; id++)
        {
            var parent = Rarely(15) ? null : new Company("Parent", Rarely(10) ? null : Place(), null);
            var employer = Rarely(20) ? null : new Company("Employer", Place(), parent);
            var customer = new Customer(Pick(names), (Tier)random.Next(3), Rarely(20) ? null : Place(), employer);

            var lines = new List<OrderLine>();
            for (var n = Rarely(15) ? random.Next(2) : random.Next(2, 6); n > 0; n--)
                lines.Add(new OrderLine(new Product(Pick(skus), "goods", new Money(random.Next(1, 300), "EUR"), random.Next(4) == 0 ? null : random.NextDouble() * 5), random.Next(1, 20)));

            var tags = new Dictionary<string, string>();
            if (!Rarely(25)) tags["channel"] = Pick(sources);

            var json = Rarely(10) ? null
                : Rarely(10) ? "{}"
                : Rarely(5) ? """{"priority": "high", "gift": "yes", "customer": "n/a", "tags": {}, "source": 7}"""
                : "{\"priority\": " + random.Next(1, 10) + ", \"source\": \"" + Pick(sources) + "\", \"gift\": " + (random.Next(2) == 0 ? "true" : "false")
                  + ", \"note\": null, \"customer\": {\"segment\": \"" + Pick(segments) + "\", \"score\": "
                  + (random.Next(0, 1000) / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                  + "}, \"tags\": [\"rush\", \"fragile\"], \"ship-to\": {\"city\": \"" + Pick(cities) + "\"}}";

            orders[id] = new Order(id + 1, customer, lines, tags, new DateTime(2026, 1, 1).AddDays(random.Next(300)),
                json == null ? null : JsonDocument.Parse(json), Rarely(30) ? null : earlier);
        }
        return orders;
    }
}

/// <inheritdoc/>
public class ObjectRuleStressClean : ObjectRuleStress { protected override bool Messy => false; }

/// <inheritdoc/>
public class ObjectRuleStressMessy : ObjectRuleStress { protected override bool Messy => true; }
