using System.Linq.Dynamic.Core;
using System.Linq.Expressions;
using BenchmarkDotNet.Attributes;
using DynamicExpresso;
using FastExpressionCompiler;
using RulesEngine.Models;
using Samples;

namespace RebolParse.Benchmarks;

/// <summary>The object every rule in these benchmarks is checked against.</summary>
public sealed record Address(string City, int Zip);

/// <inheritdoc cref="Address"/>
public sealed record Person(string Name, int Age, string Job, decimal Salary, Address Address, double Height = 1.7, bool Remote = false);

/// <summary>The rule texts and the ways to turn one into a <c>Func&lt;Person, bool&gt;</c>, shared by both benchmark classes.</summary>
internal static class RuleTexts
{
    /// <summary>The same rule in every library: each of them reads this text unchanged.</summary>
    public static string For(string rule) => rule switch
    {
        "Simple" => "Person.Age > 18 && Person.Job == \"SE\"",
        "Nested" => "Person.Age >= 18 && (Person.Job == \"PM\" || Person.Address.City == \"London\") && Person.Salary > 50000",
        _ => throw new ArgumentException(rule),
    };

    /// <summary>
    /// A rule of <paramref name="clauses"/> comparisons over ints, strings, decimals, doubles, a bool and nested
    /// properties, joined so that it is an OR of four-clause AND chains. Every library reads the same text.
    /// </summary>
    public static string Generated(int clauses)
    {
        static string Clause(int i) => (i % 8) switch
        {
            0 => $"Person.Age > {18 + i % 30}",
            1 => "Person.Job == \"SE\"",
            2 => $"Person.Salary >= {30000 + i * 500}",
            3 => "Person.Address.City != \"Leeds\"",
            4 => $"Person.Address.Zip < {20000 + i * 100}",
            5 => "!Person.Remote",
            6 => "Person.Height > 1.6",
            _ => $"!(Person.Age < {10 + i % 5} || Person.Job == \"QA\")",
        };
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < clauses; i++) text.Append(i == 0 ? "" : i % 4 == 0 ? " || " : " && ").Append(Clause(i));
        return text.ToString();
    }

    /// <summary>A thousand varied people, the same every run, so rules pass for some and fail early for others.</summary>
    public static readonly Person[] Crowd = MakeCrowd();

    private static Person[] MakeCrowd()
    {
        var random = new Random(42);
        string[] jobs = ["SE", "PM", "QA", "OPS"], cities = ["London", "Leeds", "Tampa", "Oslo"];
        return [.. Enumerable.Range(0, 1000).Select(i => new Person($"P{i}", random.Next(12, 70), jobs[random.Next(4)],
            random.Next(20_000, 200_000), new Address(cities[random.Next(4)], random.Next(1000, 40_000)),
            1.5 + random.NextDouble() * 0.5, random.Next(2) == 0))];
    }

    public static Func<Person, bool> Handwritten(string rule) => rule == "Simple"
        ? p => p.Age > 18 && p.Job == "SE"
        : p => p.Age >= 18 && (p.Job == "PM" || p.Address.City == "London") && p.Salary > 50000;

    public static readonly RuleDemo Demo = new();
    public static readonly Interpreter Expresso = new();

    /// <summary>The sample's own expression tree compiled by FastExpressionCompiler. Throws if it had to fall back to .NET's compiler.</summary>
    public static Func<Person, bool> DemoFast(string text) =>
        Demo.ToExpression<Person>(text).CompileFast(ifFastFailedReturnNull: true)
        ?? throw new InvalidOperationException("FastExpressionCompiler could not compile the rule");

    public static LambdaExpression DynamicLinq(string text) =>
        DynamicExpressionParser.ParseLambda([Expression.Parameter(typeof(Person), "Person")], typeof(bool), text);

    public static RulesEngine.RulesEngine Engine(string text) => new(
    [
        new Workflow { WorkflowName = "check", Rules = [new RulesEngine.Models.Rule { RuleName = "rule", Expression = text }] },
    ]);

    public static readonly Person[] People =
    [
        new("Ada", 36, "SE", 185_000m, new Address("London", 1815)),
        new("Tim", 17, "SE", 0m, new Address("Leeds", 2000)),
        new("Bob", 52, "PM", 120_000.50m, new Address("Tampa", 33602)),
        new("Eve", 29, "QA", 40_000m, new Address("London", 1000)),
    ];
}

/// <summary>
/// Checking one object against a rule that is already compiled: the RebolParse rules sample against a lambda
/// written in C#, DynamicExpresso, System.Linq.Dynamic.Core (compiled by .NET and by FastExpressionCompiler)
/// and Microsoft's RulesEngine.
/// </summary>
[MemoryDiagnoser]
public class ObjectRuleCheck
{
    [Params("Simple", "Nested")] public string Rule = "Simple";

    private Person person = RuleTexts.People[0];
    private Func<Person, bool> handwritten = null!, rebolParse = null!, rebolParseFast = null!, expresso = null!, dynamicLinq = null!, dynamicLinqFast = null!;
    private Lambda expressoLambda = null!;
    private RulesEngine.RulesEngine engine = null!;

    [GlobalSetup]
    public void Setup()
    {
        var text = RuleTexts.For(Rule);
        handwritten = RuleTexts.Handwritten(Rule);
        rebolParse = RuleTexts.Demo.Compile<Person>(text);
        rebolParseFast = RuleTexts.DemoFast(text);
        expresso = RuleTexts.Expresso.ParseAsDelegate<Func<Person, bool>>(text, "Person");
        expressoLambda = RuleTexts.Expresso.Parse(text, new Parameter("Person", typeof(Person)));
        dynamicLinq = (Func<Person, bool>)RuleTexts.DynamicLinq(text).Compile();
        dynamicLinqFast = (Func<Person, bool>)RuleTexts.DynamicLinq(text).CompileFast();
        engine = RuleTexts.Engine(text);

        // every engine must give the same answer for every person before anything is timed
        foreach (var p in RuleTexts.People)
        {
            var expected = handwritten(p);
            person = p;
            (string Name, bool Got)[] answers =
            [
                ("RebolParse", rebolParse(p)), ("RebolParse fast", rebolParseFast(p)), ("DynamicExpresso", expresso(p)), ("DynamicExpresso Invoke", DynamicExpressoInvoke()),
                ("Dynamic.Core", dynamicLinq(p)), ("Dynamic.Core fast", dynamicLinqFast(p)), ("RulesEngine", MicrosoftRulesEngine().Result),
            ];
            foreach (var (name, got) in answers)
                if (got != expected) throw new InvalidOperationException($"{name} says {got} for {p.Name} on the {Rule} rule; expected {expected}");
        }
        person = RuleTexts.People[0];   // Ada passes both rules, so every clause runs
    }

    [Benchmark(Baseline = true)] public bool Handwritten() => handwritten(person);
    [Benchmark] public bool RebolParseRules() => rebolParse(person);
    /// <summary>No wrapper that refuses a null object, so this is one delegate call, not two.</summary>
    [Benchmark] public bool RebolParseRulesCompileFast() => rebolParseFast(person);
    [Benchmark] public bool DynamicExpresso() => expresso(person);
    /// <summary>The untyped call: arguments are boxed into an array and the delegate is invoked dynamically.</summary>
    [Benchmark] public bool DynamicExpressoInvoke() => (bool)expressoLambda.Invoke(person);
    [Benchmark] public bool DynamicLinq() => dynamicLinq(person);
    [Benchmark] public bool DynamicLinqCompileFast() => dynamicLinqFast(person);
    [Benchmark] public async Task<bool> MicrosoftRulesEngine() => (await engine.ExecuteAllRulesAsync("check", new RuleParameter("Person", person)))[0].IsSuccess;
}

/// <summary>Turning rule text into something that can check an object: the cost paid once per rule.</summary>
[MemoryDiagnoser]
public class ObjectRuleCompile
{
    [Params("Simple", "Nested")] public string Rule = "Simple";

    private string text = "";

    [GlobalSetup]
    public void Setup() => text = RuleTexts.For(Rule);

    [Benchmark(Baseline = true)] public Func<Person, bool> RebolParseRules() => RuleTexts.Demo.Compile<Person>(text);
    [Benchmark] public Func<Person, bool> RebolParseRulesCompileFast() => RuleTexts.DemoFast(text);
    /// <summary>Reading the text and checking it against the type, without compiling: what is left when the IL step is taken out.</summary>
    [Benchmark] public Expression<Func<Person, bool>> RebolParseRulesTreeOnly() => RuleTexts.Demo.ToExpression<Person>(text);
    [Benchmark] public Func<Person, bool> DynamicExpresso() => RuleTexts.Expresso.ParseAsDelegate<Func<Person, bool>>(text, "Person");
    [Benchmark] public Func<Person, bool> DynamicLinq() => (Func<Person, bool>)RuleTexts.DynamicLinq(text).Compile();
    [Benchmark] public Func<Person, bool> DynamicLinqCompileFast() => (Func<Person, bool>)RuleTexts.DynamicLinq(text).CompileFast();
    /// <summary>RulesEngine compiles a rule the first time it runs, so this builds an engine and runs it once.</summary>
    [Benchmark] public async Task<bool> MicrosoftRulesEngine() =>
        (await RuleTexts.Engine(text).ExecuteAllRulesAsync("check", new RuleParameter("Person", RuleTexts.People[0])))[0].IsSuccess;
}

/// <summary>The engines that compile to a typed delegate, on generated rules of growing size. Shared by the two classes below.</summary>
public abstract class ObjectRuleScale
{
    /// <summary>How many comparisons the rule holds.</summary>
    [Params(1, 4, 16, 64)] public int Clauses = 1;

    protected string Text = "";

    protected Func<Person, bool> RebolParse() => RuleTexts.Demo.Compile<Person>(Text);
    protected Func<Person, bool> RebolParseFast() => RuleTexts.DemoFast(Text);
    protected Func<Person, bool> Expresso() => RuleTexts.Expresso.ParseAsDelegate<Func<Person, bool>>(Text, "Person");
    protected Func<Person, bool> Linq() => (Func<Person, bool>)RuleTexts.DynamicLinq(Text).Compile();
    protected Func<Person, bool> LinqFast() =>
        (Func<Person, bool>)(RuleTexts.DynamicLinq(Text).CompileFast(ifFastFailedReturnNull: true)
                             ?? throw new InvalidOperationException("FastExpressionCompiler could not compile the rule"));
}

/// <summary>Checking a thousand varied people against one compiled rule; the time is per person.</summary>
[MemoryDiagnoser]
public class ObjectRuleScaleCheck : ObjectRuleScale
{
    private Func<Person, bool> rebolParse = null!, rebolParseFast = null!, expresso = null!, linq = null!, linqFast = null!;

    [GlobalSetup]
    public void Setup()
    {
        Text = RuleTexts.Generated(Clauses);
        (rebolParse, rebolParseFast, expresso, linq, linqFast) = (RebolParse(), RebolParseFast(), Expresso(), Linq(), LinqFast());
        foreach (var p in RuleTexts.Crowd)
            if (new[] { rebolParseFast(p), expresso(p), linq(p), linqFast(p) }.Any(answer => answer != rebolParse(p)))
                throw new InvalidOperationException($"the engines disagree about {p} on the {Clauses}-clause rule");
        var passed = Count(rebolParse);
        if (passed is 0 or 1000) throw new InvalidOperationException($"the {Clauses}-clause rule gives everyone the same answer, so it measures nothing");
    }

    private static int Count(Func<Person, bool> check)
    {
        var passed = 0;
        foreach (var p in RuleTexts.Crowd) if (check(p)) passed++;
        return passed;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = 1000)] public int RebolParseRules() => Count(rebolParse);
    [Benchmark(OperationsPerInvoke = 1000)] public int RebolParseRulesCompileFast() => Count(rebolParseFast);
    [Benchmark(OperationsPerInvoke = 1000)] public int DynamicExpresso() => Count(expresso);
    [Benchmark(OperationsPerInvoke = 1000)] public int DynamicLinq() => Count(linq);
    [Benchmark(OperationsPerInvoke = 1000)] public int DynamicLinqCompileFast() => Count(linqFast);
}

/// <summary>Compiling one generated rule from text.</summary>
[MemoryDiagnoser]
public class ObjectRuleScaleCompile : ObjectRuleScale
{
    [GlobalSetup]
    public void Setup() => Text = RuleTexts.Generated(Clauses);

    [Benchmark(Baseline = true)] public Func<Person, bool> RebolParseRules() => RebolParse();
    [Benchmark] public Func<Person, bool> RebolParseRulesCompileFast() => RebolParseFast();
    [Benchmark] public Expression<Func<Person, bool>> RebolParseRulesTreeOnly() => RuleTexts.Demo.ToExpression<Person>(Text);
    [Benchmark] public Func<Person, bool> DynamicExpresso() => Expresso();
    [Benchmark] public Func<Person, bool> DynamicLinq() => Linq();
    [Benchmark] public Func<Person, bool> DynamicLinqCompileFast() => LinqFast();
}

/// <summary>
/// Checking an order with a deeper shape or JSON metadata, per order, using rules from <see cref="Scenarios"/>
/// that every engine can run. Each engine reads the rule in its own spelling; the JSON rules call
/// <c>JsonElement</c> methods in the libraries and use JSON paths in the sample.
/// </summary>
[MemoryDiagnoser]
public class ObjectRuleShapes
{
    [ParamsSource(nameof(Shapes))] public string Shape = "";

    public virtual IEnumerable<string> Shapes => ["DeepGuarded", "ListItems", "JsonNumber", "JsonString", "JsonMixed", "JsonHelper"];

    private static readonly Order[] Orders = [Scenarios.Full, Scenarios.Other];   // one passes, one fails, neither makes a library throw
    private Func<Order, bool> handwritten = null!, sample = null!, sampleFast = null!, expresso = null!, linq = null!;

    [GlobalSetup]
    public void Setup()
    {
        var scenario = Scenarios.Named(Shape switch
        {
            "DeepGuarded" => "Deep path, guarded against nulls",
            "ListItems" => "List count, then two items",
            "JsonNumber" => "JSON number",
            "JsonString" => "JSON nested string",
            "JsonMixed" => "JSON and properties together",
            "JsonHelper" => "JSON through a registered helper",
            "TwoStrings" => "Both sides: two strings, deep paths",
            "IntAndDecimal" => "Both sides: an int and a decimal",
            "EnumAndString" => "Both sides: an enum and a string",
            "JsonAndString" => "Both sides: JSON and a string property",
            "JsonAndNumber" => "Both sides: JSON and a number property",
            "TwoJsonNumbers" => "Both sides: two JSON numbers",
            "Arithmetic" => "Arithmetic on both sides",
            "MinMax" => "min and max",
            "ArithmeticJson" => "Arithmetic with JSON and min",
            _ => throw new ArgumentException(Shape),
        });
        handwritten = scenario.Expected;
        sample = Scenarios.SampleBare(scenario.Sample!);
        // FastExpressionCompiler declines some trees (calculations on decimal? values among them). An application would
        // then use .NET's compiler, so that is what is timed, and the run says so.
        var fast = RuleTexts.Demo.ToExpression<Order>(scenario.Sample!).CompileFast(ifFastFailedReturnNull: true);
        if (fast == null) Console.WriteLine($"// FastExpressionCompiler declined {Shape}; timing .NET's compiler in its place");
        sampleFast = fast ?? sample;
        expresso = Scenarios.Expresso(scenario.Expresso!);
        linq = Scenarios.LinqRegistered(scenario.Linq!);
        foreach (var order in Orders)
            if (new[] { sample(order), sampleFast(order), expresso(order), linq(order) }.Any(answer => answer != handwritten(order)))
                throw new InvalidOperationException($"the engines disagree on {Shape}");
    }

    private static int Count(Func<Order, bool> check)
    {
        var passed = 0;
        foreach (var order in Orders) if (check(order)) passed++;
        return passed;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = 2)] public int Handwritten() => Count(handwritten);
    [Benchmark(OperationsPerInvoke = 2)] public int RebolParseRules() => Count(sample);
    [Benchmark(OperationsPerInvoke = 2)] public int RebolParseRulesCompileFast() => Count(sampleFast);
    [Benchmark(OperationsPerInvoke = 2)] public int DynamicExpresso() => Count(expresso);
    [Benchmark(OperationsPerInvoke = 2)] public int DynamicLinq() => Count(linq);
}

/// <summary>The same measurement for rules with a property, a list item or JSON on both sides of the operator.</summary>
public class ObjectRuleBothSides : ObjectRuleShapes
{
    public override IEnumerable<string> Shapes => ["TwoStrings", "IntAndDecimal", "EnumAndString", "JsonAndString", "JsonAndNumber", "TwoJsonNumbers"];
}

/// <summary>The same measurement for rules that calculate: <c>+ - * /</c>, <c>min</c> and <c>max</c>.</summary>
public class ObjectRuleArithmetic : ObjectRuleShapes
{
    public override IEnumerable<string> Shapes => ["Arithmetic", "MinMax", "ArithmeticJson"];
}
