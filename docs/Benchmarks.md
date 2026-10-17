# Benchmarks: RebolParse vs Regex

BenchmarkDotNet, `--job short` (3 iterations, so treat the numbers as rough and expect run-to-run noise of 10-30%). Apple M4 Pro, .NET 10.0.6, Release build. Each benchmark checks in setup that both engines produce the same answer before timing.

Parse rules are built once and reused, as `Regex` objects are. Ratio is relative to RebolParse; below 1 means the regex is faster.

| Task | Size | RebolParse | Regex (interpreted) | Regex (compiled) |
|---|---|---|---|---|
| Validate `555-1234` | 8 chars | 219 ns, 792 B | 48 ns (0.22x), 0 B | 14 ns (0.06x), 0 B |
| Validate comma-separated ints | 100 numbers | 13.0 µs | 3.1 µs (0.24x) | 1.9 µs (0.15x) |
| | 10,000 numbers | 1.33 ms | 303 µs (0.23x) | 199 µs (0.15x) |
| Capture `key=value` lines | 10 lines | 7.9 µs | 1.4 µs (0.18x) | 0.9 µs (0.11x) |
| | 1,000 lines | 834 µs | 150 µs (0.18x) | 87 µs (0.10x) |
| Extract words | 1 KB | 39.5 µs | 15.2 µs (0.39x) | 16.3 µs (0.41x) |
| | 100 KB | 5.23 ms | 4.74 ms (0.91x) | 4.31 ms (0.83x) |
| Strip digits (`remove` vs `Replace`) | 1 KB | 49.8 µs | 6.5 µs (0.13x) | 4.4 µs (0.09x) |
| | 100 KB | 27.0 ms | 564 µs (0.02x) | 675 µs (0.02x) |
| Balanced parens, depth 10 | ~420 chars | 27.0 µs | 17.1 µs (0.63x) | 3.9 µs (0.15x) |
| Balanced parens, depth 1000 | ~40 KB | 2.65 ms | 1.64 ms (0.62x) | 389 µs (0.15x) |
| Build the matcher | `3 digit "-" 4 digit` | 326 ns (text), 105 ns (fluent) | 703 ns | 4,183 ns |

## What the numbers say

- **Regex is faster everywhere.** For plain validation and capture it is 4-17x faster than RebolParse. That is expected: .NET's regex engine is heavily optimized and, when compiled, emits IL, whereas RebolParse walks a rule tree.
- **Extraction on large text is close.** On 100 KB of prose both engines spend most of their time allocating the result list, and parse is within 10-20%.
- **Allocations are the main cost.** Every `Parser.Parse` call allocates a context, a copy of the input and a result (792 B even for an 8-character string), and captured text is allocated per `keep`/`copy`. Regex validation allocates nothing.
- **`remove` is the outlier: 48x slower on 100 KB.** Each removal shifts the rest of the buffer, so many removals on a large string is quadratic. If you do bulk editing of large inputs, collect the pieces you want with `collect` and join them instead.
- **Recursion is where parse holds up best.** The balanced-parentheses rule is the natural way to write that grammar, and it runs within 1.6x of the interpreted regex that needs .NET-specific balancing groups. Compiled regex is still about 7x faster.
- **Setup cost favours RebolParse.** Reading rule text costs a third of constructing an interpreted regex, and a twelfth of a compiled one.

Pick RebolParse for grammars that are awkward or impossible as regex (recursion, structured block input, rules that capture and edit as they go) and readability. For a hot loop over flat text, use a regex.

## Run it

```bash
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*"
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*StripDigits*" --job short
```

Drop `--job short` for a precise run; the full job takes several times longer.
