# RebolParse

[REBOL's `parse` dialect](http://www.rebol.com/docs/core23/rebolcore-15.html) for C#: a compact grammar language for matching, extracting and rewriting text and structured data. It sits between regular expressions and a full parser generator. Rules read left to right like the input they describe, they can recurse, and they run actions as they match.

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
- **Recursion.** Named rules can refer to each other, so balanced brackets, nested lists and expression grammars are direct to write. Regular expressions cannot do this without engine-specific extensions.
- **Capture, collect, rewrite.** `copy`, `collect`/`keep`, and `insert`/`change`/`remove` pull data out of input or edit it as it is matched.
- **Fast and allocation-free where it counts.** Rules compile on first use. `Parser.IsMatch` allocates nothing. See [Performance](#performance).

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

## Semantics at a glance

- A parse succeeds only if the rule matches **and consumes all the input**. Pass `partial: true` to allow leftovers.
- `a | b` is **ordered choice** with backtracking: the first alternative that matches wins.
- Repeats (`some`, `any`) are **greedy** and never give back.
- Matching is **case-sensitive**; pass `ignoreCase: true` for literals to ignore case.
- Paren code is **never evaluated**. Only `(@name)`, bound to a delegate you supply, is allowed, so rules can safely come from untrusted text.

## Documentation

| | |
|---|---|
| [Tutorial](docs/Tutorial.md) | Fifteen lessons with exercises and solutions, ending in an INI-file parser. Start here if you are new to parse. |
| [Reference](docs/Reference.md) | Every keyword and the text syntax, what a parse returns, errors, what backtracking undoes, and limits. |
| [Benchmarks](docs/Benchmarks.md) | Comparison with .NET `Regex`, with numbers and caveats. |
| `samples/Samples` | A runnable program: config lines, an arithmetic grammar, and a block-mode command parser. |

The public API is fully documented with XML comments, so IntelliSense shows what each keyword does.

## Keyword map

REBOL keywords, and the matching fluent builder from `using static RebolParse.P;`. See the [reference](docs/Reference.md#keywords) for exactly what each one matches and consumes.

| REBOL | Fluent | Notes |
|---|---|---|
| `"abc"` `#"a"` | `"abc"` `'a'` | implicit conversion to `Rule` |
| `a b` | `a + b` | sequence; one side must already be a `Rule` (or use `Seq(a, b)`) |
| `a \| b` | `a \| b` | ordered choice (or `Choice(a, b)`) |
| `some` `any` `opt` | `Some` `Any` `Opt` | |
| `3 r` `2 4 r` | `Rep(3, r)` `Rep(2, 4, r)` | |
| `skip` `end` `none` `fail` | `Skip` `End` `None` `Fail` | |
| `ahead` `not` | `Ahead` `Not` | lookahead, consume nothing |
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
- Backtracking is plain recursive descent with no memoization, so pathological grammars can be slow. Captures, actions and edits made in a branch that later fails are not undone, as in REBOL.

## Performance

Rules compile to specialized matcher nodes the first time they are used, so build a rule once and reuse it (a `static readonly` field is ideal), as you would a `Regex`. Text is matched as a `ReadOnlySpan<char>` with vectorized scanning for literals. `Parser.IsMatch` allocates nothing for rules that do not capture or edit; `Parser.Parse` returns captures and edits.

On flat text RebolParse runs at or ahead of .NET's interpreted `Regex` on most tasks and ahead of `RegexOptions.Compiled` on extraction and shallow recursion, while `Compiled` still wins plain validation and deep recursion. The one known slow path is `remove` on very large inputs. [Benchmarks](docs/Benchmarks.md) has the numbers, the method, and how to reproduce them.

## Build

```bash
dotnet test                                                   # 370+ tests, 100% line and branch coverage
dotnet run --project samples/Samples                          # runnable examples
dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*"
```

## License

[MIT](LICENSE)
