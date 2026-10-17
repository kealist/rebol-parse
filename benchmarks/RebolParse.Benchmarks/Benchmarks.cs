using System.Text;
using System.Text.RegularExpressions;
using BenchmarkDotNet.Attributes;
using RebolParse;
using static RebolParse.P;

namespace RebolParse.Benchmarks;

/// <summary>Shared checks so a benchmark can never quietly compare two different answers.</summary>
internal static class Guard
{
    public static void Same<T>(string what, T parse, T regex)
    {
        if (!EqualityComparer<T>.Default.Equals(parse, regex))
            throw new InvalidOperationException($"{what}: parse={parse} regex={regex}");
    }
}

/// <summary>Whole-input validation of a tiny string: the fixed per-call cost of each engine.</summary>
[MemoryDiagnoser]
public class PhoneNumber
{
    private const string Input = "555-1234";
    private readonly Rule rule = Dialect.Read("3 digit \"-\" 4 digit");
    private readonly Regex interpreted = new("^[0-9]{3}-[0-9]{4}$");
    private readonly Regex compiled = new("^[0-9]{3}-[0-9]{4}$", RegexOptions.Compiled);

    [GlobalSetup]
    public void Setup() => Guard.Same("phone", IsMatch(), compiled.IsMatch(Input));

    [Benchmark(Baseline = true)] public bool Parse() => Parser.Parse(Input, rule).Matched;
    [Benchmark] public bool IsMatch() => Parser.IsMatch(Input, rule);
    [Benchmark] public bool RegexInterpreted() => interpreted.IsMatch(Input);
    [Benchmark] public bool RegexCompiled() => compiled.IsMatch(Input);
}

/// <summary>Whole-input validation of a long comma-separated list of integers.</summary>
[MemoryDiagnoser]
public class NumberList
{
    private string input = "";
    private readonly Rule rule = Dialect.Read("some digit any [\",\" some digit]");
    private readonly Regex interpreted = new("^[0-9]+(,[0-9]+)*$");
    private readonly Regex compiled = new("^[0-9]+(,[0-9]+)*$", RegexOptions.Compiled);

    [Params(100, 10_000)] public int Count;

    [GlobalSetup]
    public void Setup()
    {
        input = string.Join(",", Enumerable.Range(1, Count).Select(i => (i * 7919 % 100_000).ToString()));
        Guard.Same("numbers", Parse(), compiled.IsMatch(input));
    }

    [Benchmark(Baseline = true)] public bool Parse() => Parser.Parse(input, rule).Matched;
    [Benchmark] public bool IsMatch() => Parser.IsMatch(input, rule);
    [Benchmark] public bool RegexInterpreted() => interpreted.IsMatch(input);
    [Benchmark] public bool RegexCompiled() => compiled.IsMatch(input);
}

/// <summary>Capture every key=value pair in a block of lines.</summary>
[MemoryDiagnoser]
public class KeyValueLines
{
    private string input = "";
    private readonly Rule rule = Dialect.Read(
        "collect out any [keep some [not [\"=\" | newline] skip] \"=\" keep any [not newline skip] opt newline]");
    private readonly Regex interpreted = new("([^=\n]+)=([^\n]*)");
    private readonly Regex compiled = new("([^=\n]+)=([^\n]*)", RegexOptions.Compiled);

    [Params(10, 1_000)] public int Lines;

    [GlobalSetup]
    public void Setup()
    {
        input = string.Join("\n", Enumerable.Range(0, Lines).Select(i => $"key{i}=value number {i}"));
        Guard.Same("kv", ParseCount(), RegexCount(compiled));
    }

    [Benchmark(Baseline = true)]
    public int ParseCount() =>
        ((IReadOnlyList<object>)Parser.Parse(input, rule).Vars["out"]!).Count / 2;

    private int RegexCount(Regex r) => r.Matches(input).Count;
    [Benchmark] public int RegexInterpreted() => RegexCount(interpreted);
    [Benchmark] public int RegexCompiled() => RegexCount(compiled);
}

/// <summary>Pull every word out of a block of prose.</summary>
[MemoryDiagnoser]
public class ExtractWords
{
    private string input = "";
    private readonly Rule rule = Dialect.Read("collect w any [keep some alpha | skip]");
    private readonly Regex interpreted = new("[A-Za-z]+");
    private readonly Regex compiled = new("[A-Za-z]+", RegexOptions.Compiled);

    [Params(1_000, 100_000)] public int Chars;

    [GlobalSetup]
    public void Setup()
    {
        var words = new[] { "the", "quick", "brown", "fox", "jumps", "over", "lazy", "dog", "42", "again," };
        var sb = new StringBuilder();
        for (var i = 0; sb.Length < Chars; i++) sb.Append(words[i % words.Length]).Append(' ');
        input = sb.ToString();
        Guard.Same("words", ParseCount(), compiled.Matches(input).Count);
    }

    [Benchmark(Baseline = true)]
    public int ParseCount() => ((IReadOnlyList<object>)Parser.Parse(input, rule, partial: true).Vars["w"]!).Count;
    [Benchmark] public int RegexInterpreted() => interpreted.Matches(input).Count;
    [Benchmark] public int RegexCompiled() => compiled.Matches(input).Count;
}

/// <summary>Strip every run of digits from a string: <c>remove</c> against <c>Regex.Replace</c>.</summary>
[MemoryDiagnoser]
public class StripDigits
{
    private string input = "";
    private readonly Rule rule = Dialect.Read("any [remove some digit | skip]");
    private readonly Regex interpreted = new("[0-9]+");
    private readonly Regex compiled = new("[0-9]+", RegexOptions.Compiled);

    [Params(1_000, 100_000)] public int Chars;

    [GlobalSetup]
    public void Setup()
    {
        var sb = new StringBuilder();
        for (var i = 0; sb.Length < Chars; i++) sb.Append("item").Append(i).Append(' ');
        input = sb.ToString();
        Guard.Same("strip", (string)Parser.Parse(input, rule).Output!, compiled.Replace(input, ""));
    }

    [Benchmark(Baseline = true)] public object? Parse() => Parser.Parse(input, rule).Output;
    [Benchmark] public string RegexInterpreted() => interpreted.Replace(input, "");
    [Benchmark] public string RegexCompiled() => compiled.Replace(input, "");
}

/// <summary>
/// Nested parentheses. The parse rule recurses; .NET needs balancing groups to do the same thing in a regex.
/// </summary>
[MemoryDiagnoser]
public class BalancedParens
{
    private string input = "";
    private readonly Dictionary<string, Rule> rules = Dialect.ReadRules(new Dictionary<string, string>
    {
        ["group"] = "\"(\" any group \")\"",
    });
    private static readonly Rule group = Ref("group");
    private const string Pattern = @"^(?:[^()]|(?<open>\()|(?<-open>\)))*(?(open)(?!))$";
    private readonly Regex interpreted = new(Pattern);
    private readonly Regex compiled = new(Pattern, RegexOptions.Compiled);

    [Params(10, 1_000)] public int Depth;

    [GlobalSetup]
    public void Setup()
    {
        // Depth nested groups, repeated side by side inside one outer group
        var unit = new string('(', Depth) + new string(')', Depth);
        input = "(" + string.Concat(Enumerable.Repeat(unit, 20)) + ")";
        Guard.Same("parens", Parse(), compiled.IsMatch(input));
    }

    [Benchmark(Baseline = true)] public bool Parse() => Parser.Parse(input, Ref("group"), rules).Matched;
    [Benchmark] public bool IsMatch() => Parser.IsMatch(input, group, rules);
    [Benchmark] public bool RegexInterpreted() => interpreted.IsMatch(input);
    [Benchmark] public bool RegexCompiled() => compiled.IsMatch(input);
}

/// <summary>The cost of turning pattern text into something runnable.</summary>
[MemoryDiagnoser]
public class Construction
{
    [Benchmark(Baseline = true)] public Rule ReadRule() => Dialect.Read("3 digit \"-\" 4 digit");
    [Benchmark] public Rule BuildFluent() => Rep(3, Digit) + "-" + Rep(4, Digit);
    [Benchmark] public Regex RegexInterpreted() => new("^[0-9]{3}-[0-9]{4}$");
    [Benchmark] public Regex RegexCompiled() => new("^[0-9]{3}-[0-9]{4}$", RegexOptions.Compiled);
}
