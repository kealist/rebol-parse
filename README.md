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

New to parse? Work through the [tutorial](docs/Tutorial.md): fifteen lessons with exercises and solutions.

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
| `break` | `Break` | leaves the enclosing `some`/`any`/`opt`, succeeding |
| `reject` | `Reject` | leaves the enclosing `some`/`any`/`opt`, failing it; outside a loop it is `fail` |
| `insert v` | `Insert(v)` | inserts at the position and moves past it |
| `change r v` | `Change(r, v)` | replaces what `r` matched, then moves past the new value |
| `remove r` | `Remove(r)` | deletes what `r` matched |
| `collect v r` | `Collect("v", r)` | list of everything `r` keeps, stored in `Vars` |
| `keep r` | `Keep(r)` | appends the match to the enclosing `collect` |
| `keep pick r` | `KeepPick(r)` | appends only the first matched item |
| `keep (@fn)` | `Keep(ctx => value)` | appends a computed value; `null` keeps nothing |
| `if (@fn)` | `If(ctx => bool)` | succeeds without consuming when the condition holds |
| `(code)` | `Do(ctx => ...)` | text form binds by name: `(@name)` |
| `digit alpha alnum upper lower space newline tab` | `Digit` ... | string mode |
| `integer! decimal! string! word! block!` | `Integer` ... | block mode |
| bare word | `Ref("name")` | looks up the rule table passed to `Parse`; recursion works |

## Editing and collecting

`insert`, `change` and `remove` edit a private copy of the input; your string or list is never modified. Read the result from `ParseResult.Output` (a string, or a list for block input).

```csharp
var r = Parser.Parse("a-b-c", Dialect.Read("any [change \"-\" \"+\" | skip]"));
r.Output;   // "a+b+c"
```

A value is a string, char or integer literal, or a variable name set earlier by `copy`/`set` (`copy w to end insert w`). The fluent forms also take a `Func<Ctx, object?>`. In block mode a value is inserted as one item, and edits inside `into` are written back to the parent. Marks saved before an edit are not adjusted.

`collect` stores a list in `Vars`: strings in text mode, and in block mode the item itself for a one-item match or a list for a longer one. A `keep` inside a branch that later fails is dropped; captures, actions and edits are not.

## Case-insensitive mode

Literal strings, chars and words match regardless of case with `Parser.Parse(input, rule, ignoreCase: true)`. Character sets such as `upper` stay case-sensitive. Matching is case-sensitive by default, whereas REBOL's is case-insensitive by default.

## Differences from REBOL

- Paren code is not evaluated. Actions are C# delegates bound with `(@name)`.
- Matching is case-sensitive unless you pass `ignoreCase: true`.
- Edits apply to a copy of the input and come back in `ParseResult.Output`.
- Paren code in `keep (@fn)` and `if (@fn)` is bound by name, like actions: pass a `values` table to `Dialect.Read`, mapping names to `Func<Ctx, object?>`. `if` treats `false` and `null` as false and any other result as true.
- Backtracking is plain recursive descent with no memoization, so pathological grammars can be slow.
- Variables set inside an alternative that later fails are not rolled back (REBOL behaves the same way).

## Performance

Regex is faster on flat text, by roughly 4-17x for validation and capture. RebolParse is aimed at grammars regex handles badly. See [benchmarks](docs/Benchmarks.md) for the numbers and the one known slow path (`remove` on large inputs).

## Build

```bash
dotnet test
dotnet run --project samples/Samples
```

## License

[MIT](LICENSE)
