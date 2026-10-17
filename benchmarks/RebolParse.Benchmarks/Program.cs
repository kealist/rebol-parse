using BenchmarkDotNet.Running;

// dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*"
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
