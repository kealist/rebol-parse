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
public sealed record Person(string Name, int Age, string Job, decimal Salary, Address Address);

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
