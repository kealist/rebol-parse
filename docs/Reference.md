# Reference

The complete behaviour of the parse dialect: the text syntax, every keyword, what a parse returns, and the rules for errors and side effects. For a guided introduction, see the [tutorial](Tutorial.md).

## Text syntax

Rules are read by `Dialect.Read(text, actions, values)`. Whitespace separates items and is otherwise ignored.

| Form | Meaning |
|---|---|
| `"abc"` | the literal text `abc` |
| `#"a"` | the single character `a` |
| `42` | a number is a repeat count, not a literal: `3 digit` is three digits. Use `quote 42` to match the number 42 in a block |
| `word` | a keyword, a built-in class, or a reference to a named rule (in that order) |
| `name:` | save the current position as `name` |
| `:name` | jump to the position saved as `name` |
| `[ ... ]` | group items; the outer brackets of a whole rule are optional |
| `a \| b` | ordered choice, lowest precedence: `a b \| c` is `(a b) \| c` |
| `(@name)` | call the action `name` |
| `charset "a-z_"` | one character from a set (see below) |
| `; ...` | a comment, to the end of the line |

**Character sets.** `charset "..."` takes a string written like the inside of a regex class. A `-` between two characters makes a range (`a-z`, `0-9a-fA-F`); a `-` first or last is just a hyphen (`-+*/`). A range must not run backwards: `+-*/` is an error because `+` comes after `*`, so write `-+*/`. In a charset string, `^-` still means a tab, so put the hyphen first or last instead. Sets are case-sensitive even under `ignoreCase`, and are tested without allocating (a bit test for ASCII).

**Grammars.** `Dialect.ReadGrammar(text)` reads several rules at once, written as `name = rule`. The `=` must be its own word, and a definition runs until the next `name =` at the top level, so a rule can span lines and contain groups and comments. It returns the rule table to pass to `Parse`. It is an error for a grammar to have text before its first definition, an empty definition, or two definitions with one name.

```
expr   = term any [charset "+-" term]
term   = factor any [charset "*/" factor]
factor = some digit | "(" expr ")"
```

**Escapes** in strings and chars use REBOL's caret: `^/` newline, `^-` tab, `^"` a quote, `^^` a caret. Any other `^x` is just `x`. So `#"^""` matches a double quote.

**Keywords and built-ins** are case-insensitive in rule text (`SOME Digit` works). Variable names and mark names are case-sensitive, and so are rule names in `Ref` lookups unless the dictionary you pass was built with a case-insensitive comparer.

**Paren code is never evaluated.** `(print 1)` is an error; only `(@name)` is allowed, bound to a C# delegate you pass in. This makes it safe to read rules from untrusted text.

## Fluent operators and chained forms

The fluent API has the same rules as the text, plus operator and chained shorthands that build exactly the same trees:

| | |
|---|---|
| `a + b`, `a \| b` | sequence, ordered choice (flattened: `a + b + c` is one sequence of three) |
| `+r`, `!r`, `r * n`, `n * r` | `some`, `not`, exactly `n` times |
| `r.Some()` `.Any()` `.Opt()` `.Times(n)` `.Times(min, max)` | repeats |
| `r.Not()` `.Ahead()` `.To()` `.Thru()` `.Into()` | lookahead, scans, `into` |
| `r.Copy("v")` `.Set("v")` `.Keep()` | captures and `collect` |
| `r.ChangeTo(v)` `.Remove()` | edits |
| `r.Then(action)` | the rule, then an action |
| `rule.IsMatch(input)` `rule.Parse(input)` | the same as the `Parser` methods, with the rule first |

As always with C# operators, one operand of `+` or `\|` must already be a `Rule`: `"a" + "b"` is a string. Use `Seq` and `Choice` to combine plain strings.

## Keywords

| Keyword | Matches | Consumes |
|---|---|---|
| `skip` | any one item; fails at the end | 1 item |
| `end` | only at the end of the input | nothing |
| `none` | always | nothing |
| `fail` | never | n/a |
| `some r` `any r` `opt r` | `r` one or more, zero or more, zero or one times | what `r` matched |
| `n r` `n m r` | `r` exactly `n` times, or between `n` and `m` | what `r` matched |
| `while r` | `r` zero or more times; same as `any r` (REBOL's `while` has no no-progress guard, this one does) | what `r` matched |
| `ahead r`, `and r` | if `r` matches here | nothing |
| `not r` | if `r` does not match here | nothing |
| `to r` | scans forward to where `r` matches | up to, not including, the match |
| `thru r` | scans forward to where `r` matches | up to and including the match |
| `copy v r` | `r`, storing the matched input in `v` | what `r` matched |
| `set v r` | `r`, storing its first item in `v` (`null` if empty) | what `r` matched |
| `into r` | a nested list, matched entirely by `r` (blocks only) | the list |
| `quote x` | the literal string, char, number or word `x` | 1 item |
| `break` | leaves the enclosing loop, succeeding | nothing |
| `reject` | leaves the enclosing loop, failing it; outside a loop, plain failure | nothing |
| `insert v` | inserts `v` and steps past it | nothing from the input |
| `change r v` | `r`, replacing it with `v` | what `r` matched |
| `remove r` | `r`, deleting it | nothing remains |
| `collect v r` | `r`, storing everything it `keep`s as a list in `v` | what `r` matched |
| `keep r` | `r`, adding the match to the enclosing `collect` | what `r` matched |
| `keep pick r` | `r`, adding only its first item | what `r` matched |
| `keep (@fn)` | always, adding the value from `fn` (`null` adds nothing) | nothing |
| `if (@fn)` | if `fn` returns true (or any non-null, non-`false` value) | nothing |

**Built-in classes** (text): `digit` (ASCII 0-9), `alpha` (any letter), `alnum`, `upper`, `lower`, `space` (any whitespace, including newlines), `newline`, `tab`. **Datatypes** (blocks): `integer!`, `decimal!`, `string!`, `word!`, `block!`.

**Values** for `insert` and `change` are a string, char or integer literal, or the name of a variable set earlier by `copy`/`set`. Referring to a variable that was never set throws `KeyNotFoundException`. In the fluent API they can also be any `Func<Ctx, object?>`.

## Matching semantics

- **Whole input.** A parse succeeds only if the rule matches *and* consumes all the input. Pass `partial: true` to accept a prefix; `ParseResult.Position` then tells you how far it got.
- **Ordered choice.** `a | b` takes the first alternative that matches. If something later fails, the parse backtracks into the choice and tries the next alternative.
- **Greedy repeats.** `some` and `any` take as much as they can and never give anything back. A loop stops when an iteration consumes nothing, so `any none` terminates.
- **Failure restores position.** A rule that fails leaves the position where it started.
- **Case.** Matching is case-sensitive. With `ignoreCase: true`, literal strings, chars and words ignore case; character classes (`upper`, `lower`) do not.
- **Block input.** Strings, chars and numbers in a rule match equal items; `quote word` matches a `Word`; `into` descends into a nested list. A text parse never matches numbers, words or `into`.

## What is undone when a branch fails

When a branch fails and the parse backtracks, **these are dropped**: its position, a `break` or `reject` it made, and anything it `keep`ed. **These are not undone** (REBOL behaves the same way): variables set by `copy`/`set`, actions already run, marks saved, and edits made by `insert`/`change`/`remove`. Write rules so a branch captures or edits only after it can no longer fail, or capture into `collect` instead.

## Results

`Parser.Parse(input, rule, rules, partial, ignoreCase)` returns a `ParseResult`:

| Member | Meaning |
|---|---|
| `Matched` | whether the rule matched (and, unless `partial`, consumed everything) |
| `Position` | where matching stopped |
| `Vars` | values from `copy`, `set` and `collect`, by name |
| `Output` | the input after any edits: a `string`, or an `IReadOnlyList<object>` for blocks. If no rule edited, it is the original instance |

A `ParseResult` converts to `bool`, so `if (Parser.Parse(...))` works. Captures are strings in text and lists in blocks; `set` captures a `char` in text and the item itself in blocks.

`Parser.IsMatch(...)` takes the same arguments and returns only a `bool`. It allocates nothing for rules that do not capture, and **throws `InvalidOperationException` for rules that edit** since it has no way to return the result. The context passed to actions during `IsMatch` is reused between calls on a thread: do not keep a reference to it.

## Errors

| Thrown | When |
|---|---|
| `FormatException` | malformed rule text: unmatched or missing bracket, unterminated string or paren, a keyword with nothing to apply to, a char literal longer than one character, paren code that is not `(@name)` |
| `KeyNotFoundException` | at read time, a `(@name)` with no registered action or value function; at parse time, a `Ref` to an undefined rule, a `Seek` to an unset mark, or an `insert`/`change` of an unset variable |
| `InvalidOperationException` | `keep` outside `collect`; an editing rule inside `IsMatch` |

## Threading and reuse

Rules are immutable and safe to share across threads. The first parse with a rule compiles it into a faster internal form, cached for that rule instance, so build rules once (a `static readonly` field is ideal) rather than per call. Re-reading the same text with `Dialect.Read` creates a new rule that compiles again. `IsMatch` keeps its working state per thread; it is safe to call from many threads and to call re-entrantly from an action.

## Limits

- Recursion depth is bounded by the stack: a rule that recurses a few thousand levels deep on one thread can overflow it. Left recursion (`expr = expr "+" digit`) recurses forever; rewrite it with a loop.
- Backtracking is plain recursive descent with no memoization, so pathological grammars can take exponential time.
- Edits to a string are cheap when they sweep left to right, as in `any [remove r | skip]`. Putting the position back before an earlier edit (a failed branch, `to` or `ahead` around an edit, a `:mark`) makes the engine close up its gap, which costs a copy of the rest of the text.
- Marks saved before an edit are not adjusted for it.
