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
    internal static Order[] MakeOrders(bool messy)
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

/// <summary>
/// The same 20,000 orders against rules of the size a business would really write: seven to nine conditions
/// each, with nested brackets, negation, deep paths, list items, a dictionary key and JSON metadata together.
/// The libraries use one spelling that is safe on both data sets: <c>?.</c> or <c>np(...)</c> on paths, a count
/// before a list item, ContainsKey before a key, and the registered helper for JSON.
/// </summary>
[MemoryDiagnoser]
public abstract class ObjectRuleStressComplex
{
    private const string M = "MetaJson.Num(Order.Metadata, ", S = "MetaJson.Str(Order.Metadata, ", F = "MetaJson.Flag(Order.Metadata, ";

    private sealed record Complex(string Key, Func<Order, bool> Expected, string Sample, string Expresso, string Linq);

    private static readonly Complex[] Rules =
    [
        // plain objects: an enum, three deep paths, a list and two of its items, a dictionary key, a link to another order
        new("Eligibility",
            o => o.Customer.Tier != Tier.Bronze
                 && (o.Customer.Address?.City == "Oslo" || o.Customer.Employer?.Address?.City == "Oslo" || o.Customer.Employer?.Parent?.Address?.City == "Oslo")
                 && o.Lines.Count >= 2 && o.Lines[0].Product.Price.Amount > 50 && !(o.Lines[1].Qty < 3)
                 && o.Tags.GetValueOrDefault("channel") != "phone" && o.Previous != null,
            """
            Order.Customer.Tier != "Bronze"
            && (Order.Customer.Address.City == "Oslo" || Order.Customer.Employer.Address.City == "Oslo" || Order.Customer.Employer.Parent.Address.City == "Oslo")
            && Order.Lines.Count >= 2 && Order.Lines[0].Product.Price.Amount > 50 && !(Order.Lines[1].Qty < 3)
            && Order.Tags["channel"] != "phone" && Order.Previous != null
            """,
            """
            Order.Customer.Tier != Tier.Bronze
            && (Order.Customer.Address?.City == "Oslo" || Order.Customer.Employer?.Address?.City == "Oslo" || Order.Customer.Employer?.Parent?.Address?.City == "Oslo")
            && Order.Lines.Count >= 2 && Order.Lines[0].Product.Price.Amount > 50 && !(Order.Lines[1].Qty < 3)
            && !(Order.Tags.ContainsKey("channel") && Order.Tags["channel"] == "phone") && Order.Previous != null
            """,
            """
            Order.Customer.Tier != "Bronze"
            && (np(Order.Customer.Address.City) == "Oslo" || np(Order.Customer.Employer.Address.City) == "Oslo" || np(Order.Customer.Employer.Parent.Address.City) == "Oslo")
            && Order.Lines.Count >= 2 && Order.Lines[0].Product.Price.Amount > 50 && !(Order.Lines[1].Qty < 3)
            && !(Order.Tags.ContainsKey("channel") && Order.Tags["channel"] == "phone") && Order.Previous != null
            """),

        // nearly all JSON: numbers, strings, a flag, an array item, and JSON compared with a property
        new("Fraud",
            o => (MetaJson.Num(o.Metadata, "priority") >= 7 || MetaJson.Num(o.Metadata, "customer.score") < 20)
                 && MetaJson.Str(o.Metadata, "source") != "store" && !MetaJson.Flag(o.Metadata, "gift")
                 && (MetaJson.Str(o.Metadata, "ship-to.city") != o.Customer.Address?.City || MetaJson.Str(o.Metadata, "customer.segment") == "consumer")
                 && MetaJson.Str(o.Metadata, "tags.0") == "rush",
            """
            (Order.Metadata.priority >= 7 || Order.Metadata.customer.score < 20)
            && Order.Metadata.source != "store" && !Order.Metadata.gift
            && (Order.Metadata["ship-to"].city != Order.Customer.Address.City || Order.Metadata.customer.segment == "consumer")
            && Order.Metadata.tags[0] == "rush"
            """,
            $"""
            ({M}"priority") >= 7 || {M}"customer.score") < 20)
            && {S}"source") != "store" && !{F}"gift")
            && ({S}"ship-to.city") != Order.Customer.Address?.City || {S}"customer.segment") == "consumer")
            && {S}"tags.0") == "rush"
            """,
            $"""
            ({M}"priority") >= 7 || {M}"customer.score") < 20)
            && {S}"source") != "store" && !{F}"gift")
            && ({S}"ship-to.city") != np(Order.Customer.Address.City) || {S}"customer.segment") == "consumer")
            && {S}"tags.0") == "rush"
            """),

        // both together, with brackets inside brackets
        new("Routing",
            o => (o.Customer.Tier == Tier.Gold && MetaJson.Num(o.Metadata, "priority") >= 5 || o.Customer.Tier == Tier.Silver && MetaJson.Num(o.Metadata, "priority") >= 8)
                 && (o.Lines.Count > 2 || (o.Lines.Count > 0 && o.Lines[0].Product.WeightKg > 2.5))
                 && !(MetaJson.Str(o.Metadata, "source") == "phone" && o.Tags.GetValueOrDefault("channel") == "phone")
                 && o.Customer.Employer?.Parent != null && MetaJson.Num(o.Metadata, "customer.score") >= 40,
            """
            (Order.Customer.Tier == "Gold" && Order.Metadata.priority >= 5 || Order.Customer.Tier == "Silver" && Order.Metadata.priority >= 8)
            && (Order.Lines.Count > 2 || Order.Lines[0].Product.WeightKg > 2.5)
            && !(Order.Metadata.source == "phone" && Order.Tags["channel"] == "phone")
            && Order.Customer.Employer.Parent != null && Order.Metadata.customer.score >= 40
            """,
            $"""
            (Order.Customer.Tier == Tier.Gold && {M}"priority") >= 5 || Order.Customer.Tier == Tier.Silver && {M}"priority") >= 8)
            && (Order.Lines.Count > 2 || (Order.Lines.Count > 0 && Order.Lines[0].Product.WeightKg > 2.5))
            && !({S}"source") == "phone" && Order.Tags.ContainsKey("channel") && Order.Tags["channel"] == "phone")
            && Order.Customer.Employer?.Parent != null && {M}"customer.score") >= 40
            """,
            $"""
            (Order.Customer.Tier == "Gold" && {M}"priority") >= 5 || Order.Customer.Tier == "Silver" && {M}"priority") >= 8)
            && (Order.Lines.Count > 2 || (Order.Lines.Count > 0 && Order.Lines[0].Product.WeightKg > 2.5))
            && !({S}"source") == "phone" && Order.Tags.ContainsKey("channel") && Order.Tags["channel"] == "phone")
            && np(Order.Customer.Employer.Parent) != null && {M}"customer.score") >= 40
            """),
    ];

    /// <summary>One rule, or all three checked against every order.</summary>
    [Params("Eligibility", "Fraud", "Routing", "AllThree")] public string Rule = "AllThree";

    protected abstract bool Messy { get; }

    private Order[] orders = [];
    private Func<Order, bool>[] handwritten = [], sample = [], expresso = [], linq = [];

    [GlobalSetup]
    public void Setup()
    {
        orders = ObjectRuleStress.MakeOrders(Messy);
        var chosen = Rules.Where(r => Rule == "AllThree" || r.Key == Rule).ToArray();
        handwritten = [.. chosen.Select(r => r.Expected)];
        sample = [.. chosen.Select(r => Scenarios.SampleBare(r.Sample))];
        expresso = [.. chosen.Select(r => Scenarios.Expresso(r.Expresso))];
        linq = [.. chosen.Select(r => Scenarios.LinqRegistered(r.Linq))];
        for (var i = 0; i < chosen.Length; i++)
        {
            var passed = 0;
            foreach (var order in orders)
            {
                var expected = handwritten[i](order);
                if (expected) passed++;
                if (sample[i](order) != expected) throw new InvalidOperationException($"the sample disagrees about order {order.Id} on {chosen[i].Key}");
                if (expresso[i](order) != expected) throw new InvalidOperationException($"DynamicExpresso disagrees about order {order.Id} on {chosen[i].Key}");
                if (linq[i](order) != expected) throw new InvalidOperationException($"Dynamic.Core disagrees about order {order.Id} on {chosen[i].Key}");
            }
            if (passed is 0 or ObjectRuleStress.Count) throw new InvalidOperationException($"{chosen[i].Key} gives every order the same answer");
            Console.WriteLine($"// {(Messy ? "messy" : "clean")} {chosen[i].Key}: {passed} of {ObjectRuleStress.Count} orders pass");
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
}

/// <inheritdoc/>
public class ObjectRuleStressComplexClean : ObjectRuleStressComplex { protected override bool Messy => false; }

/// <inheritdoc/>
public class ObjectRuleStressComplexMessy : ObjectRuleStressComplex { protected override bool Messy => true; }
