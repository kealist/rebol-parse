# RebolParse

A REBOL-style `parse` dialect for C#. Rules match strings (character by character) or blocks (lists of values), with captures and inline actions. Two ways to write rules, one engine underneath.

## Install

```
dotnet add package RebolParse
```

Targets .NET 10.

## Example

```csharp
using RebolParse;
using static RebolParse.P;

// Fluent
var rule = Some(Digit) + "." + Some(Digit);
Parser.Parse("3.14", rule).Matched;            // true

// Text dialect, same rule tree
var same = Dialect.Read("some digit \".\" some digit");

// Captures and named actions
var kv = Dialect.Read("copy k to \"=\" \"=\" copy v to end (@done)",
    new Dictionary<string, Action<Ctx>> { ["done"] = c => Console.WriteLine(c.Vars["v"]) });
```

Semantics follow REBOL: ordered choice (`|`) with backtracking, greedy repeats, and a parse succeeds only if the whole input is consumed (pass `partial: true` to allow a prefix match).

## Keyword map

| REBOL | Fluent | Notes |
|---|---|---|
| `"abc"` `#"a"` | `"abc"` `'a'` | implicit conversion to `Rule` |
| `a b` | `a + b` | sequence |
| `a \| b` | `a \| b` | ordered choice |
| `some` `any` `opt` | `Some` `Any` `Opt` | |
| `3 r` `2 4 r` | `Rep(3, r)` `Rep(2, 4, r)` | |
| `skip` `end` `none` `fail` | `Skip` `End` `None` `Fail` | |
| `ahead` `not` | `Ahead` `Not` | |
| `to` `thru` | `To` `Thru` | |
| `copy v r` `set v r` | `Copy("v", r)` `Set("v", r)` | results in `ParseResult.Vars` |
| `into r` | `Into(r)` | descends into a nested block |
| `quote word` | `Lit(new Word("word"))` | |
| `name:` `:name` | `Mark("name")` `Seek("name")` | save / restore position |
| `break` | `Break` | leaves the enclosing `some`/`any`/`opt` |
| `(code)` | `Do(ctx => ...)` | text form binds by name: `(@name)` |
| `digit alpha alnum upper lower space newline tab` | `Digit` ... | string mode |
| `integer! decimal! string! word! block!` | `Integer` ... | block mode |
| bare word | `Ref("name")` | looks up the rule table passed to `Parse`; recursion works |

## Differences from REBOL

- Paren code is not evaluated. Actions are C# delegates bound with `(@name)`.
- Not implemented: `reject`, `change`, `insert`, `remove`, `collect`/`keep`, case-insensitive mode, `to`/`thru` on non-rule datatype shorthands. Rules never mutate the input.
- Backtracking is plain recursive descent with no memoization, so pathological grammars can be slow.
- Variables set inside an alternative that later fails are not rolled back (REBOL behaves the same way).

## Build

```bash
dotnet test
dotnet run --project samples/Samples
```

## License

[MIT](LICENSE)
