# Benchmarks: RebolParse vs Regex and System.Text.Json

BenchmarkDotNet, `--job short` (3 iterations, so treat the numbers as rough and expect run-to-run noise of 10-30%; the regex rows in particular moved by that much between runs). Apple M4 Pro, .NET 10.0.6, Release build. Each benchmark checks in setup that every engine produces the same answer before timing.

Rules are built once and reused, as `Regex` objects are. Every cell is the mean time, then the bytes allocated per call. **IL** is the same rule after `.Compiled()` (see [Compiling to IL](Reference.md#compiling-to-il)); it is `IsMatch` for the validation rows and `Parse` for the rows that capture or edit.

## Against .NET Regex

| Task | Size | Parse (interpreter) | IsMatch (interpreter) | **IL** | Regex (interpreted) | Regex (compiled) |
|---|---|---|---|---|---|---|
| Validate `555-1234` | 8 chars | 39 ns, 128 B | 29 ns, 0 B | **11 ns**, 0 B | 47 ns, 0 B | 14 ns, 0 B |
| Validate comma-separated ints | 100 numbers | 2.1 µs, 128 B | 2.2 µs, 0 B | **0.34 µs**, 0 B | 3.1 µs, 0 B | 1.9 µs, 0 B |
| | 10,000 numbers | 212 µs, 128 B | 216 µs, 0 B | **34 µs**, 0 B | 299 µs, 0 B | 199 µs, 0 B |
| Balanced parens, depth 10 | ~420 chars | 6.2 µs, 230 B | 5.6 µs, 0 B | **2.1 µs**, 0 B | 16.8 µs, 0 B | 4.0 µs, 0 B |
| Balanced parens, depth 1000 | ~40 KB | 937 µs, 240 B | 944 µs, 0 B | 523 µs, 0 B | 1,807 µs, 0 B | **426 µs**, 0 B |
| Capture `key=value` lines | 10 lines | 0.69 µs, 1.9 KB | n/a (captures) | **0.61 µs**, 1.9 KB | 1.3 µs, 3.2 KB | 0.89 µs, 3.2 KB |
| | 1,000 lines | 51 µs, 126 KB | n/a (captures) | **48 µs**, 126 KB | 150 µs, 305 KB | 86 µs, 305 KB |
| Extract words | 1 KB | 10.8 µs, 10.3 KB | n/a (collects) | **5.7 µs**, 10.3 KB | 15.2 µs, 40.7 KB | 15.9 µs, 40.7 KB |
| | 100 KB | 1,189 µs, 1.05 MB | n/a (collects) | **709 µs**, 1.05 MB | 4,782 µs, 4.07 MB | 4,250 µs, 4.07 MB |
| Strip digits (`remove` vs `Replace`) | 1 KB | 11.2 µs, 1.5 KB | n/a (edits) | **5.4 µs**, 1.6 KB | 6.5 µs, 1.4 KB | 8.7 µs, 1.4 KB |
| | 100 KB | 862 µs, 109 KB | n/a (edits) | **423 µs**, 109 KB | 543 µs, 109 KB | 640 µs, 109 KB |

Building the matcher (not including the interpreter's compile to nodes, which happens on first use and is cached per rule instance):

| | Time | Allocated |
|---|---|---|
| Read `3 digit "-" 4 digit` from text | 372 ns | 1.0 KB |
| Build it with the fluent API | 112 ns | 400 B |
| `new Regex(...)` interpreted | 705 ns | 1.9 KB |
| `new Regex(..., RegexOptions.Compiled)` | 4,131 ns | 8.7 KB |
| `.Compiled()` and run once (rule already warm) | 258 µs | 27.5 KB |

### What the numbers say

- **The interpreter** is faster than interpreted regex on all but one of these tasks (validation 1.2-2x, key=value 2-3x, extraction 1.4-4x, recursion 1.9-2.5x), and slower on bulk deletion (strip digits, 1.6-1.7x).
- **IL** is the fastest column on every row but deep recursion. Against `RegexOptions.Compiled` it is 1.2x faster on the phone number, 5.6-5.8x on the long number list, 1.9x on shallow recursion, 1.5-1.8x on `key=value`, 2.8-6x on word extraction and 1.5-1.6x on stripping digits. It is 1.2x slower on depth-1000 recursion, where every level is a call between generated methods.
- **Allocations.** `IsMatch` allocates nothing, interpreted or IL. Captures allocate what they capture, 2-4x less than `Regex.Matches`. Edits now allocate the same as `Regex.Replace` (109 KB for 100 KB of text): the edit buffer is rented from `ArrayPool` and sized to the input, so the result string is the only thing left behind.
- **How it gets there.** Rules compile once into specialized nodes: a loop over a character class is one tight loop, `to`/`thru` on a literal use the vectorized `IndexOf`, `some [not X skip]` is one scan, a choice skips alternatives that cannot start with the next character, and text is matched as a `ReadOnlySpan<char>`. Edits go through a gap buffer, so a left-to-right sweep moves each character at most once. IL removes the per-node calls on top of that, compares short literals inline, calls other rules through a cached delegate, and loads its constants once per call.
- **IL costs time to build.** The first compile in a process takes about 30 ms, mostly loading and warming the expression compiler. After that a small rule takes under a millisecond and the 7-rule JSON grammar about 8 ms. A rule you run once is better left interpreted.
- **Set-up cost still favours RebolParse** over a compiled regex: reading rule text costs a tenth of constructing one.
- **Noise.** These are `--job short` runs. Regex cells moved by up to 2x between runs (compiled regex on 1 KB of stripping measured 4.4 and 8.7 µs; the figure shown is the more common one), so differences under about 30% are not meaningful.

Pick RebolParse for grammars that are awkward or impossible as regex (recursion, structured block input, rules that capture and edit as they go) and for readability. For a hot loop over flat text, compile the rule: it is as fast as `RegexOptions.Compiled` or faster on everything above except deep recursion.

## Reading properties out of JSON: RebolParse vs System.Text.Json

Three properties per call (for example `author.name`, `tags[1]`, `version`) from a 400-byte document and from arrays of 2,000 records (150 KB) and 200,000 records (11 MB). The RebolParse side uses the grammar from the [JSON sample](../samples/Samples/JsonDemo.cs) two ways: read the whole document into objects and navigate to the paths, or use a rule that skips straight to each property and copies only its text. Each is shown interpreted and compiled to IL. "Start/mid/end" reads the first, middle and last record; "first 3" reads only properties in the first three records, where a parse can stop early.

| | 400 B | 150 KB, start/mid/end | 150 KB, first 3 | 11 MB, start/mid/end | 11 MB, first 3 |
|---|---|---|---|---|---|
| `JsonDocument` (string) | 1.37 µs, 336 B | 523 µs, 328 B | 500 µs, 312 B | 52.9 ms, 336 B | 53.6 ms, 312 B |
| `JsonDocument` (UTF-8 bytes) | 1.27 µs, 336 B | 500 µs, 328 B | 494 µs, 312 B | 51.5 ms, 3.0 KB | 51.3 ms, 312 B |
| `JsonNode` | 1.8 µs, 2.9 KB | 595 µs, 560 KB | 593 µs, 560 KB | 71.6 ms, 55.2 MB | 71.0 ms, 55.2 MB |
| RebolParse, full read | 4.4 µs, 3.3 KB | 2,300 µs, 1.77 MB | 2,315 µs, 1.77 MB | 418 ms, 179 MB | 426 ms, 179 MB |
| RebolParse IL, full read | 3.1 µs, 3.3 KB | 1,657 µs, 1.77 MB | 1,656 µs, 1.77 MB | 351 ms, 179 MB | 318 ms, 179 MB |
| RebolParse, skip to property | 1.9 µs, 1.5 KB | 1,880 µs, 1.4 KB | 3.8 µs, 1.4 KB | 187 ms, 1.5 KB | 3.4 µs, 1.4 KB |
| RebolParse IL, skip to property | **1.1 µs**, 1.5 KB | **981 µs**, 1.4 KB | **1.7 µs**, 1.4 KB | **101 ms**, 1.5 KB | **2.2 µs**, 1.4 KB |

- **If you need the whole document, `System.Text.Json` is 2.5-8x faster.** It is a hand-tuned UTF-8 state machine, and `JsonDocument` does not even build objects: it records offsets into the buffer. RebolParse's grammar-driven reader, compiled, runs at 30-70 MB/s on this document.
- **Skipping to a property is competitive.** Compiled, it is slightly faster than `JsonDocument` on the small document (1.1 against 1.3 µs) and about 2x its time on the large ones, since it still has to step over every value before the property it wants.
- **A parse can stop early; `JsonDocument` cannot.** It validates and indexes the whole document before you can ask for anything: 500 µs for 150 KB and 52 ms for 11 MB, even to read the first record. The skip rule stops as soon as it has the last property: 1.7 µs and 2.2 µs, about 300 and 23,000 times faster. This is the one case where RebolParse wins outright, and it needs a partial parse.
- **Building objects is a minor cost.** The full read allocates 179 MB for the 11 MB document and the skip rules 1.5 KB, but the skip rule is only about 2-3x faster, so most of the time is matching, not allocation.
- **For ordinary JSON, use `System.Text.Json`.** The sample shows that a grammar is a few lines and gives precise errors, not that it competes with a dedicated reader.

## Run it

```bash
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*"
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*StripDigits*" --job short
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*JsonLookup*" --job short
```

Drop `--job short` for a precise run; the full job takes several times longer. The 11 MB JSON cases take a few minutes.
