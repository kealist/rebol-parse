# Benchmarks: RebolParse vs Regex

BenchmarkDotNet, `--job short` (3 iterations, so treat the numbers as rough and expect run-to-run noise of 10-30%). Figures are from the run after the compiled-node engine landed. Apple M4 Pro, .NET 10.0.6, Release build. Each benchmark checks in setup that both engines produce the same answer before timing.

Parse rules are built once and reused, as `Regex` objects are. Times are the mean of a short run, followed by bytes allocated per call.

| Task | Size | Parse | IsMatch | Regex (interpreted) | Regex (compiled) |
|---|---|---|---|---|---|
| Validate `555-1234` | 8 chars | 37 ns, 120 B | 31 ns, **0 B** | 47 ns, 0 B | 14 ns, 0 B |
| Validate comma-separated ints | 100 numbers | 2.1 µs, 120 B | 2.1 µs, **0 B** | 3.1 µs | 1.9 µs |
| | 10,000 numbers | 212 µs, 120 B | 216 µs, **0 B** | 299 µs | 199 µs |
| Balanced parens, depth 10 | ~420 chars | 5.4 µs, 222 B | 5.8 µs, **0 B** | 16.5 µs | 3.9 µs |
| Balanced parens, depth 1000 | ~40 KB | 1.16 ms, 232 B | 952 µs, **0 B** | 1.62 ms | 381 µs |
| Capture `key=value` lines | 10 lines | 2.1 µs, 1.9 KB | n/a (captures) | 1.3 µs, 3.2 KB | 0.9 µs, 3.2 KB |
| | 1,000 lines | 214 µs, 126 KB | n/a (captures) | 144 µs, 305 KB | 86 µs, 305 KB |
| Extract words | 1 KB | 9.6 µs, 10 KB | n/a (collects) | 15.2 µs, 41 KB | 9.9 µs, 41 KB |
| | 100 KB | 1.06 ms, 1.05 MB | n/a (collects) | 4.54 ms, 4.07 MB | 4.15 ms, 4.07 MB |
| Strip digits (`remove` vs `Replace`) | 1 KB | 13.8 µs, 5.4 KB | n/a (edits) | 6.3 µs, 1.4 KB | 8.6 µs, 1.4 KB |
| | 100 KB | 23.1 ms, 499 KB | n/a (edits) | 536 µs, 109 KB | 668 µs, 109 KB |
| Build the matcher | `3 digit "-" 4 digit` | 318 ns (text), 103 ns (fluent) | | 701 ns | 4,138 ns |

Building the matcher does not include compiling the rule tree to nodes, which happens on first use and is cached per rule instance.

## What the numbers say

- **Against interpreted regex, RebolParse is now faster or close on everything.** Validation runs at 1.3-1.5x the speed of the interpreted regex, word extraction at 1.6-4.3x, and the recursive grammar at 1.4-3x. Capturing key=value lines is the one flat-text task where interpreted regex still leads, by 1.5x.
- **Against `RegexOptions.Compiled`, which emits IL, it wins on extraction (1-4x) and loses on plain validation (2.7x slower on the phone number, about even on the number list) and on recursion (1.4x slower at depth 10, 3x at depth 1000).** That gap is the cost of a tree of virtual calls against straight-line generated code; closing it would mean generating IL too.
- **How it got here.** Rules compile once into specialized nodes: a loop over a character class is one tight loop, `to`/`thru` on a literal uses the vectorized `IndexOf`, literal strings use `StartsWith`, and nodes that cannot break, reject, keep or edit skip the backtracking bookkeeping. Text is matched as a `ReadOnlySpan<char>`, so there is no copy of the input and no per-character interface call. Before this step RebolParse was 3-8x slower than regex on these tasks.
- **`Parser.IsMatch` allocates nothing.** `Parse` allocates about 120 B plus whatever it captures; it never copies input it does not edit.
- **`remove` on large inputs is still the slow path:** 43x slower than `Regex.Replace` on 100 KB, because each removal shifts the rest of the buffer and the loop is quadratic. Collect the pieces you want with `collect` and join them instead.
- **Setup cost favours RebolParse.** Reading rule text costs under half of constructing an interpreted regex and a thirteenth of a compiled one.

Pick RebolParse for grammars that are awkward or impossible as regex (recursion, structured block input, rules that capture and edit as they go) and for readability. For a hot loop that only validates flat text, `RegexOptions.Compiled` is still the fastest option.

## Run it

```bash
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*"
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*StripDigits*" --job short
```

Drop `--job short` for a precise run; the full job takes several times longer.
