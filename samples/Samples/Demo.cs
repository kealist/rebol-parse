namespace Samples;

/// <summary>One self-contained example. Each demo prints what it does and checks its own results.</summary>
public interface IDemo
{
    /// <summary>The short name used on the command line.</summary>
    string Name { get; }

    /// <summary>A one-line description.</summary>
    string Title { get; }

    /// <summary>Run the demo, reporting through <paramref name="report"/>.</summary>
    void Run(Report report);
}

/// <summary>Collects a demo's printed output and its pass/fail checks.</summary>
public sealed class Report(bool quiet = false)
{
    public int Passed { get; private set; }
    public List<string> Failures { get; } = [];

    /// <summary>Print a line of demo output.</summary>
    public void Say(string line = "")
    {
        if (!quiet) Console.WriteLine("    " + line.Replace("\n", "\n    "));
    }

    /// <summary>Check that <paramref name="actual"/> equals <paramref name="expected"/>.</summary>
    public void Expect<T>(string what, T actual, T expected)
    {
        if (EqualityComparer<T>.Default.Equals(actual, expected))
        {
            Passed++;
            if (!quiet) Console.WriteLine($"  ok   {what}");
        }
        else
        {
            var message = $"{what}: expected [{expected}] but got [{actual}]";
            Failures.Add(message);
            Console.WriteLine($"  FAIL {message}");
        }
    }

    /// <summary>Check that a condition holds.</summary>
    public void Expect(string what, bool condition) => Expect(what, condition, true);
}

/// <summary>Every demo, in the order they run.</summary>
public static class Demos
{
    public static IReadOnlyList<IDemo> All { get; } =
    [
        new Calculator(),
        new JsonDemo(),
        new IniDemo(),
        new CsvDemo(),
        new LogDemo(),
        new SourceCleaner(),
        new RobotDemo(),
        new TemplateDemo(),
    ];

    /// <summary>Run <paramref name="demos"/> and return the total number of failed checks.</summary>
    public static int Run(IEnumerable<IDemo> demos, bool quiet = false)
    {
        var failed = 0;
        foreach (var demo in demos)
        {
            if (!quiet) Console.WriteLine($"\n== {demo.Name}: {demo.Title}");
            var report = new Report(quiet);
            try { demo.Run(report); }
            catch (Exception ex) { report.Failures.Add($"threw {ex.GetType().Name}: {ex.Message}"); Console.WriteLine($"  FAIL {demo.Name} threw {ex}"); }
            failed += report.Failures.Count;
            if (!quiet) Console.WriteLine($"  -> {report.Passed} passed, {report.Failures.Count} failed");
        }
        return failed;
    }
}
