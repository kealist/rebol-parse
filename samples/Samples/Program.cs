using Samples;

// dotnet run --project samples/Samples            run every demo
// dotnet run --project samples/Samples -- json    run one demo
// dotnet run --project samples/Samples -- list    list the demos
var wanted = args.Where(a => !a.StartsWith('-')).ToList();
if (wanted.Contains("list"))
{
    foreach (var d in Demos.All) Console.WriteLine($"{d.Name,-10} {d.Title}");
    return 0;
}

var chosen = wanted.Count == 0
    ? Demos.All
    : wanted.Select(w => Demos.All.FirstOrDefault(d => d.Name.Equals(w, StringComparison.OrdinalIgnoreCase))
                         ?? throw new ArgumentException($"No demo called '{w}'. Try 'list'.")).ToList();

var failed = Demos.Run(chosen);
Console.WriteLine(failed == 0 ? "\nAll checks passed." : $"\n{failed} check(s) failed.");
return failed == 0 ? 0 : 1;
