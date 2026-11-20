using BenchmarkDotNet.Running;
using RebolParse.Benchmarks;

// dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*"
// dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- scenarios     what each rule library does with deep objects and JSON
if (args is ["scenarios", ..])
{
    Scenarios.Print();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
