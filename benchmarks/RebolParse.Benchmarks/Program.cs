using BenchmarkDotNet.Running;
using RebolParse.Benchmarks;

// dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*"
// dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- scenarios     what each rule library does with deep objects and JSON
// dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- safety        what each rule library lets rule text call
if (args is ["scenarios", ..])
{
    Scenarios.Print();
    return;
}

if (args is ["safety", ..])
{
    Scenarios.PrintSafety();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
