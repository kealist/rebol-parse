# RebolParse

[![NuGet](https://img.shields.io/nuget/v/RebolParse.svg)](https://www.nuget.org/packages/RebolParse)
[![Downloads](https://img.shields.io/nuget/dt/RebolParse.svg)](https://www.nuget.org/packages/RebolParse)
[![build](https://github.com/kealist/rebol-parse/actions/workflows/build.yml/badge.svg)](https://github.com/kealist/rebol-parse/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/github/license/kealist/rebol-parse.svg)](LICENSE)
![.NET](https://img.shields.io/badge/.NET-net10.0%20%7C%20netstandard2.0-512BD4)

[REBOL's `parse` dialect](http://www.rebol.com/docs/core23/rebolcore-15.html) for C#. It is a small grammar language for matching, extracting and rewriting text and structured data, more capable than a regex and lighter than a parser generator.

```csharp
using RebolParse;

var rule = Dialect.Read("copy key some alpha \"=\" copy value some digit");
var result = Parser.Parse("width=640", rule);

result.Matched;        // true
result.Vars["key"];    // "width"
result.Vars["value"];  // "640"
```

## Why use it

A regex gets hard to work with once a pattern nests or captures several things. A parser generator handles that, but brings a grammar file, a build step and generated code. With Parse, a rule is a string or a few lines of C# kept next to the code that uses it.

- **Readable rules.** `copy key some alpha "=" copy value some digit` reads in the same order as the input. No numbered groups.
- **Recursion.** A rule can refer to itself, so balanced brackets, nested lists and arithmetic are easy to express.
- **Actions during a match.** `(@name)` calls a C# delegate when that part matches. The calculator sample evaluates `2*(3+4)` as it parses, without building a tree.
- **Capture and rewrite.** `copy`, `collect`/`keep`, and `insert`/`change`/`remove` extract data or edit it as it is matched.
- **Text or data.** The same rules match characters in a string or items in a list, including nested lists.
- **Rule text or fluent C#.** Write `some digit "." some digit` or `Some(Digit) + "." + Some(Digit)`. Both run on the same engine.
- **Safe with untrusted rules.** Rule text cannot run code. It can only call delegates you registered.
- **Fast.** Interpreted, it beats interpreted .NET `Regex` on most benchmarks. Compiled with `.Compiled()`, it matches or beats `RegexOptions.Compiled` on all of them. `Parser.IsMatch` allocates nothing. See [Benchmarks](docs/Benchmarks.md).
- **Well tested.** 100% line and branch coverage. Random rules run through the string engine, the list engine and the IL compiler, and all three must agree.

When to use something else:

- For a simple pattern, a regex is shorter and more familiar.
- To read whole JSON or XML documents, use `System.Text.Json` or `System.Xml`. They are 2 to 8 times faster. Parse only wins when you can stop early, such as reading the first few properties of a large document.
- Matching lists is interpreted, not compiled, so it is slower than matching text.
- See [Differences from REBOL](#differences-from-rebol) for what is not implemented.

## Install

```
dotnet add package RebolParse
```

Targets `net10.0` and `netstandard2.0`, so it also runs on .NET Framework 4.6.1 and later, .NET Core 2.0 and later, Mono and Unity. The `net10.0` build is the fastest. The `netstandard2.0` build gives the same results and depends only on `System.Memory`.

## A tour

**Validate.** `IsMatch` returns true or false and allocates nothing.

```csharp
using RebolParse;
using static RebolParse.P;

// build once, e.g. as a static field, and reuse
static readonly Rule Phone = Dialect.Read("3 digit \"-\" 4 digit");

Parser.IsMatch("555-1234", Phone);   // true
Parser.IsMatch("5551234", Phone);    // false
```

**Extract.** `collect` and `keep` gather matches from anywhere in the input.

```csharp
var words = Dialect.Read("collect words any [keep some alpha | skip]");
Parser.Parse("hello, big world!", words).Vars["words"];   // ["hello", "big", "world"]
```

**Rewrite.** `change`, `insert` and `remove` edit as they match. The result is in `Output`. Your input is not modified.

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

**Run code.** Name an action with `(@name)` and bind it to a C# delegate.

```csharp
var total = 0;
var sum = Dialect.Read("any [copy n some digit (@add) opt \",\"]",
    new Dictionary<string, Action<Ctx>> { ["add"] = c => total += int.Parse((string)c.Vars["n"]!) });
Parser.Parse("1,2,30", sum);   // total == 33
```

**Write a grammar.** `ReadGrammar` takes `name = rule` lines, so a recursive grammar fits in one block of text. `charset` defines a set of characters, like a regex character class.

```csharp
var rules = Dialect.ReadGrammar("""
    expr   = term any [charset "+-" term]
    term   = factor any [charset "*/" factor]
    factor = some digit | "(" expr ")"
    """);
Ref("expr").IsMatch("2*(3+(4-1))", rules);   // true
```

**Match structured data.** Pass a list instead of a string and rules match items by value or by type. `into` descends into nested lists.

```csharp
var command = Dialect.Read("[quote add integer! integer!] | [quote say string!]");
Parser.IsMatch(new List<object> { new Word("add"), 1, 2 }, command);   // true
```

## Fluent sugar

With `using static RebolParse.P;`, you can build rules with operators:

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

Like `RegexOptions.Compiled`, a rule can be compiled to IL. It matches exactly what the interpreter matches, only faster:

```csharp
static readonly Rule Phone = Dialect.Read("3 digit \"-\" 4 digit").Compiled();
Parser.IsMatch("555-1234", Phone);      // 11 ns, against 32 ns interpreted and 14 ns for a compiled Regex

var rules = Dialect.ReadGrammar(grammar).Compiled();   // compile every rule in a table
```

Compile once and reuse the result. The first compile in a process takes about 30 ms. Every keyword compiles for text input. List input falls back to the interpreter, so `.Compiled()` is always safe to call. See [Compiling to IL](docs/Reference.md#compiling-to-il).

## Semantics at a glance

- A parse succeeds only if the rule matches **and consumes all the input**. Pass `partial: true` to allow leftovers.
- `a | b` is **ordered choice** with backtracking: the first alternative that matches wins.
- Repeats (`some`, `any`) are **greedy** and never give back.
- Matching is **case-sensitive**; pass `ignoreCase: true` for literals to ignore case.
- Paren code is **never evaluated**. Only `(@name)`, bound to a delegate you supply, is allowed.

## Documentation

| | |
|---|---|
| [Tutorial](docs/Tutorial.md) | Sixteen lessons with exercises, from a first rule to an INI parser and compiling to IL. Start here if you are new to parse. |
| [Reference](docs/Reference.md) | Every keyword, the text syntax, grammars, results, errors, backtracking, compiling to IL, and limits. |
| [Benchmarks](docs/Benchmarks.md) | Numbers against .NET `Regex` and `System.Text.Json`, interpreted and compiled. |
| [Architecture](docs/Architecture.md) | For contributors: how the engines, compiler and tests work, and how to add a keyword. |
| [Samples](samples/README.md) | Eight runnable programs: a calculator, a JSON reader, INI, CSV, log analysis, a source cleaner, a block-mode robot language, and a template engine. |

The public API has XML doc comments, so IntelliSense shows what each keyword does.

## Keyword map

REBOL keywords and their fluent equivalents from `using static RebolParse.P;`. The [reference](docs/Reference.md#keywords) has full details.

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

- **No evaluated code.** `(@name)` calls a bound delegate. Nothing else in parens runs. `keep (@fn)` and `if (@fn)` take named value functions, passed as the `values` argument of `Dialect.Read`.
- **Case-sensitive by default.** REBOL is case-insensitive by default. Here you opt in with `ignoreCase: true`.
- **Edits work on a copy.** `insert`, `change` and `remove` never modify your input. The result is in `ParseResult.Output`.
- **Rules are values.** A rule is an immutable object you build once and reuse. There is no global state.
- **Not every keyword.** `then`, `return`, `limit` and the `only` refinements are not implemented. `while` is an alias for `any`, including its guard against looping on an empty match.
- **No memoization.** Backtracking is plain recursive descent, so some grammars can be slow.
- **Failed branches are not undone.** As in REBOL, captures, actions and edits made in a branch that later fails stay in place. The [reference](docs/Reference.md#what-is-undone-when-a-branch-fails) shows how to write rules around this.

## Performance

Build a rule once and reuse it, for example as a `static readonly` field, as you would a `Regex`. Rules compile to matcher nodes on first use. Text is matched as a `ReadOnlySpan<char>`, and literal searches are vectorized. `Parser.IsMatch` allocates nothing for rules that do not capture or edit.

Interpreted, Parse beats interpreted `Regex` on most tasks and beats `RegexOptions.Compiled` on extraction. `RegexOptions.Compiled` still wins on simple validation and recursion. Calling `.Compiled()` closes that gap. Edits run in linear time: within 2x of `Regex.Replace` when interpreted, and faster when compiled. [Benchmarks](docs/Benchmarks.md) has the numbers and how to reproduce them.

## Build

```bash
dotnet test                                                   # 500+ tests, 100% line and branch coverage
dotnet run --project samples/Samples                          # all samples (add a name to run one)
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*"
```

## License

[MIT](LICENSE)
