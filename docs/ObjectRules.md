# Object rules: the rules sample against expression libraries

The [`rules` sample](../samples/Samples/RuleDemo.cs) turns text such as `Person.Age > 18 && Person.Job == "SE"` into a `Func<Person, bool>`. This page compares it with the libraries usually used for that job: what each can express, what each lets rule text call, and how fast each is.

- [Summary](#summary)
- [What was compared](#what-was-compared)
- [What each engine can express](#what-each-engine-can-express)
- [What rule text is allowed to call](#what-rule-text-is-allowed-to-call)
- [Speed: checking](#speed-checking)
- [Speed: compiling a rule](#speed-compiling-a-rule)
- [Speed: 20,000 orders](#speed-20000-orders)
- [Run it](#run-it)
- [Appendix: the rule texts](#appendix-the-rule-texts)

## Summary

| | RebolParse rules sample | DynamicExpresso | System.Linq.Dynamic.Core |
|---|---|---|---|
| A null, a missing key or a short list in a path | reads as null | throws unless the rule guards it with `?.` | throws unless the rule guards it with `np(...)` |
| JSON metadata | paths: `Order.Metadata.customer.score` | method calls, or a helper you write | the same, after registering the types |
| Mixed number types | compared as the wider type | needs a cast for `decimal` against `double` | needs a cast |
| A condition over every item of a list | no | yes, with lambdas switched on | yes |
| Method calls | none | any public method on any object the rule reaches | methods on built-in and registered types |
| One check, plain properties | 2 to 6 ns | 2 to 4 ns | 2 to 4 ns |
| One check that reads JSON | 55 to 145 ns | 54 to 217 ns | 54 to 219 ns |
| 20,000 orders against six rules | 6.6 to 7.4 ms, nothing allocated | 8.8 to 9.8 ms, 3 to 8 MB | 8.9 to 9.6 ms, 3 to 8 MB |
| Compiling one rule | 25 µs to 4 ms | 49 µs to 4 ms | 30 µs to 3 ms |

Which to use:

- **The sample**, when rule authors are not trusted, when rules read JSON metadata, or when the data has gaps. It cannot call anything, and nothing in a rule can throw.
- **DynamicExpresso or System.Linq.Dynamic.Core**, when rules need method calls or a condition over every item of a list (`Order.Lines.Any(...)`), and the authors are trusted to write guards.
- **Microsoft RulesEngine**, when you want rule sets stored as JSON with a result and a message per rule. It is about 100 times slower per check, so keep it off hot paths.
- **FastExpressionCompiler on top of any of them**, when many rules are compiled or recompiled. It compiles 3 to 23 times faster and its code runs 10 to 45% slower. Keep a fallback: it declines some trees.

## What was compared

| Engine | Version | How it is used here |
|---|---|---|
| RebolParse rules sample | this repository | `RuleDemo.Compile<T>(text)`, or `ToExpression<T>(text)` for the tree |
| [DynamicExpresso](https://github.com/dynamicexpresso/DynamicExpresso) | 2.19.6 | `Interpreter.ParseAsDelegate<Func<T, bool>>(text, "Order")` |
| [System.Linq.Dynamic.Core](https://github.com/zzzprojects/System.Linq.Dynamic.Core) | 1.7.4 | `DynamicExpressionParser.ParseLambda(...)` with a parameter named in the rule, then `Compile()` |
| [FastExpressionCompiler](https://github.com/dadhi/FastExpressionCompiler) | 6.0.0 | `CompileFast()` in place of `Compile()` on the sample's tree or Dynamic.Core's |
| [RulesEngine](https://github.com/microsoft/RulesEngine) | 6.0.1 | One workflow with one rule, `ExecuteAllRulesAsync` |
| Lambda written in C# | | The baseline. For JSON it calls the `MetaJson` helper described below |

**How it was measured.** BenchmarkDotNet with `--job short` on an Apple M4 Pro, .NET 10.0.6, Release build. A short job is three iterations, so differences under about 30% are noise, and on the single-rule rows of the 20,000-order tables under about 1.5 times. Before anything is timed, every engine must give the same answer as the C# lambda for every object.

**How to read a figure.** "74 ns, 40 B" is one check that took 74 nanoseconds and allocated 40 bytes. No byte figure means nothing was allocated.

**The objects.**

- `Person` has an age, a job, a salary and an address. Used for the small rules and the rules of 1 to 64 comparisons, which are checked against 1,000 varied people.
- `Order` has a customer, the customer's employer and its parent company, a list of lines, a dictionary of tags, an earlier order, and a `JsonDocument` of loose metadata such as `{"priority": 5, "source": "web", "customer": {"segment": "enterprise", "score": 87.5}, "tags": ["rush"], "ship-to": {"city": "Oslo"}}`.
- Four orders test behaviour: **full** (every rule true), **other** (every rule false), **sparse** (nulls and empty lists everywhere, no metadata) and **odd** (metadata holding the wrong type under every name).
- 20,000 generated orders, each with its own parsed `JsonDocument`, test throughput. **Clean** has every field filled. **Messy** has nulls, short lists, missing metadata or wrong types in 5 to 25% of the orders.

**The helper.** Neither library can read into JSON without throwing on a missing name, so a team using one writes a small static class and registers it. Here that is `MetaJson`, with `Num`, `Str` and `Flag` taking a document and a dotted path: `MetaJson.Num(Order.Metadata, "customer.score")`. It splits the path on every call.

The text of every rule named in the tables is in the [appendix](#appendix-the-rule-texts).

## What each engine can express

Each rule is written the way each engine wants it and run against the four orders. "ok" means all four answers matched a null-safe C# lambda. Where an engine needs a guard, a cast or the helper to get there, the cell says so. `-- scenarios` prints these tables with every rule text.

Dynamic.Core appears twice: as it comes, and with `JsonDocument`, `JsonElement`, `MetaJson` and the `Tier` enum registered and `ToString()` allowed.

### Plain objects

| Rule | Sample | DynamicExpresso | Dynamic.Core, default | Dynamic.Core, registered |
|---|---|---|---|---|
| Deep path `Order.Customer.Employer.Parent.Address.City` | ok | throws on sparse | throws on sparse | throws on sparse |
| The same, guarded | ok, no guard needed | ok with `?.` | ok with `np(...)` | ok with `np(...)` |
| Enum by name: `Order.Customer.Tier == "Gold"` | ok | ok as `Tier.Gold` | ok | ok |
| List item: `Order.Lines[0]...` | ok | throws on sparse | throws on sparse | throws on sparse |
| List count, then two items | ok | ok | ok | ok |
| Nullable number | ok | throws on sparse | throws on sparse | throws on sparse |
| Dictionary key: `Order.Tags["channel"]` | ok | throws on sparse | throws on sparse | throws on sparse |
| Any item matches: `Order.Lines.Any(...)` | cannot say it | ok | ok | ok |
| Method call: `Name.StartsWith("A")` | cannot say it | ok | ok | ok |

### JSON metadata

| Rule | Sample | DynamicExpresso | Dynamic.Core, default | Dynamic.Core, registered |
|---|---|---|---|---|
| A number, a string, true or false, an array item, a key with a dash | ok | throws on sparse and odd | does not compile | throws on sparse and odd |
| JSON and properties together | ok | throws on sparse and odd | does not compile | throws on sparse and odd |
| The same through the helper | ok, no helper needed | ok | does not compile | ok |

### A property, JSON or a calculation on both sides

The sample has `+ - * /`, unary minus, and `min(...)` and `max(...)` of two or more numbers, on either side of a comparison.

| Rule | Sample | DynamicExpresso | Dynamic.Core, default | Dynamic.Core, registered |
|---|---|---|---|---|
| Two strings at the end of deep paths | ok | ok with `?.` | ok with `np(...)` | ok with `np(...)` |
| An `int` and a `decimal`; a `double?` and an `int` | ok | ok | ok | ok |
| Ints on two objects: `Order.Id != Order.Previous.Id` | ok | ok with `?.` | ok with `np(...)` | ok with `np(...)` |
| Dates on two objects: `Order.Placed > Order.Previous.Placed` | ok | throws on sparse | throws on sparse | throws on sparse |
| Two enums: `Order.Customer.Tier == Order.Previous.Customer.Tier` | ok | **throws on sparse** even as `Order.Previous?.Customer.Tier` | ok with `np(...)` | ok with `np(...)` |
| Two whole objects: `Order.Customer.Address == Order.Customer.Employer.Address` | ok | ok with `?.` | ok with `np(...)` | ok with `np(...)` |
| An enum and a string property: `Order.Customer.Tier == Order.Tags["tier"]` | ok | ok as `Tier.ToString() == ...` | does not compile | ok as `Tier.ToString()` |
| JSON and a string or number property | ok | ok through the helper | does not compile | ok through the helper |
| JSON and an enum: `Order.Metadata.tier == Order.Customer.Tier` | ok | ok, helper and `ToString()` | does not compile | ok, helper and `ToString()` |
| Two JSON numbers: `Order.Metadata.priority < Order.Metadata.customer.score` | ok | ok through the helper | does not compile | ok through the helper |
| Two JSON strings: `Order.Metadata.source == Order.Metadata.backup` | ok, with a different meaning (below) | ok through the helper | does not compile | ok through the helper |
| Arithmetic: `Order.Lines[0].Qty * ...Price.Amount > 1000` | ok | throws on sparse | throws on sparse | throws on sparse |
| Arithmetic on both sides, after a count | ok | ok | ok | ok |
| `max(a, b) > 10 && min(Order.Id, 50) == Order.Id` | ok | ok as `Math.Max`, `Math.Min` | ok as `Math.Max`, `Math.Min` | ok |
| A `decimal` product against JSON arithmetic | ok | ok with `Convert.ToDouble(...)` | does not compile | ok with `Convert.ToDouble(...)` |
| Whole-number division: `Order.Lines[0].Qty / 8 > 1.2`, quantity 12 | true: 12 / 8 is 1.5 | false: 12 / 8 is 1 | false | false |
| Division by zero | false | throws | throws | throws |

### What the tables show

**Missing values**

- The libraries follow C#, so a missing value throws: a null in a path is a `NullReferenceException`, a list position past the end an `ArgumentOutOfRangeException`, a missing key a `KeyNotFoundException`. Each has a guard, but the rule author has to remember it. The sample reads all of these as null.
- `?.` in DynamicExpresso guards one step, not the rest of the chain. `Order.Previous?.Customer.Tier` still throws when `Previous` is null, where C# would stop. Write `?.` at every step.
- A missing value on both sides is equal in every engine: `null == null`. If a rule means "both are there and equal", add `&& Order.Previous != null`.

**JSON**

- Neither library has JSON paths. A rule calls `JsonElement` methods, `Order.Metadata.RootElement.GetProperty("customer").GetProperty("score").GetDouble() > 80`, against the sample's `Order.Metadata.customer.score > 80`. The calls throw when the metadata is null, when a name is missing, and when a value is the wrong type. With the helper both libraries get every order right.
- Dynamic.Core refuses method calls on types it was not told about, so out of the box no JSON rule compiles. Registering the types fixes that. It also refuses `ToString()` unless `AllowEqualsAndToStringMethodsOnObject` is set, and without it there is no way to compare an enum with text that is not a literal.
- Two JSON values are equal in the sample when they are the same JSON: the number `7` is not equal to a missing value. The helper spelling asks "are both the same string", so a number and a missing value both read as null and compare equal. Neither is wrong; they are different questions.

**Numbers**

- Mixed types need no casts in the sample. It compares and calculates as the wider type: `long`, then `decimal`, then `double`. The libraries follow C#, which has no `decimal > double`, so a decimal price against a JSON number needs `Convert.ToDouble(...)` written into the rule.
- Division differs on purpose. In the sample whole numbers divide exactly, and dividing by zero is a missing value, so the comparison is false. In the libraries `12 / 8` is `1`, as in C#, and a zero divisor throws. Decide which your rule authors expect.
- In the sample a missing value in a calculation makes the result missing, and a JSON number compared with a literal is read as a `double`, which is exact for whole numbers up to 2^53. Against a `decimal` property it is read as a `decimal`.

**What the sample cannot say**

- A condition over every item of a list, and a method call. Its calculations stop at `+ - * /`, `min` and `max`.

## What rule text is allowed to call

Rule text from users is code, so what it can reach matters as much as how fast it runs. Each line below was compiled, not run (`-- safety` prints this). "Locked down" is DynamicExpresso built with `InterpreterOptions.PrimitiveTypes | SystemKeywords` and `EnableAssignment(AssignmentOperators.None)`.

| Rule text | Sample | DynamicExpresso, default | DynamicExpresso, locked down | Dynamic.Core, default or registered |
|---|---|---|---|---|
| Comparisons, `&&`, `\|\|`, `!`, brackets | yes | yes | yes | yes |
| Arithmetic: `Order.Id + 1 > 2` | yes | yes | yes | yes |
| Method on a value: `Order.Customer.Name.ToUpper() == "ADA"` | no | yes | yes | yes |
| Method that changes the object: `Order.Tags.Remove("channel")` | no | **yes** | **yes** | no (see below) |
| Assignment: `(Order.Customer = null) == null` | no | **yes** | no | reads `=` as "equals" |
| `Order.GetType().Name` | no | **yes** | **yes** | no |
| Reflection: `Order.GetType().Assembly.GetType(...)` | no | no | no | no |
| Static calls: `System.IO.File.Exists("x")`, `File.Exists("x")`, `System.Environment.MachineName` | no | no | no | no |
| `new System.Text.StringBuilder()`, `typeof(System.IO.File)` | no | no | no | no |
| A lambda: `Order.Lines.Any(l => l.Qty > 10)` | no | no, unless switched on | no | yes |

- **Neither library lets rule text name a type it was not given.** Static calls, `new` and `typeof` on arbitrary types are refused, and reflection past `GetType()` is blocked. Rule text cannot reach the file system or start a process out of the box.
- **Both allow method calls; the difference is which.** DynamicExpresso allows any public method on any object the rule can reach, including ones that change it (`Order.Tags.Remove(...)`) and any method on your own classes (`Order.Cancel()`), and by default it allows assignment. Dynamic.Core allows methods only on its built-in types (strings, numbers, dates, LINQ over collections) plus types you register.
- **Dynamic.Core refused `Tags.Remove` here by accident, not by policy.** The error is an ambiguous overload. Do not read that row as a guarantee about mutating methods; treat any type you register as fully callable.
- **Every type you register widens the surface.** Registering `JsonDocument` and `JsonElement` to make JSON rules work makes all their methods callable. A helper class with three static methods is a narrower thing to expose.
- **Neither limits work.** `Order.Customer.Name.PadLeft(2000000000)` is an ordinary string method that allocates 4 GB. If authors are not trusted, also bound rule length and run with a timeout.
- **The sample allows only what its grammar has.** Comparisons, `&&`, `||`, `!`, brackets, `+ - * /`, `min(...)`, `max(...)`, and reads of public properties, fields, list positions, dictionary keys and JSON. `min` and `max` are words in the grammar, not calls into .NET, so there is no method a rule can name and nothing to lock down. A property getter does run when it is read.

## Speed: checking

Time for one check with a rule that is already compiled.

### Small rules on a `Person`

**Simple** is `Person.Age > 18 && Person.Job == "SE"`. **Nested** adds brackets, a nested property and a decimal.

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

- **Once compiled, they all cost the same as C#.** Each ends in a JIT-compiled delegate. Figures under 1 ns are at the limit of what can be measured, and the differences between the first six rows are not meaningful. The sample's extra half nanosecond on the simple rule is the wrapper that refuses a null object.
- **Call the typed delegate.** `Lambda.Invoke` boxes its arguments and calls dynamically, which is about 100 times slower. RulesEngine builds a result tree for every check, which costs about the same.

### By rule size

Generated rules of 1 to 64 comparisons, checked against 1,000 varied people, so some pass and some fail on the first clause. Time per person. Nothing allocates.

| Comparisons in the rule | 1 | 4 | 16 | 64 |
|---|---:|---:|---:|---:|
| System.Linq.Dynamic.Core | 1.9 ns | 3.9 ns | 6.4 ns | 13.6 ns |
| DynamicExpresso | 1.9 ns | 3.9 ns | 6.4 ns | 13.7 ns |
| RebolParse rules sample | 1.6 ns | 3.9 ns | 6.6 ns | 14.1 ns |
| System.Linq.Dynamic.Core + FastExpressionCompiler | 2.2 ns | 4.4 ns | 7.0 ns | 16.3 ns |
| RebolParse rules sample + FastExpressionCompiler | 2.2 ns | 4.4 ns | 8.1 ns | 20.4 ns |

- **They tie at every size.** The sample collects a path's null checks into one guard, `(a = x.A) != null && (b = a.B) != null ? b.C : null`, which runs about twice as fast as a check wrapped around every step.
- **FastExpressionCompiler's code is 10 to 45% slower to run** than .NET's, for both parsers.

### By kind of rule, on an `Order`

One row per rule, averaged over the full and the other order. The [appendix](#appendix-the-rule-texts) has each rule's text.

| Rule | C# lambda | DynamicExpresso | Dynamic.Core | Sample | Sample + FastExpressionCompiler |
|---|---:|---:|---:|---:|---:|
| **Plain objects** | | | | | |
| Guarded deep path | 1.0 ns | 1.8 ns | 1.8 ns | 1.7 ns | 2.1 ns |
| List count, two items | 1.4 ns | 1.6 ns | 1.6 ns | 2.9 ns | 4.5 ns |
| Two strings, deep paths | 1.5 ns | 2.2 ns | 2.8 ns | 2.0 ns | 2.2 ns |
| An `int` and a `decimal` | 4.1 ns | 4.1 ns | 4.1 ns | 5.7 ns | 6.8 ns |
| An enum and a string property | 16 ns, 24 B | 20 ns, 24 B | 19 ns, 24 B | 22 ns, 24 B | 22 ns, 24 B |
| `min` and `max` | 1.6 ns | 1.7 ns | 1.8 ns | 3.7 ns | 5.1 ns |
| Arithmetic on both sides | 23 ns | 26 ns | 26 ns | 27 ns | declined |
| **JSON metadata** | | | | | |
| JSON number | 83 ns, 32 B | 54 ns | 54 ns | 66 ns | 66 ns |
| JSON nested string | 114 ns, 160 B | 74 ns, 40 B | 74 ns, 40 B | 56 ns | 56 ns |
| JSON and properties | 110 ns, 112 B | 72 ns | 72 ns | 70 ns | 71 ns |
| JSON through a helper | 163 ns, 144 B | 164 ns, 144 B | 169 ns, 144 B | 102 ns | 102 ns |
| JSON and a string property | 88 ns, 144 B | 88 ns, 144 B | 89 ns, 144 B | 55 ns, 32 B | 55 ns, 32 B |
| JSON and a number property | 86 ns, 32 B | 86 ns, 32 B | 85 ns, 32 B | 72 ns | 72 ns |
| Two JSON numbers | 222 ns, 144 B | 217 ns, 144 B | 219 ns, 144 B | 145 ns | 144 ns |
| Arithmetic with JSON | 90 ns, 32 B | 94 ns, 32 B | 95 ns, 32 B | 76 ns | declined |

In the first three JSON rows the libraries call `JsonElement` methods directly, which is only safe on clean data. In the rest they call the helper, as does the C# lambda throughout.

- **On plain properties it is a tie**, within a nanosecond or two. The sample is slower where a list item is involved (2.9 against 1.6 ns, 3.7 against 1.7 ns on `min` and `max`): it checks the list is there and long enough and carries an `int?`, where the libraries index the list and would throw.
- **Reading JSON costs 50 to 100 ns a lookup whoever does it**, 20 to 50 times a property read, because `JsonDocument` finds a property by scanning the object. If a rule runs millions of times against metadata, copy the fields it needs into real properties first.
- **With JSON the sample is level or ahead, and allocates little or nothing.** It is 1.3 times faster on strings, because it compares the JSON text in place where `GetString()` makes a string, and 1.2 to 1.6 times faster than the libraries calling a helper. It is 1.2 times slower on a lone number (66 against 54 ns), where it reads a `double` and the libraries' rule asks for an `int`. It looks names up by their UTF-8 bytes, encoded once when the rule is compiled.
- **Decimal arithmetic costs the same everywhere**: 26 to 27 ns for five operations.
- **An enum against a string property allocates 24 bytes in every engine**: the enum has to be turned into its name.
- **FastExpressionCompiler declines rules that calculate with `decimal?` values.** It returns nothing so the caller can fall back to .NET's compiler.

## Speed: compiling a rule

Turning rule text into a delegate, the cost paid once per rule. "Tree only" is the sample reading the text and checking it against the type, without generating code.

| | Simple | Nested | Allocated (Simple, Nested) |
|---|---:|---:|---:|
| RebolParse rules sample, tree only | 1.7 µs | 3.3 µs | 2 KB, 5 KB |
| RebolParse rules sample + FastExpressionCompiler | 6.2 µs | 13 µs | 7 KB, 16 KB |
| System.Linq.Dynamic.Core + FastExpressionCompiler | 15 µs | 70 µs | 25 KB, 137 KB |
| RebolParse rules sample | 66 µs | 221 µs | 7 KB, 12 KB |
| System.Linq.Dynamic.Core | 73 µs | 269 µs | 24 KB, 131 KB |
| DynamicExpresso | 95 µs | 265 µs | 55 KB, 90 KB |
| RulesEngine (build an engine and run it once) | 2,504 µs | 2,753 µs | 217 KB, 333 KB |

| Comparisons in the rule | 1 | 4 | 16 | 64 |
|---|---:|---:|---:|---:|
| RebolParse rules sample, tree only | 1.0 µs | 3.2 µs | 14 µs | 55 µs |
| RebolParse rules sample + FastExpressionCompiler | 3.4 µs | 12 µs | 46 µs | 167 µs |
| System.Linq.Dynamic.Core + FastExpressionCompiler | 8.8 µs | 70 µs | 183 µs | 674 µs |
| RebolParse rules sample | 25 µs | 230 µs | 806 µs | 3,829 µs |
| System.Linq.Dynamic.Core | 30 µs | 269 µs | 729 µs | 3,143 µs |
| DynamicExpresso | 49 µs | 274 µs | 843 µs | 3,704 µs |

- **Compiling is about 10,000 times dearer than checking, so cache the delegate.**
- **Most of it is .NET turning the tree into IL**, which is why three different parsers land close together: 3 to 4 ms for a 64-comparison rule. The sample reads and type-checks that rule in 55 µs; the rest is `Expression.Compile`.
- **FastExpressionCompiler removes most of that.** On the sample's tree it is 7 to 23 times faster than .NET's compiler, and 2.6 to 5.7 times faster than Dynamic.Core with the same compiler. It pays when rules are compiled often or there are thousands of them, and loses when a few rules check millions of objects, since its code runs slower.
- **The sample allocates far less while compiling**: 127 KB for the 64-comparison rule against 1.1 to 1.6 MB for the libraries.

## Speed: 20,000 orders

One pass over 20,000 different orders. The time is for the whole pass, on one core. These are the figures to plan capacity with.

The first six rules are the ones timed singly above. **Eligibility**, **Fraud** and **Routing** are rules of the size a business would write, seven to nine conditions each: Eligibility uses plain objects only, Fraud is nearly all JSON, Routing mixes both with brackets inside brackets.

On messy data the libraries must guard every path and read JSON through the helper, since direct calls would throw. For the three larger rules they use that safe spelling on both data sets. The sample runs the same text everywhere.

### Clean data

| Rule | Sample | DynamicExpresso | Dynamic.Core | C# lambda |
|---|---:|---:|---:|---:|
| Guarded deep path | 0.14 ms | 0.17 ms | 0.16 ms | 0.12 ms |
| List count, two items | 0.38 ms | 0.28 ms | 0.28 ms | 0.14 ms |
| JSON number | 2.3 ms | 2.4 ms | 2.2 ms | 2.9 ms |
| JSON nested string | 2.6 ms | 3.6 ms | 3.5 ms | 4.3 ms |
| JSON and properties | 3.3 ms | 3.4 ms | 3.2 ms | 3.8 ms |
| JSON through a helper | 4.3 ms | 5.1 ms | 5.2 ms | 5.1 ms |
| **All six** | **7.4 ms** | **8.8 ms**, 3.3 MB | **8.9 ms**, 3.3 MB | **11.6 ms**, 8.6 MB |
| Eligibility | 0.45 ms | 0.55 ms | 0.49 ms | 0.37 ms |
| Fraud | 5.2 ms, 0.1 MB | 7.0 ms, 3.8 MB | 7.0 ms, 3.8 MB | 7.6 ms, 3.8 MB |
| Routing | 3.0 ms | 3.3 ms, 1.2 MB | 3.4 ms, 1.2 MB | 3.8 ms, 1.2 MB |
| **All three** | **7.7 ms**, 0.1 MB | **9.4 ms**, 5.0 MB | **9.5 ms**, 5.0 MB | **10.0 ms**, 5.0 MB |

### Messy data

| Rule | Sample | DynamicExpresso | Dynamic.Core | C# lambda |
|---|---:|---:|---:|---:|
| Guarded deep path | 0.11 ms | 0.13 ms | 0.13 ms | 0.10 ms |
| List count, two items | 0.21 ms | 0.12 ms | 0.13 ms | 0.12 ms |
| JSON number | 2.2 ms | 2.4 ms | 2.3 ms | 2.5 ms |
| JSON nested string | 2.3 ms | 3.6 ms | 3.5 ms | 3.5 ms |
| JSON and properties | 2.7 ms | 3.4 ms | 3.3 ms | 3.3 ms |
| JSON through a helper | 2.7 ms | 3.5 ms | 3.5 ms | 3.6 ms |
| **All six** | **6.6 ms** | **9.8 ms**, 7.6 MB | **9.6 ms**, 7.6 MB | **9.7 ms**, 7.6 MB |
| Eligibility | 0.29 ms | 0.31 ms | 0.28 ms | 0.26 ms |
| Fraud | 4.3 ms, 0.1 MB | 5.8 ms, 3.3 MB | 5.6 ms, 3.3 MB | 6.1 ms, 3.3 MB |
| Routing | 2.1 ms | 2.2 ms, 0.8 MB | 2.2 ms, 0.8 MB | 2.6 ms, 0.8 MB |
| **All three** | **6.0 ms**, 0.1 MB | **7.6 ms**, 4.1 MB | **7.5 ms**, 4.1 MB | **7.8 ms**, 4.1 MB |

### What the tables show

- **20,000 orders against a set of rules takes 6 to 10 ms**, whichever engine runs them. That is roughly 2 to 3 million orders a second.
- **JSON is nearly all of it.** A rule on plain properties covers 20,000 orders in 0.1 to 0.5 ms (5 to 25 ns each). A rule that reads metadata takes 2 to 7 ms (110 to 350 ns each).
- **A larger rule does not cost in proportion to its size**, because `&&` and `||` stop at the first answer. Nine conditions on plain objects take about as long as one, since most orders fail early.
- **Cost follows how often JSON is reached.** Fraud opens with two JSON reads on every order and takes 4 to 7 ms. Routing checks an enum first, so most orders never touch the metadata, and takes 2 to 3 ms. Put the cheap, selective conditions first and JSON last.
- **Real data is 1.5 to 4 times slower per check than the single-object tables.** There one or two orders stayed in the processor's cache; here each order and its JSON are somewhere else in memory.
- **The sample is fastest on every set and allocates almost nothing**: 1.2 to 1.5 times faster, with 0 to 0.1 MB a pass against 3 to 8 MB. The libraries' allocations come from `GetString()` and from the helper splitting its path. The sample's 0.1 MB comes from the one place it has to make a string, comparing a JSON value with a property. It trails only on the list rule.
- **Messy data is faster than clean**, because a missing value ends a rule sooner.
- **Compiling six rules is a one-off of about 1 ms** with .NET's compiler, so it stops mattering after the first few hundred orders.

## Run it

```bash
# behaviour: no timing, a few seconds each
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- scenarios   # what each engine does with each rule, with every rule text
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- safety      # what each engine lets rule text call

# timing
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*ObjectRuleCheck*" "*ObjectRuleCompile*" --job short     # small rules
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*ObjectRuleScale*" --job short                           # 1 to 64 comparisons
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*ObjectRuleShapes*" --job short                          # deep paths, lists, JSON
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*ObjectRuleBothSides*" "*ObjectRuleArithmetic*" --job short
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*ObjectRuleStress*" --job short                          # 20,000 orders
```

Drop `--job short` for a precise run; the full job takes several times longer. The code is in [`ObjectRules.cs`](../benchmarks/RebolParse.Benchmarks/ObjectRules.cs), [`ObjectRuleStress.cs`](../benchmarks/RebolParse.Benchmarks/ObjectRuleStress.cs) and [`Scenarios.cs`](../benchmarks/RebolParse.Benchmarks/Scenarios.cs). The comparison libraries are referenced by the benchmark project only, not by the RebolParse package.

## Appendix: the rule texts

The sample's text comes first. Where a library needs a different spelling, it follows.

### On a `Person`

```text
Simple    Person.Age > 18 && Person.Job == "SE"
Nested    Person.Age >= 18 && (Person.Job == "PM" || Person.Address.City == "London") && Person.Salary > 50000
```

The rules of 1 to 64 comparisons repeat eight clauses with changing numbers, joined as an OR of four-clause AND chains: `Person.Age > 18 && Person.Job == "SE" && Person.Salary >= 31000 && Person.Address.City != "Leeds" || Person.Address.Zip < 20400 && !Person.Remote && ...`

### Plain objects, on an `Order`

```text
Guarded deep path
  sample           Order.Customer.Employer.Parent.Address.City == "Oslo"
  DynamicExpresso  Order.Customer.Employer?.Parent?.Address?.City == "Oslo"
  Dynamic.Core     np(Order.Customer.Employer.Parent.Address.City) == "Oslo"

List count, two items
  all              Order.Lines.Count >= 2 && Order.Lines[0].Qty > Order.Lines[1].Qty

Two strings, deep paths
  sample           Order.Customer.Address.City == Order.Customer.Employer.Parent.Address.City
  DynamicExpresso  Order.Customer.Address?.City == Order.Customer.Employer?.Parent?.Address?.City
  Dynamic.Core     np(Order.Customer.Address.City) == np(Order.Customer.Employer.Parent.Address.City)

An int and a decimal
  all              Order.Lines.Count > 0 && Order.Lines[0].Qty < Order.Lines[0].Product.Price.Amount

An enum and a string property
  sample           Order.Customer.Tier == Order.Tags["tier"]
  libraries        Order.Tags.ContainsKey("tier") && Order.Customer.Tier.ToString() == Order.Tags["tier"]

min and max
  sample           max(Order.Lines[0].Qty, Order.Lines[1].Qty) > 10 && min(Order.Id, 50) == Order.Id
  libraries        Order.Lines.Count >= 2 && Math.Max(Order.Lines[0].Qty, Order.Lines[1].Qty) > 10 && Math.Min(Order.Id, 50) == Order.Id

Arithmetic on both sides
  all              Order.Lines.Count >= 2 && Order.Lines[0].Qty * Order.Lines[0].Product.Price.Amount
                     > (Order.Lines[1].Product.Price.Amount + 10) * Order.Lines[1].Qty / 2
```

### JSON metadata, on an `Order`

```text
JSON number
  sample           Order.Metadata.priority >= 3
  libraries        Order.Metadata.RootElement.GetProperty("priority").GetInt32() >= 3

JSON nested string
  sample           Order.Metadata.customer.segment == "enterprise"
  libraries        Order.Metadata.RootElement.GetProperty("customer").GetProperty("segment").GetString() == "enterprise"

JSON and properties
  sample           Order.Metadata.customer.score > 80 && Order.Customer.Tier == "Gold" && Order.Lines.Count > 1
  DynamicExpresso  Order.Metadata.RootElement.GetProperty("customer").GetProperty("score").GetDouble() > 80
                     && Order.Customer.Tier == Tier.Gold && Order.Lines.Count > 1
  Dynamic.Core     the same, with Order.Customer.Tier == "Gold"

JSON through a helper
  sample           Order.Metadata.customer.score > 80 && Order.Metadata.source == "web"
  libraries        MetaJson.Num(Order.Metadata, "customer.score") > 80 && MetaJson.Str(Order.Metadata, "source") == "web"

JSON and a string property
  sample           Order.Metadata["ship-to"].city == Order.Customer.Employer.Parent.Address.City
  DynamicExpresso  MetaJson.Str(Order.Metadata, "ship-to.city") == Order.Customer.Employer?.Parent?.Address?.City
  Dynamic.Core     MetaJson.Str(Order.Metadata, "ship-to.city") == np(Order.Customer.Employer.Parent.Address.City)

JSON and a number property
  sample           Order.Metadata.priority > Order.Lines.Count
  libraries        MetaJson.Num(Order.Metadata, "priority") > Order.Lines.Count

Two JSON numbers
  sample           Order.Metadata.priority < Order.Metadata.customer.score
  libraries        MetaJson.Num(Order.Metadata, "priority") < MetaJson.Num(Order.Metadata, "customer.score")

Arithmetic with JSON
  sample           Order.Lines[0].Qty * Order.Lines[0].Product.Price.Amount > Order.Metadata.priority * 100 + min(Order.Id, 50)
  libraries        Order.Lines.Count > 0 && Convert.ToDouble(Order.Lines[0].Qty * Order.Lines[0].Product.Price.Amount)
                     > MetaJson.Num(Order.Metadata, "priority") * 100 + Math.Min(Order.Id, 50)
```

On messy data the first three are also written with the helper in the libraries: `MetaJson.Num(Order.Metadata, "priority") >= 3`, and so on.

### The three larger rules

The sample's text. The libraries add `?.` or `np(...)` on paths, a count before a list item, `ContainsKey` before a key, and the helper for JSON.

```text
Eligibility
  Order.Customer.Tier != "Bronze"
  && (Order.Customer.Address.City == "Oslo" || Order.Customer.Employer.Address.City == "Oslo"
      || Order.Customer.Employer.Parent.Address.City == "Oslo")
  && Order.Lines.Count >= 2 && Order.Lines[0].Product.Price.Amount > 50 && !(Order.Lines[1].Qty < 3)
  && Order.Tags["channel"] != "phone" && Order.Previous != null

Fraud
  (Order.Metadata.priority >= 7 || Order.Metadata.customer.score < 20)
  && Order.Metadata.source != "store" && !Order.Metadata.gift
  && (Order.Metadata["ship-to"].city != Order.Customer.Address.City || Order.Metadata.customer.segment == "consumer")
  && Order.Metadata.tags[0] == "rush"

Routing
  (Order.Customer.Tier == "Gold" && Order.Metadata.priority >= 5 || Order.Customer.Tier == "Silver" && Order.Metadata.priority >= 8)
  && (Order.Lines.Count > 2 || Order.Lines[0].Product.WeightKg > 2.5)
  && !(Order.Metadata.source == "phone" && Order.Tags["channel"] == "phone")
  && Order.Customer.Employer.Parent != null && Order.Metadata.customer.score >= 40
```
