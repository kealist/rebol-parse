# Benchmarks: RebolParse vs Regex

BenchmarkDotNet, `--job short` (3 iterations, so treat the numbers as rough and expect run-to-run noise of 10-30%). Figures are from the second run, after the allocation work. Apple M4 Pro, .NET 10.0.6, Release build. Each benchmark checks in setup that both engines produce the same answer before timing.

Parse rules are built once and reused, as `Regex` objects are. Times are the mean of a short run, followed by bytes allocated per call.

| Task | Size | Parse | IsMatch | Regex (interpreted) | Regex (compiled) |
|---|---|---|---|---|---|
| Validate `555-1234` | 8 chars | 167 ns, 368 B | 128 ns, **0 B** | 47 ns, 0 B | 14 ns, 0 B |
| Validate comma-separated ints | 100 numbers | 10.3 µs, 368 B | 10.0 µs, **0 B** | 3.1 µs | 1.9 µs |
| | 10,000 numbers | 1.00 ms, 368 B | 1.00 ms, **0 B** | 297 µs | 199 µs |
| Balanced parens, depth 10 | ~420 chars | 26.0 µs, 392 B | 25.9 µs, **0 B** | 16.5 µs | 3.9 µs |
| Balanced parens, depth 1000 | ~40 KB | 2.65 ms, 392 B | 2.61 ms, **0 B** | 1.62 ms | 390 µs |
| Capture `key=value` lines | 10 lines | 7.2 µs, 2.0 KB | n/a (captures) | 1.3 µs, 3.2 KB | 0.9 µs, 3.2 KB |
| | 1,000 lines | 801 µs, 126 KB | n/a (captures) | 147 µs, 305 KB | 81 µs, 305 KB |
| Extract words | 1 KB | 34.5 µs, 10.3 KB | n/a (collects) | 15.6 µs, 40.7 KB | 15.8 µs, 40.7 KB |
| | 100 KB | 3.55 ms, 1.05 MB | n/a (collects) | 4.79 ms, 4.07 MB | 4.17 ms, 4.07 MB |
| Strip digits (`remove` vs `Replace`)* | 1 KB | 49.8 µs | n/a (edits) | 6.5 µs | 4.4 µs |
| | 100 KB | 27.0 ms | n/a (edits) | 564 µs | 675 µs |
| Build the matcher* | `3 digit "-" 4 digit` | 326 ns (text), 105 ns (fluent) | | 703 ns | 4,183 ns |

\* Not re-measured after the allocation work; figures are from the first run.

## What the numbers say

- **Regex is still faster on flat text:** about 3-8x for validation and capture. Removing allocations did not change that, because the time goes into walking the rule tree and virtual calls, not the allocator. The one place RebolParse wins is large-text extraction, where it is about 25% faster and allocates a quarter of the memory.
- **`Parser.IsMatch` allocates nothing.** It reuses one context, matcher and pair of series per thread, and tests string characters without boxing them. A test (`AllocationTests`) measures 0 bytes per call for validation rules covering sequences, choice, repeats, `to`/`thru`, lookahead, marks, recursion, `into`, actions, `if` and ignore-case. It does not apply to rules that `copy`, `set` or `collect`, which must allocate what they return, or that edit input.
- **`Parser.Parse` allocates much less than before.** It no longer copies the input unless a rule edits it, and `Output` is the original string or list when nothing changed. Boxing every character tested also went away: validating 10,000 numbers dropped from 1.33 ms to 1.00 ms, and extracting words from 100 KB from 5.2 ms to 3.5 ms. What remains is the result object, the parse context and the captured values themselves.
- **`remove` is still the outlier,** 48x slower than `Regex.Replace` on 100 KB, because each removal shifts the rest of the buffer. Collect the pieces you want with `collect` and join them instead.
- **Recursion is where parse holds up best.** The balanced-parentheses rule is the natural way to write that grammar, and it is within 1.6x of the interpreted regex that needs .NET-specific balancing groups.
- **Setup cost favours RebolParse.** Reading rule text costs under half of constructing an interpreted regex and a twelfth of a compiled one.

Pick RebolParse for grammars that are awkward or impossible as regex (recursion, structured block input, rules that capture and edit as they go) and readability. For a hot loop over flat text, use a regex.

## Run it

```bash
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*"
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*StripDigits*" --job short
```

Drop `--job short` for a precise run; the full job takes several times longer.
