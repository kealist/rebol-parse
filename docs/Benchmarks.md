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

## Object rules: the rules sample vs expression libraries

The [`rules` sample](../samples/Samples/RuleDemo.cs) turns text like `Person.Age > 18 && Person.Job == "SE"` into a `Func<Person, bool>`. Here it is against a lambda written in C#, [DynamicExpresso](https://github.com/dynamicexpresso/DynamicExpresso) 2.19.6, [System.Linq.Dynamic.Core](https://github.com/zzzprojects/System.Linq.Dynamic.Core) 1.7.4 (compiled by .NET, and by [FastExpressionCompiler](https://github.com/dadhi/FastExpressionCompiler) 6.0.0) and Microsoft's [RulesEngine](https://github.com/microsoft/RulesEngine) 6.0.1. Every library reads the same rule text. **Simple** is the rule above. **Nested** is `Person.Age >= 18 && (Person.Job == "PM" || Person.Address.City == "London") && Person.Salary > 50000`.

**Checking one object** with a rule that is already compiled. Nothing allocates unless a figure is given.

| | Simple | Nested |
|---|---:|---:|
| Lambda written in C# | 0.3 ns | 3.1 ns |
| System.Linq.Dynamic.Core | 0.4 ns | 3.1 ns |
| DynamicExpresso, typed delegate | 0.4 ns | 3.5 ns |
| System.Linq.Dynamic.Core + FastExpressionCompiler | 0.6 ns | 3.9 ns |
| RebolParse rules sample | 0.9 ns | 3.1 ns |
| RebolParse rules sample + FastExpressionCompiler | 0.6 ns | 3.5 ns |
| DynamicExpresso, `Lambda.Invoke` | 308 ns, 784 B | 312 ns, 784 B |
| RulesEngine | 474 ns, 1.6 KB | 476 ns, 1.6 KB |

**Compiling a rule** from text, the cost paid once per rule.

| | Simple | Nested | Allocated (Simple, Nested) |
|---|---:|---:|---:|
| RebolParse rules sample, tree only (not compiled) | 1.7 µs | 3.3 µs | 2 KB, 5 KB |
| RebolParse rules sample + FastExpressionCompiler | 6.2 µs | 13 µs | 7 KB, 16 KB |
| System.Linq.Dynamic.Core + FastExpressionCompiler | 15 µs | 70 µs | 25 KB, 137 KB |
| RebolParse rules sample | 66 µs | 221 µs | 7 KB, 12 KB |
| System.Linq.Dynamic.Core | 73 µs | 269 µs | 24 KB, 131 KB |
| DynamicExpresso | 95 µs | 265 µs | 55 KB, 90 KB |
| RulesEngine (build an engine and run it once) | 2,504 µs | 2,753 µs | 217 KB, 333 KB |

- **Once compiled, they all cost the same as C#.** Every one of them ends in a JIT-compiled delegate, so a check is a few nanoseconds. Figures under 1 ns are at the limit of what can be measured, and the differences between the first six rows are not meaningful. The sample's extra half nanosecond is the wrapper that refuses a null object.
- **Call the typed delegate.** `Lambda.Invoke` boxes its arguments and calls dynamically, which is about 100 times slower on the nested rule. RulesEngine builds a result tree for every check, which costs about the same.
- **Compiling is 10,000 times dearer than checking, so cache the delegate.** Most of the compile time is .NET turning the expression tree into IL, which is why three different parsers land close together. The sample reads and type-checks a rule in 2 to 3 µs; the other 64 to 217 µs is `Expression.Compile`.
- **FastExpressionCompiler removes most of that.** `RuleDemo.ToExpression` returns the tree, and `CompileFast()` on it takes 6 to 13 µs in all, 10 to 17 times faster than the sample's own `Compile` and 2 to 5 times faster than Dynamic.Core with the same compiler. The benchmark fails if FastExpressionCompiler falls back to .NET's compiler, and it did not.
- **Use a library unless you need the narrow surface.** The sample is competitive but does less: no method calls, no arithmetic. That is the point of it when rule authors are not trusted, and a limit otherwise.

### Larger rules and varied objects

The two rules above are small and were checked against one person who passes. These tables use generated rules of 1 to 64 comparisons over ints, strings, decimals, doubles, a bool and nested properties, joined as an OR of four-clause AND chains, and check them against 1,000 varied people, so some pass and some fail on the first clause. The engines must agree about every person before anything is timed.

**Checking**, time per person. Nothing allocates.

| Comparisons in the rule | 1 | 4 | 16 | 64 |
|---|---:|---:|---:|---:|
| System.Linq.Dynamic.Core | 1.9 ns | 3.9 ns | 6.4 ns | 13.6 ns |
| DynamicExpresso | 1.9 ns | 3.9 ns | 6.4 ns | 13.7 ns |
| System.Linq.Dynamic.Core + FastExpressionCompiler | 2.2 ns | 4.4 ns | 7.0 ns | 16.3 ns |
| RebolParse rules sample | 1.6 ns | 3.9 ns | 6.6 ns | 14.1 ns |
| RebolParse rules sample + FastExpressionCompiler | 2.2 ns | 4.4 ns | 8.1 ns | 20.4 ns |

**Compiling** one rule from text.

| Comparisons in the rule | 1 | 4 | 16 | 64 |
|---|---:|---:|---:|---:|
| RebolParse rules sample, tree only (not compiled) | 1.0 µs | 3.2 µs | 14 µs | 55 µs |
| RebolParse rules sample + FastExpressionCompiler | 3.4 µs | 12 µs | 46 µs | 167 µs |
| System.Linq.Dynamic.Core + FastExpressionCompiler | 8.8 µs | 70 µs | 183 µs | 674 µs |
| RebolParse rules sample | 25 µs | 230 µs | 806 µs | 3,829 µs |
| System.Linq.Dynamic.Core | 30 µs | 269 µs | 729 µs | 3,143 µs |
| DynamicExpresso | 49 µs | 274 µs | 843 µs | 3,704 µs |

- **The compile-time result holds at every size.** With FastExpressionCompiler the sample is 7 to 23 times faster than with .NET's compiler, and 2.6 to 5.7 times faster than Dynamic.Core with the same compiler. With .NET's compiler all three cost the same, 3 to 4 ms for a 64-comparison rule.
- **Per check they tie at every size too**: 14.1 ns for the sample against 13.5 for the libraries at 64 comparisons. An earlier version of the sample was 1.4 times slower there, because it wrapped a null check around every step of a path. It now collects the checks into one guard, `(a = x.A) != null && (b = a.B) != null ? b.C : null`, which runs about twice as fast.
- **FastExpressionCompiler's code is 10 to 45% slower to run** than .NET's on these rules, for both parsers. It trades a little speed per check for a much faster compile, which pays when rules are compiled often or there are thousands of them, and loses when a few rules check millions of objects.
- **The sample allocates far less while compiling**: 127 KB for the 64-comparison rule against 1.1 to 1.6 MB for the libraries.

### Deeper objects and JSON metadata

An `Order` with a customer, an employer and its parent company, a list of lines, a dictionary of tags, an earlier order, and a `JsonDocument` of loose metadata. Each rule is written the way each library wants it and run against four orders: **full** (every rule true), **other** (every rule false), **sparse** (nulls and empty lists everywhere, no metadata) and **odd** (metadata holding the wrong type under every name). "ok" means all four answers matched a null-safe C# lambda. Print this table, with every rule text, by running `-- scenarios`.

| Rule | RebolParse rules sample | DynamicExpresso | Dynamic.Core, default | Dynamic.Core, types registered |
|---|---|---|---|---|
| Deep path `Order.Customer.Employer.Parent.Address.City` | ok | throws on sparse | throws on sparse | throws on sparse |
| The same, guarded (`?.` or `np(...)`) | ok, no guard needed | ok | ok | ok |
| Enum by name | ok | ok, as `Tier.Gold` | ok | ok |
| List item `Order.Lines[0]...` | ok | throws on sparse | throws on sparse | throws on sparse |
| List count, then two items | ok | ok | ok | ok |
| Nullable number | ok | throws on sparse | throws on sparse | throws on sparse |
| Dictionary key `Order.Tags["channel"]` | ok | throws on sparse | throws on sparse | throws on sparse |
| Dates on two objects | ok | throws on sparse | throws on sparse | throws on sparse |
| Any item matches (`Lines.Any(...)`) | cannot say it | ok | ok | ok |
| Method call (`Name.StartsWith("A")`) | cannot say it | ok | ok | ok |
| Arithmetic (`Qty * Price > 1000`) | cannot say it | throws on sparse | throws on sparse | throws on sparse |
| JSON number, string, true/false, array item, key with a dash | ok | throws on sparse and odd | does not compile | throws on sparse and odd |
| JSON and properties together | ok | throws on sparse and odd | does not compile | throws on sparse and odd |
| JSON through a helper you write and register | ok, no helper needed | ok | does not compile | ok |

- **The libraries follow C#, so a missing value throws.** A null in a path is a `NullReferenceException`, a list position past the end an `ArgumentOutOfRangeException`, a missing key a `KeyNotFoundException`. Each has a guard (`?.` in DynamicExpresso, `np(...)` in Dynamic.Core), but the rule author has to remember it. The sample treats all of these as null.
- **Neither library has JSON paths.** A rule reaches into a `JsonDocument` by calling its methods: `Order.Metadata.RootElement.GetProperty("customer").GetProperty("score").GetDouble() > 80` against the sample's `Order.Metadata.customer.score > 80`. That throws when the metadata is null, when a name is missing, and when a value is the wrong type. The practical fix is a helper such as `MetaJson.Num(Order.Metadata, "customer.score")` that you write and register; with one, both libraries get every order right.
- **Dynamic.Core refuses method calls on types it was not told about**, so out of the box no JSON rule compiles. Registering `JsonDocument` and `JsonElement` with a custom type provider fixes that. This is its protection against rule text calling arbitrary code, and DynamicExpresso does not have it.
- **The sample cannot say three things the libraries can**: a condition over every item of a list, a method call, and arithmetic.

**Checking one order**, for the rules every engine can run, averaged over the full and the other order.

| | Guarded deep path | List count, two items | JSON number | JSON nested string | JSON and properties | JSON through a helper |
|---|---:|---:|---:|---:|---:|---:|
| Lambda written in C# | 1.0 ns | 1.4 ns | 83 ns, 32 B | 114 ns, 160 B | 110 ns, 112 B | 163 ns, 144 B |
| DynamicExpresso | 1.8 ns | 1.6 ns | 54 ns | 74 ns, 40 B | 72 ns | 164 ns, 144 B |
| System.Linq.Dynamic.Core | 1.8 ns | 1.6 ns | 54 ns | 74 ns, 40 B | 72 ns | 169 ns, 144 B |
| RebolParse rules sample | 1.7 ns | 2.9 ns | 66 ns | 56 ns | 70 ns | 102 ns |
| RebolParse rules sample + FastExpressionCompiler | 2.1 ns | 4.5 ns | 66 ns | 56 ns | 71 ns | 102 ns |

The C# lambda uses the `MetaJson` helper for JSON, which splits its path on every call; that is why it is slower than the libraries' direct method calls. In the last column the libraries call that helper and the sample uses its own paths.

- **Reading JSON costs 50 to 100 ns whoever does it**, 20 to 50 times a property read, because `JsonDocument` finds a property by scanning the object. If a rule runs millions of times against metadata, copy the fields it needs into real properties first.
- **On JSON the sample is level or ahead, and allocates nothing.** It is 1.3 times faster on strings (56 against 74 ns), because it compares the JSON text in place where `GetString()` makes a string, level on the mixed rule, and 1.6 times faster than the libraries calling a helper. It is 1.2 times slower on a lone number (66 against 54 ns), where it reads a `double` and the libraries' rule asks for an `int`. It looks names up by their UTF-8 bytes, encoded once when the rule is compiled.
- **On plain objects null safety now costs little.** The guarded deep path ties the libraries. List items are 2.9 ns against 1.5: the sample checks the list is there and long enough, and compares `int?` values, where the libraries index it and would throw.

## Run it

```bash
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*"
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*StripDigits*" --job short
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*JsonLookup*" --job short
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*ObjectRule*" --job short
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- scenarios     # the deep-object and JSON table, with every rule text
```

Drop `--job short` for a precise run; the full job takes several times longer. The 11 MB JSON cases take a few minutes.
