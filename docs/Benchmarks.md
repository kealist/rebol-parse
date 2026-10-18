# Benchmarks: RebolParse vs Regex

BenchmarkDotNet, `--job short` (3 iterations, so treat the numbers as rough and expect run-to-run noise of 10-30%). Figures are from the run after the compiled-node engine landed. Apple M4 Pro, .NET 10.0.6, Release build. Each benchmark checks in setup that both engines produce the same answer before timing.

Parse rules are built once and reused, as `Regex` objects are. Times are the mean of a short run, followed by bytes allocated per call.

| Task | Size | Parse | IsMatch | Regex (interpreted) | Regex (compiled) |
|---|---|---|---|---|---|
| Validate `555-1234` | 8 chars | 37 ns, 120 B | 31 ns, **0 B** | 47 ns, 0 B | 14 ns, 0 B |
| Validate comma-separated ints | 100 numbers | 2.1 µs, 120 B | 2.1 µs, **0 B** | 3.1 µs | 1.9 µs |
| | 10,000 numbers | 212 µs, 120 B | 216 µs, **0 B** | 299 µs | 199 µs |
| Balanced parens, depth 10 | ~420 chars | 5.8 µs, 230 B | 5.9 µs, **0 B** | 16.5 µs | 4.0 µs |
| Balanced parens, depth 1000 | ~40 KB | 938 µs, 240 B | 946 µs, **0 B** | 1.62 ms | 385 µs |
| Capture `key=value` lines | 10 lines | 0.68 µs, 1.9 KB | n/a (captures) | 1.35 µs, 3.2 KB | 0.89 µs, 3.2 KB |
| | 1,000 lines | 51 µs, 126 KB | n/a (captures) | 152 µs, 305 KB | 88 µs, 305 KB |
| Extract words | 1 KB | 9.6 µs, 10 KB | n/a (collects) | 15.2 µs, 41 KB | 9.9 µs, 41 KB |
| | 100 KB | 1.06 ms, 1.05 MB | n/a (collects) | 4.54 ms, 4.07 MB | 4.15 ms, 4.07 MB |
| Strip digits (`remove` vs `Replace`) | 1 KB | 9.7 µs, 5.4 KB | n/a (edits) | 6.5 µs, 1.4 KB | 4.4 µs, 1.4 KB |
| | 100 KB | 771 µs, 499 KB | n/a (edits) | 540 µs, 109 KB | 680 µs, 109 KB |
| Build the matcher | `3 digit "-" 4 digit` | 318 ns (text), 103 ns (fluent) | | 701 ns | 4,138 ns |

Building the matcher does not include compiling the rule tree to nodes, which happens on first use and is cached per rule instance.

## What the numbers say

- **Against interpreted regex, RebolParse is now faster or close on everything.** Validation runs at 1.3-1.5x the speed of the interpreted regex, word extraction at 1.6-4.3x, and the recursive grammar at 1.4-3x. Capturing key=value lines is now faster than regex too (2-3x, since the `to` scans and `not X skip` loops compile to vectorized searches).
- **Against `RegexOptions.Compiled`, which emits IL, it wins on extraction (1-4x) and loses on plain validation (2.7x slower on the phone number, about even on the number list) and on recursion (1.4x slower at depth 10, 3x at depth 1000).** That gap is the cost of a tree of virtual calls against straight-line generated code; closing it would mean generating IL too.
- **How it got here.** Rules compile once into specialized nodes: a loop over a character class is one tight loop, `to`/`thru` on a literal uses the vectorized `IndexOf`, literal strings use `StartsWith`, and nodes that cannot break, reject, keep or edit skip the backtracking bookkeeping. Text is matched as a `ReadOnlySpan<char>`, so there is no copy of the input and no per-character interface call. Before this step RebolParse was 3-8x slower than regex on these tasks.
- **`Parser.IsMatch` allocates nothing.** `Parse` allocates about 120 B plus whatever it captures; it never copies input it does not edit.
- **`remove` is within 1.1-1.4x of `Regex.Replace`.** Edits go through a gap buffer: a removal widens a gap instead of shifting the rest of the text, so a left-to-right sweep moves each character at most once. Before this it was 43x slower on 100 KB (23 ms), because every removal shifted the tail and the loop was quadratic.
- **Setup cost favours RebolParse.** Reading rule text costs under half of constructing an interpreted regex and a thirteenth of a compiled one.

Pick RebolParse for grammars that are awkward or impossible as regex (recursion, structured block input, rules that capture and edit as they go) and for readability. For a hot loop that only validates flat text, `RegexOptions.Compiled` is still the fastest option.

## Reading properties out of JSON: RebolParse vs System.Text.Json

Three properties per call (for example `author.name`, `tags[1]`, `version`), on a 400-byte document and on a 150 KB array of 2,000 records. The RebolParse side uses the grammar from the [JSON sample](../samples/Samples/JsonDemo.cs) in two ways: read the whole document into objects and navigate to the paths, or use a rule that skips straight to each property and copies only its text. Each benchmark checks in setup that every method returns the same three values.

| Document | `JsonDocument` (string) | `JsonDocument` (UTF-8) | `JsonNode` | RebolParse, full read | RebolParse, skip to property |
|---|---|---|---|---|---|
| 400 B | 1.39 µs, 336 B | 1.28 µs, 336 B | 1.86 µs, 2.9 KB | 4.5 µs, 3.3 KB | 2.0 µs, 1.5 KB |
| 150 KB, properties at start, middle, end | 509 µs, 328 B | 496 µs, 328 B | 641 µs, 574 KB | 2,440 µs, 1.9 MB | 1,933 µs, 1.5 KB |
| 150 KB, properties in the first 3 records | 507 µs, 312 B | 502 µs, 312 B | 612 µs, 574 KB | 2,431 µs, 1.9 MB | **3.7 µs**, 1.5 KB |

- **If you need the whole document, `System.Text.Json` is 3-5x faster.** It is a hand-tuned UTF-8 state machine, and `JsonDocument` does not even build objects: it records offsets into the buffer. RebolParse's grammar-driven reader runs at about 45-60 MB/s on this document, against several hundred MB/s on flat text, because a recursive grammar pays a few virtual calls for every value.
- **Skipping to a property is closer.** On the small document it is 1.4x `JsonDocument`; on the large one, 3.8x, since it still has to step over every value before the property it wants.
- **A parse can stop early; `JsonDocument` cannot.** It validates and indexes the whole document before you can ask for anything. The skip rule stops as soon as it has the last property, so reading from the first few records takes 3.7 µs against 507 µs, 137x faster. This is the one case where RebolParse wins, and it needs a partial parse.
- **Building objects is a minor cost.** The full read allocates 1.9 MB and the skip rules 1.5 KB, but the time is only 25% different. Most of it is matching.
- **For ordinary JSON, use `System.Text.Json`.** The sample shows that a grammar is a few lines and gives precise errors, not that it competes with a dedicated reader.

**What tuning did.** The first version of this comparison took 4.0 ms (large, skip) and 3.9 ms (large, full read). Two engine changes brought it to 1.9 and 2.4 ms. A loop of the form `some [not X skip]` with one-character `X` now compiles to a single scan (`IndexOfAny` when `X` is plain characters). A choice now knows which characters each alternative can start with and skips the ones that cannot match the next character, which removes three or four failed attempts per JSON value. Writing the string rule as `any [some [not ["^\"" | "\"] skip] | "\" skip]` instead of one character at a time lets it use the scan.

## Run it

```bash
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*"
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*StripDigits*" --job short
```

Drop `--job short` for a precise run; the full job takes several times longer.
