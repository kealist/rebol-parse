# RebolParse

[REBOL's `parse` dialect](http://www.rebol.com/docs/core23/rebolcore-15.html) for C#: a compact grammar language for matching, extracting and rewriting text and structured data. It sits between regular expressions and a full parser generator. Rules read left to right like the input they describe, they can recurse, and they run actions as they match.

## Why use it

Regular expressions are fine until a pattern has to nest, capture several things, or be understood by someone else next month. The usual next step is a parser generator, which means a grammar file, a build step and a generated class. Parse fills the gap between them. A rule is a string, or a few lines of C#, that lives next to the code that uses it. It reads in the order the input is written, and it can call itself.

What you get:

- **Rules you can read.** `copy key some alpha "=" copy value some digit` says what it matches, in order. The regex equivalent has groups you count by index.
- **Recursion.** Balanced brackets, nested lists and arithmetic are a rule that refers to itself. .NET regex can match balanced text only with balancing groups, which are hard to write and harder to read.
- **Actions in the middle of a match.** `(@name)` calls a C# delegate as soon as that part matches. The calculator sample evaluates `2*(3+4)` while it parses, with no tree built first.
- **Errors you can locate.** You can parse a file line by line, report line 14, and still load the other lines. The INI sample does this.
- **One rule set for text and data.** The same rules match characters in a string or items in a list, including nested lists.
- **Safe to load from outside.** Rule text can come from a config file or a user. It cannot run code. It can only call delegates you registered.
- **Speed that is easy to check.** Interpreted, it is faster than the interpreted .NET `Regex` on most of the benchmark tasks and slower on one (bulk deletion). With `.Compiled()`, validating a phone number takes about 10 ns against 14 ns for a compiled `Regex`, and a long list of numbers about 5 times faster. The [benchmarks](docs/Benchmarks.md) show the losses too.
- **Tested against itself.** The library has 100% line and branch coverage, and random rules are run through the string engine, the list engine and the IL compiler, which must all agree.

When another tool is the better choice:

- For a simple pattern, a regex is shorter and everyone already knows it.
- To read whole JSON or XML documents quickly, use `System.Text.Json` or `System.Xml`. A parse grammar reads full JSON 3 to 5 times slower than `System.Text.Json`. It wins only when you can stop early, for example to read the first few properties of a large document.
- Matching lists is interpreted, not compiled, so it is slower than matching text.
- Captures, actions and edits made in a branch that later fails are not undone, as in REBOL. [The reference](docs/Reference.md#what-is-undone-when-a-branch-fails) explains how to write rules around that.
- This is REBOL's dialect, not a complete copy. `then`, `return`, `limit` and the `only` refinements are not implemented.

```csharp
using RebolParse;

var rule = Dialect.Read("copy key some alpha \"=\" copy value some digit");
var result = Parser.Parse("width=640", rule);

result.Matched;        // true
result.Vars["key"];    // "width"
result.Vars["value"];  // "640"
```

- **Two ways to write rules, one engine.** Rule text like `some digit "." some digit`, or fluent C# like `Some(Digit) + "." + Some(Digit)`.
- **Text and structured data.** The same rules match characters in a string or items in a list, including nested lists.
- **Capture, collect, rewrite.** `copy`, `collect`/`keep`, and `insert`/`change`/`remove` pull data out of input or edit it as it is matched.
- **Allocation-free validation.** `Parser.IsMatch` allocates nothing. See [Performance](#performance).

## Install

```
dotnet add package RebolParse
```

Requires .NET 10.

## A tour

**Validate.** `IsMatch` answers yes or no, allocation-free.

```csharp
using RebolParse;
using static RebolParse.P;

// build once, e.g. as a static field, and reuse
static readonly Rule Phone = Dialect.Read("3 digit \"-\" 4 digit");

Parser.IsMatch("555-1234", Phone);   // true
Parser.IsMatch("5551234", Phone);    // false
```

**Extract.** `collect` and `keep` gather what you want from anywhere in the input.

```csharp
var words = Dialect.Read("collect words any [keep some alpha | skip]");
Parser.Parse("hello, big world!", words).Vars["words"];   // ["hello", "big", "world"]
```

**Rewrite.** `change`, `insert` and `remove` edit as they match; the result is in `Output`, and your input is never touched.

```csharp
var dashes = Dialect.Read("any [change \"-\" \"+\" | skip]");
Parser.Parse("a-b-c", dashes).Output;   // "a+b+c"
```

**Recurse.** Named rules can refer to themselves.

```csharp
var rules = Dialect.ReadRules(new Dictionary<string, string>
{
    ["group"] = "\"(\" any group \")\"",
});
Parser.IsMatch("(()(()))", Ref("group"), rules);   // true
Parser.IsMatch("(()",      Ref("group"), rules);   // false
```

**Run code.** Name an action in the rule with `(@name)` and bind it to a C# delegate.

```csharp
var total = 0;
var sum = Dialect.Read("any [copy n some digit (@add) opt \",\"]",
    new Dictionary<string, Action<Ctx>> { ["add"] = c => total += int.Parse((string)c.Vars["n"]!) });
Parser.Parse("1,2,30", sum);   // total == 33
```

**Write grammars as grammars.** `ReadGrammar` takes `name = rule` definitions, so a recursive grammar is one block of text. `charset` spells out a set of characters the way a regex class does.

```csharp
var rules = Dialect.ReadGrammar("""
    expr   = term any [charset "+-" term]
    term   = factor any [charset "*/" factor]
    factor = some digit | "(" expr ")"
    """);
Ref("expr").IsMatch("2*(3+(4-1))", rules);   // true
```

**Match structured data.** Give `Parse` a list instead of a string and rules match items, by value or by type; `into` descends into nested lists.

```csharp
var command = Dialect.Read("[quote add integer! integer!] | [quote say string!]");
Parser.IsMatch(new List<object> { new Word("add"), 1, 2 }, command);   // true
```

## Fluent sugar

With `using static RebolParse.P;`, rules can be built with operators and read left to right:

```csharp
var phone = Digit * 3 + "-" + Digit * 4;                  // 3 digit "-" 4 digit
var ident = Chars("a-zA-Z_") + Chars("a-zA-Z0-9_").Any(); // charset "a-zA-Z_" any charset "a-zA-Z0-9_"
var pair  = Alpha.Some().Copy("key") + "=" + (+Digit).Copy("value");

phone.IsMatch("555-1234");                // rules have IsMatch and Parse of their own
pair.Parse("width=640").Vars["value"];    // "640"
```

| Sugar | Same as |
|---|---|
| `+r` | `Some(r)` |
| `!r` | `Not(r)` |
| `r * 3`, `3 * r` | `Rep(3, r)` |
| `r.Some()` `.Any()` `.Opt()` `.Times(n)` `.Times(min, max)` | `Some(r)` `Any(r)` `Opt(r)` `Rep(n, r)` `Rep(min, max, r)` |
| `r.Not()` `.Ahead()` `.To()` `.Thru()` `.Into()` | `Not(r)` `Ahead(r)` `To(r)` `Thru(r)` `Into(r)` |
| `r.Copy("v")` `.Set("v")` `.Keep()` | `Copy("v", r)` `Set("v", r)` `Keep(r)` |
| `r.ChangeTo(v)` `.Remove()` | `Change(r, v)` `Remove(r)` |
| `r.Then(action)` | `r + Do(action)` |
| `Chars("a-z_")` | `CharIf` over a regex-style class; `charset "a-z_"` in text |
| `rule.IsMatch(input)` `rule.Parse(input)` | `Parser.IsMatch(input, rule)` `Parser.Parse(input, rule)` |

## Compile to IL

A rule you run many times can be compiled to IL, like `RegexOptions.Compiled`. It matches exactly what the interpreter matches, only faster:

```csharp
static readonly Rule Phone = Dialect.Read("3 digit \"-\" 4 digit").Compiled();
Parser.IsMatch("555-1234", Phone);      // 10 ns, against 30 ns interpreted and 14 ns for a compiled Regex

var rules = Dialect.ReadGrammar(grammar).Compiled();   // compile every rule in a table
```

Compile once and reuse; the first compile in a process takes about 30 ms. Every keyword compiles for text, including `collect`/`keep` and the edits; block input uses the interpreter, so `.Compiled()` is always safe. See [Compiling to IL](docs/Reference.md#compiling-to-il) for the details.

## Semantics at a glance

- A parse succeeds only if the rule matches **and consumes all the input**. Pass `partial: true` to allow leftovers.
- `a | b` is **ordered choice** with backtracking: the first alternative that matches wins.
- Repeats (`some`, `any`) are **greedy** and never give back.
- Matching is **case-sensitive**; pass `ignoreCase: true` for literals to ignore case.
- Paren code is **never evaluated**. Only `(@name)`, bound to a delegate you supply, is allowed, so rules can safely come from untrusted text.

## Documentation

| | |
|---|---|
| [Tutorial](docs/Tutorial.md) | Sixteen lessons with exercises and solutions, from your first rule to an INI-file parser and compiling to IL. Start here if you are new to parse. |
| [Reference](docs/Reference.md) | Every keyword and the text syntax, grammars, what a parse returns, errors, what backtracking undoes, compiling to IL, and limits. |
| [Benchmarks](docs/Benchmarks.md) | Comparison with .NET `Regex` and `System.Text.Json`, the interpreter against the IL back end, with numbers and caveats. |
| [Architecture](docs/Architecture.md) | For contributors: the three engines, the node compiler, the gap buffer, choice dispatch, the IL back end, the invariants, the tests, and how to add a keyword. |
| [Samples](samples/README.md) | Eight runnable programs that check their own results: a calculator, a JSON reader, INI, CSV, log analysis, a source cleaner, a block-mode robot language, and a template engine. |

The public API is fully documented with XML comments, so IntelliSense shows what each keyword does.

## Keyword map

REBOL keywords, and the matching fluent builder from `using static RebolParse.P;`. See the [reference](docs/Reference.md#keywords) for exactly what each one matches and consumes.

| REBOL | Fluent | Notes |
|---|---|---|
| `"abc"` `#"a"` | `"abc"` `'a'` | implicit conversion to `Rule` |
| `a b` | `a + b` | sequence; one side must already be a `Rule` (or use `Seq(a, b)`) |
| `a \| b` | `a \| b` | ordered choice (or `Choice(a, b)`) |
| `some` `any` `opt` | `Some` `Any` `Opt` | `while r` is accepted as another spelling of `any r` |
| `3 r` `2 4 r` | `Rep(3, r)` `Rep(2, 4, r)` | |
| `skip` `end` `none` `fail` | `Skip` `End` `None` `Fail` | |
| `ahead` `not` | `Ahead` `Not` | lookahead, consume nothing; `and r` is another spelling of `ahead r` |
| `to` `thru` | `To` `Thru` | scan forward to a match |
| `copy v r` `set v r` | `Copy("v", r)` `Set("v", r)` | results in `ParseResult.Vars` |
| `into r` | `Into(r)` | descends into a nested list |
| `quote word` | `Lit(new Word("word"))` | |
| `name:` `:name` | `Mark("name")` `Seek("name")` | save / restore position |
| `break` `reject` | `Break` `Reject` | leave the enclosing loop, succeeding / failing it |
| `insert v` | `Insert(v)` | insert at the position and move past it |
| `change r v` | `Change(r, v)` | replace what `r` matched |
| `remove r` | `Remove(r)` | delete what `r` matched |
| `collect v r` | `Collect("v", r)` | list of everything `r` keeps, in `Vars` |
| `keep r` `keep pick r` | `Keep(r)` `KeepPick(r)` | add the match / only its first item |
| `keep (@fn)` | `Keep(ctx => value)` | add a computed value; `null` adds nothing |
| `if (@fn)` | `If(ctx => bool)` | succeed, consuming nothing, when the condition holds |
| `(@name)` | `Do(ctx => ...)` | run a C# action |
| `digit alpha alnum upper lower space newline tab` | `Digit` ... | character classes (text) |
| `charset "a-zA-Z_"` | `Chars("a-zA-Z_")` | a custom class; `-` between two characters is a range |
| `integer! decimal! string! word! block!` | `Integer` ... | datatypes (lists) |
| bare word | `Ref("name")` | a named rule from the rule table passed to `Parse` |

## Differences from REBOL

- **No evaluated code.** `(@name)` calls a bound delegate; there is no interpreter behind the parens. `keep (@fn)` and `if (@fn)` take named value functions, passed as the `values` argument of `Dialect.Read`.
- **Case-sensitive by default.** REBOL's parse is case-insensitive unless told otherwise; here you opt in with `ignoreCase: true`.
- **Edits work on a copy.** `insert`, `change` and `remove` never modify your input; the edited result is `ParseResult.Output`.
- **Rules are values.** A rule is an immutable object you build once and reuse; there is no global state.
- **Not every keyword.** `then`, `return`, `limit` and the `only` refinements are not implemented. `while` is an alias of `any` and has its no-progress guard, so a rule that matches nothing cannot loop forever.
- Backtracking is plain recursive descent with no memoization, so pathological grammars can be slow. Captures, actions and edits made in a branch that later fails are not undone, as in REBOL.

## Performance

Rules compile to specialized matcher nodes the first time they are used, so build a rule once and reuse it (a `static readonly` field is ideal), as you would a `Regex`. Text is matched as a `ReadOnlySpan<char>` with vectorized scanning for literals. `Parser.IsMatch` allocates nothing for rules that do not capture or edit; `Parser.Parse` returns captures and edits.

On flat text RebolParse runs at or ahead of .NET's interpreted `Regex` on most tasks and ahead of `RegexOptions.Compiled` on extraction, while `RegexOptions.Compiled` still wins plain validation and recursion against the interpreter. `rule.Compiled()` generates IL and closes that gap: it beats `RegexOptions.Compiled` on validation and shallow recursion. Edits (`remove`, `change`, `insert`) are linear when they sweep through the text, within about 1.4x of `Regex.Replace`. [Benchmarks](docs/Benchmarks.md) has the numbers, the method, and how to reproduce them.

## Build

```bash
dotnet test                                                   # 500+ tests, 100% line and branch coverage of the library
dotnet run --project samples/Samples                          # eight runnable examples (add a name to run one)
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*"
```

## License

[MIT](LICENSE)
