# Benchmarks: RebolParse vs Regex and System.Text.Json

BenchmarkDotNet, `--job short` (3 iterations, so treat the numbers as rough and expect run-to-run noise of 10-30%; the regex rows in particular moved by that much between runs). Apple M4 Pro, .NET 10.0.6, Release build. Each benchmark checks in setup that every engine produces the same answer before timing.

Rules are built once and reused, as `Regex` objects are. Times are the mean per call. **IL** means the rule after `.Compiled()` (see [Compiling to IL](Reference.md#compiling-to-il)). Validation rows call `IsMatch`; the rows that capture or edit call `Parse`.

## Against .NET Regex

Each table compares like with like: the RebolParse interpreter against interpreted `Regex`, then RebolParse IL against `RegexOptions.Compiled`. The last column says how much faster or slower RebolParse is. Differences under about 30% are within run-to-run noise.

### Interpreted

| Task | Input | RebolParse | Regex | RebolParse is |
|---|---|---:|---:|---|
| Validate `555-1234` | 8 chars | 32 ns | 47 ns | 1.5x faster |
| Validate comma-separated ints | 100 numbers | 2.2 µs | 3.1 µs | 1.4x faster |
| | 10,000 numbers | 217 µs | 303 µs | 1.4x faster |
| Balanced parens | depth 10, ~420 chars | 5.4 µs | 16.7 µs | 3.1x faster |
| | depth 1000, ~40 KB | 904 µs | 1,589 µs | 1.8x faster |
| Capture `key=value` lines | 10 lines | 0.68 µs | 1.3 µs | 1.9x faster |
| | 1,000 lines | 51 µs | 137 µs | 2.7x faster |
| Extract words | 1 KB | 10.7 µs | 15.4 µs | 1.4x faster |
| | 100 KB | 1,170 µs | 4,661 µs | 4.0x faster |
| Strip digits (`remove` vs `Replace`) | 1 KB | 10.0 µs | 6.4 µs | **1.6x slower** |
| | 100 KB | 858 µs | 543 µs | **1.6x slower** |

### Compiled

| Task | Input | RebolParse IL | Regex compiled | RebolParse is |
|---|---|---:|---:|---|
| Validate `555-1234` | 8 chars | 11 ns | 14 ns | 1.3x faster |
| Validate comma-separated ints | 100 numbers | 0.34 µs | 1.9 µs | 5.6x faster |
| | 10,000 numbers | 34 µs | 200 µs | 5.9x faster |
| Balanced parens | depth 10, ~420 chars | 1.4 µs | 3.9 µs | 2.8x faster |
| | depth 1000, ~40 KB | 144 µs | 390 µs | 2.7x faster |
| Capture `key=value` lines | 10 lines | 0.61 µs | 0.88 µs | 1.4x faster |
| | 1,000 lines | 48 µs | 82 µs | 1.7x faster |
| Extract words | 1 KB | 5.6 µs | 10-16 µs | 1.8-2.9x faster |
| | 100 KB | 696 µs | 4,269 µs | 6.1x faster |
| Strip digits (`remove` vs `Replace`) | 1 KB | 5.3 µs | 4.4-8.7 µs | about the same |
| | 100 KB | 422 µs | 596 µs | 1.4x faster |

Where a cell shows a range, that result moved between runs.

### Memory

Bytes allocated per call. The interpreter and IL allocate the same amount.

| Task | Input | RebolParse | Regex |
|---|---|---:|---:|
| Validate (all rows) | any | 0 B | 0 B |
| Capture `key=value` lines | 10 lines | 1.9 KB | 3.2 KB |
| | 1,000 lines | 126 KB | 305 KB |
| Extract words | 1 KB | 10.3 KB | 40.7 KB |
| | 100 KB | 1.05 MB | 4.07 MB |
| Strip digits | 1 KB | 1.5 KB | 1.4 KB |
| | 100 KB | 109 KB | 109 KB |

Calling `Parse` instead of `IsMatch` on a validation rule allocates a result object (128-240 B) and adds a little time, for example 38 ns instead of 32 ns on the phone number.

### Set-up cost

Building the matcher. This does not include the interpreter's compile to nodes, which happens on first use and is cached per rule.

| | Time | Allocated |
|---|---|---|
| Read `3 digit "-" 4 digit` from text | 319 ns | 1.0 KB |
| Build it with the fluent API | 98 ns | 400 B |
| `new Regex(...)` interpreted | 698 ns | 1.9 KB |
| `new Regex(..., RegexOptions.Compiled)` | 4,155 ns | 8.7 KB |
| `.Compiled()` and run once (rule already warm) | 251 µs | 28.7 KB |

### What the numbers say

- **The interpreter** beats interpreted regex on every task except bulk deletion, where it is 1.6x slower.
- **IL** beats `RegexOptions.Compiled` on every task except stripping 1 KB, where the two are level.
- **Allocations.** Captures allocate 2-4x less than `Regex.Matches`. Edits allocate the same as `Regex.Replace` (109 KB for 100 KB of text): the edit buffer is rented from `ArrayPool` and sized to the input, so the result string is the only thing left behind.
- **How it gets there.** Rules compile once into specialized nodes: a loop over a character class is one tight loop, `to`/`thru` on a literal use the vectorized `IndexOf`, `some [not X skip]` is one scan, a choice skips alternatives that cannot start with the next character, and text is matched as a `ReadOnlySpan<char>`. Edits go through a gap buffer, so a left-to-right sweep moves each character at most once. IL removes the per-node calls on top of that, compares short literals inline, writes small rules into the rules that call them (so the JSON grammar's whitespace, string and number rules are not calls), writes a recursive rule into itself three times (so nesting costs a quarter of the calls), calls what is left through a cached delegate, and loads its constants once per loop.
- **IL costs time to build.** The first compile in a process takes about 30 ms, mostly loading and warming the expression compiler. After that a small rule takes under a millisecond and the 7-rule JSON grammar, with its inlining, about 70 ms. A rule you run once is better left interpreted.
- **Set-up cost still favours RebolParse** over a compiled regex: reading rule text costs a tenth or less of constructing one.
- **Noise.** These are `--job short` runs. Some regex results moved by up to 2x between runs, so differences under about 30% are not meaningful.

Pick RebolParse for grammars that are awkward or impossible as regex (recursion, structured block input, rules that capture and edit as they go) and for readability. For a hot loop over flat text, compile the rule: it is as fast as `RegexOptions.Compiled` or faster on everything above.

## Reading properties out of JSON: RebolParse vs System.Text.Json

Three properties per call (for example `author.name`, `tags[1]`, `version`) from a 400-byte document and from arrays of 2,000 records (150 KB) and 200,000 records (11 MB). The RebolParse side uses the grammar from the [JSON sample](../samples/Samples/JsonDemo.cs) two ways: read the whole document into objects and navigate to the paths, or use a rule that skips straight to each property and copies only its text. Each is shown interpreted and compiled to IL. "Start/mid/end" reads the first, middle and last record; "first 3" reads only properties in the first three records, where a parse can stop early.

Time per call. The fastest in each column is in bold.

| | 400 B | 150 KB, start/mid/end | 150 KB, first 3 | 11 MB, start/mid/end | 11 MB, first 3 |
|---|---:|---:|---:|---:|---:|
| `JsonDocument` (string) | 1.36 µs | 506 µs | 506 µs | 52.4 ms | 51.1 ms |
| `JsonDocument` (UTF-8 bytes) | 1.27 µs | **496 µs** | 493 µs | **51.3 ms** | 50.8 ms |
| `JsonNode` | 1.8 µs | 604 µs | 599 µs | 68.9 ms | 69.7 ms |
| RebolParse, full read | 4.5 µs | 2,361 µs | 2,260 µs | 411 ms | 421 ms |
| RebolParse IL, full read | 2.8 µs | 1,366 µs | 1,368 µs | 339 ms | 340 ms |
| RebolParse, skip to property | 1.9 µs | 1,963 µs | 3.4 µs | 208 ms | 3.3 µs |
| RebolParse IL, skip to property | **0.86 µs** | 616 µs | **1.3 µs** | 63 ms | **1.3 µs** |

Bytes allocated per call. Reading the first 3 records allocates about the same as start/mid/end.

| | 400 B | 150 KB | 11 MB |
|---|---:|---:|---:|
| `JsonDocument` (string or bytes) | 336 B | 312-328 B | 312-336 B |
| `JsonNode` | 2.9 KB | 560 KB | 55.2 MB |
| RebolParse, full read (interpreted or IL) | 3.3 KB | 1.77 MB | 179 MB |
| RebolParse, skip to property (interpreted or IL) | 1.5 KB | 1.4 KB | 1.4-1.5 KB |

- **If you need the whole document, `System.Text.Json` is 2-8x faster.** It is a hand-tuned UTF-8 state machine, and `JsonDocument` does not even build objects: it records offsets into the buffer. RebolParse's grammar-driven reader, compiled, runs at 30-80 MB/s on this document.
- **Skipping to a property is competitive.** Compiled, it is faster than `JsonDocument` on the small document (0.86 against 1.27 µs) and within 1.2x of it on the large ones (616 against 496 µs; 63 against 51 ms), even though it has to step over every value before the property it wants.
- **A parse can stop early; `JsonDocument` cannot.** It validates and indexes the whole document before you can ask for anything: 500 µs for 150 KB and 51 ms for 11 MB, even to read the first record. The skip rule stops as soon as it has the last property: 1.3 µs in both, about 400 and 39,000 times faster. This is the one case where RebolParse wins outright, and it needs a partial parse.
- **Building objects is a minor cost.** The full read allocates 179 MB for the 11 MB document and the skip rules 1.5 KB, but the skip rule is only about 2-3x faster, so most of the time is matching, not allocation.
- **For ordinary JSON, use `System.Text.Json`.** The sample shows that a grammar is a few lines and gives precise errors, not that it competes with a dedicated reader.

## Run it

```bash
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*"
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*StripDigits*" --job short
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*JsonLookup*" --job short
```

Drop `--job short` for a precise run; the full job takes several times longer. The 11 MB JSON cases take a few minutes.
