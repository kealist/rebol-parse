# Architecture

How RebolParse is put together, for people changing it. If you only want to use the library, start with the [tutorial](Tutorial.md) and the [reference](Reference.md).

## The pipeline

```
 rule text ──Dialect──┐                                   ┌── string input ──► Compiler ──► Node tree ──► TextEngine
                      ├──► Rule tree (immutable records) ─┤                       │
 fluent P.* / ops ────┘                                   │                       └─ Compiled() ─► IlCompiler ─► delegate
                                                          └── block input ───► Matcher (interpreter over ISeries)
```

A **rule** is a tree of immutable records (`Rule.cs`). The text reader and the fluent builder both produce the same tree, so everything downstream sees one representation. The tree is then run by one of three engines, chosen by the input type and by whether `.Compiled()` was used:

| Input | Engine | Where | Used when |
|---|---|---|---|
| `string` | compiled **nodes** over a `ReadOnlySpan<char>` | `Compiled.cs` | the default for text |
| `string` | **IL** generated with expression trees | `IlCompiler.cs` | the rule was wrapped by `.Compiled()` (every keyword compiles for text) |
| `IReadOnlyList<object>` | tree-walking **interpreter** over `ISeries` | `Matcher.cs`, `Series.cs` | all block input |

All three must agree on every answer. The tests exist to hold them to that (see [Testing](#testing)).

## Source map

| File | Contents |
|---|---|
| `Rule.cs` | The rule records (`Lit`, `Seq`, `Alt`, `Repeat`, `Copy`, ...) and the operators on `Rule` |
| `P.cs` | Fluent builders (`Some`, `Copy`, `Digit`, ...) |
| `RuleExtensions.cs` | Chaining forms (`rule.Some()`) and `Compiled()` |
| `Dialect.cs` | The text reader: tokenizer, `Read`, `ReadRules`, `ReadGrammar` |
| `Matcher.cs` | `Ctx` (per-parse state), `ParseResult`, `Parser` (the entry points), and the block interpreter |
| `Series.cs` | `Word`, `ISeries`, `BlockSeries` (block input with copy-on-write edits) |
| `Compiled.cs` | `TextEngine`, `Compiler`, and every node class for text |
| `IlCompiler.cs` | `CompiledRule`, the expression-tree generator, and the runtime helpers it calls |
| `CharSet.cs` | `Chars("a-z_")` sets: a bit test for ASCII |

## Rules and the two readers

Records give value equality and cheap construction, and make a rule safe to share between threads. `Dialect` is a hand-written tokenizer plus recursive-descent reader (`ReadAlt` for `|`, `ReadItem` for one element, `ReadWord` for keywords). Paren code is never evaluated: `(@name)` only looks a name up in a dictionary the caller supplies, which is what makes reading rules from untrusted text safe. `ReadGrammar` splits `name = rule` definitions by finding top-level words followed by a `=` token.

## Entry points and state

`Parser.Parse` and `Parser.IsMatch` (in `Matcher.cs`) are the only doors in. `Parse` allocates a `Ctx` and a `TextEngine` per call and returns a `ParseResult`. `IsMatch` allocates nothing: it takes a per-thread `Scratch` holding a reusable `Ctx`, engine and block series, marked read-only so any edit throws. A re-entrant call (an action that calls `IsMatch`) gets a fresh `Scratch` instead of the cached one.

`Ctx` holds everything a running parse mutates: `Vars` (from `copy`, `set`, `collect`), `Marks`, the `collect` stack, the `break`/`reject` flags, `LoopDepth` and `Rules` (the named-rule table). Its dictionaries are created on first use so a parse that never captures allocates none.

## The node engine (strings)

`Compiler.Get(rule)` turns a rule into a tree of `Node` objects, once per rule instance. The result is cached in a `ConditionalWeakTable` keyed by the rule, with a `[ThreadStatic]` last-used slot in front of it so a hot loop over one rule pays a reference comparison. This is why the docs say to build rules once.

**The node contract.** `bool Match(TextEngine e, ref ReadOnlySpan<char> text, ref int pos)`. On success `pos` is the new position. On failure `pos` is unchanged: every node restores it itself. `text` is passed by `ref` because an edit can replace it for everything upstream.

**Specialization.** The compiler picks the cheapest node for each shape: a repeat over one character test is a single tight loop (`RepeatChar`); `to`/`thru` on a literal use the vectorized `IndexOf` (`ScanString`, `ScanChar`); literals use `StartsWith`; `to end` is a position assignment. A node whose subtree cannot `break`, `reject`, `keep` or edit is marked `Pure`, and pure subtrees skip the bookkeeping described next.

**`TextEngine.Try`.** Wherever a failure is absorbed (choice, loops, lookahead, scans), `Try` runs the node and, on failure, undoes what the failed attempt may have done to *flags and the collect list*: it restores `BreakRequested`/`RejectRequested` and truncates the list `keep` was filling. It deliberately does not undo variables, marks, actions or edits, which matches REBOL and is documented in the reference.

**Loops stop when an iteration makes no progress** (no movement and no change in text length), so `any none` terminates.

### Scans for `not X skip` (`StopSet`, `RepeatUntil`)

`some [not X skip]` is the usual way to say "up to the next X". When `X` reduces to single characters and character classes (a literal, a class, or a choice of them), the compiler replaces the loop with one scan. Plain characters use `SearchValues<char>.IndexOfAny`; classes fall back to a predicate loop; ignore-case mode compares upper-cased characters. A multi-character `X` is not equivalent (the loop would step one character at a time) and is left alone.

### First-character dispatch (`FirstSet`, `AltNode`)

Each node can answer `First(...)`: which characters can this node start a match with, and is that answer exact? A choice builds, lazily and per (rule table, case mode), a 256-bit set for each alternative whose answer is exact, and skips an alternative when the next character is not in its set. Characters above 255 are always treated as "maybe". `Ref` nodes resolve their target to answer, with a depth limit for rules that refer back to themselves.

Two details matter for correctness:

- The character is **read again for each alternative**. A failed alternative may have edited the text under `pos`, so the next alternative must look at what is there now. A randomized test found this.
- The table records which rule table it was built for and is rebuilt when a different one is passed, because the answer for a `Ref` depends on the table.

## Edits and the gap buffer

`insert`, `change` and `remove` must make later matching see the edited text, while never touching the caller's string. `TextEngine` copies the text into a `char[]` on the first edit and keeps it as `buffer[0..gapStart) + buffer[gapStart+gapSize..length)`, a *gap buffer*. A removal moves the gap to the edit point and widens it; an insert writes into the gap, growing it geometrically when it is too small. A sweep of edits from left to right therefore moves each character at most once, which is why stripping 100 KB takes under a millisecond and not 23.

Nodes read the text through a span that starts *after* the gap, so every index at or beyond `gapStart` is correct. Indices before it are **stale**. The invariant that keeps this safe: whoever can put the position back before `gapStart` calls `TextEngine.Settle` first, which closes the gap (one copy of the tail) and refreshes the span. The places that do it:

| Place | Why |
|---|---|
| end of `Try` | a failed branch restored `pos` after editing |
| `to` (`ScanNode`) | it rewinds to where the body *started* matching |
| `ahead` | succeeds without consuming, so `pos` is where it began |
| `:mark` (`SeekNode`) | jumps to a saved position |
| `copy`, `set`, `keep`, `keep pick` | they read from the start of what their body matched |

Add a new node that moves `pos` backwards and you must add its `Settle`. `EditParityTests` runs thousands of random edit-heavy rules on both the text engine and the list interpreter and demands identical results, and every `Settle` call has been mutation-checked.

## Block input

Block input does not use nodes. `Matcher` walks the rule tree directly against an `ISeries`. `BlockSeries` copies the list on the first edit, into a list it reuses, so edits never touch the caller's list. `into` descends by taking a pooled nested `BlockSeries` per depth so repeated `into` allocates nothing. Matching items use `MatchLiteral` (string, char and `Word` compare case-insensitively under `ignoreCase`). Because this is the slow, general path and text is the common case, performance work has gone into the node engine.

## The IL back end

`rule.Compiled()` wraps a rule in `CompiledRule`. When `Compiler.Compile` meets one, it asks `IlCompiler.TryCompile` for a delegate. Every rule kind compiles for text; a rule type the engine does not know falls back to the node compiler, which rejects it as it would without `Compiled()`. Block input never reaches the IL compiler.

**Why expression trees.** They are compiled to IL by the framework, are far easier to build and debug than raw `ILGenerator` code, and have no verification traps. The cost is that expression trees cannot hold `ReadOnlySpan<char>`, so generated code works on a `string` and an `int` offset and calls small static helpers (`IlRuntime`) for anything vectorized.

**The delegate.** `int Fn(string text, int start, TextEngine e)` returns the position after the match, or -1. Each rule kind is emitted inline into one method: a sequence is straight-line code, and a choice, loop, lookahead or scan owns a saved position that it restores on failure. The generator's contract mirrors the node contract in reverse: code for a rule *jumps to a `fail` label* when it cannot match, leaving `pos` unspecified, and whoever backtracks restores it. Character tests become direct calls when the delegate points at an ordinary public method (`char.IsAsciiDigit`, `CharSet.Contains`), and go through the delegate otherwise.

**Bookkeeping that matches the node engine.** The node engine's `Try` undoes a failed attempt's effect on the `break`/`reject` flags and the `collect` list. The generated code does the same, with a snapshot (`IlRuntime.Snap`) taken before, and a restore after, each attempt that can break, reject or keep: a choice alternative, a loop iteration, `not`, and each position of a scan. It is skipped for attempts the generator can tell are pure, by the same definition as `Node.Pure`, and it is not needed for `ahead` or a call to another rule, whose failure always propagates to an attempt that restores. Loops that can break or reject use the same iteration protocol as `RepeatNode`: clear the flags, run the body, stop on a break, reject or no progress.

**Edits: the buffered variant.** A string cannot be edited cheaply, so a rule that edits is generated against the engine's gap buffer instead: `char[] buf`, an offset, and a length, with character `i` at `buf[off + i]` for any `i` at or after the gap. The generated code reloads those three after anything that can edit (an edit, a call to another rule) and calls `NeedsSettle`/`SettleNow` at the same places the node engine calls `Settle`, so a position that rewinds before an edit sees correct text. `TextEngine.EnsureBuffer` copies the input into the buffer when such a rule starts, without counting the text as edited until an edit is made, so `ParseResult.Output` is still the original string when nothing changed. A rule that does not edit reads the string directly, which is faster, and gets a buffered variant made on demand if it ever runs after another rule has edited the text (`IlNode.BufferedFn`).

Which rules must be buffered is decided at compile time. A rule that contains an edit always is. A rule that only *calls* a rule that edits has to be as well, and that depends on the rule table, so `Compiled(table)` finds the rules that can reach an edit through `Ref`s (`IlCompiler.RulesReachingEdits`) and marks them. A rule compiled on its own that calls an editing rule throws a clear error rather than reading stale text.

**Named rules.** A `Ref` becomes a call through a `RefSite`, which looks the target up once per rule table and caches it behind an immutable holder (so concurrent use is safe). A compiled target is called directly, in its buffered variant when the caller is buffered. An interpreted target is run through its node, from a span over the string or over the buffer. After a call from string-reading code, the engine is checked for edits.

**What it does not do.** There is no first-character dispatch in the generated choice (it was tried and measured no better on the JSON grammar) and no inlining across `Ref`. Grammars made of many tiny rules therefore gain little: see the [benchmarks](Benchmarks.md#compiled-to-il). A buffered rule pays one copy of the input when it starts.

## Invariants worth knowing

- A parse succeeds only if it consumes everything, unless `partial` is set.
- Failure restores the position; success may move it.
- Backtracking does not undo variables, marks, actions or edits (as in REBOL), but does undo `break`/`reject` and `keep`.
- Loops stop on a no-progress iteration, which includes "the text changed length".
- Character classes ignore `ignoreCase`; literals honour it.
- `IsMatch` is allocation-free and read-only; an editing rule throws.
- Rules and compiled forms are immutable once built and safe to share across threads.

## Testing

| What | Where | How it protects |
|---|---|---|
| Behaviour by feature | `ParseTests`, `FeatureTests`, `MoreFeatureTests`, `BacktrackingTests`, `ConformanceTests` | named cases per keyword and REBOL edge case |
| Text vs block engines | `EngineParityTests` | the same rule rows on both engines |
| Edits vs a plain list | `EditParityTests` | thousands of random edit rules, text engine against the list interpreter; plus deterministic rewind cases for every `Settle` |
| Choice dispatch | `DispatchTests` | alternatives, ignore-case, non-ASCII, undefined rules, table switching |
| Interpreter vs IL | `CompiledRuleTests` | 4,000 random rules including edits, the parity rows, an exhaustive 400,000-case check of attempt-edit-fail shapes inside `to`, `thru`, `not`, `any` and `ahead`, recursion and rule tables |
| Docs stay true | `TutorialTests`, `DocsExamplesTests`, `SamplesTests` | every tutorial solution and every sample demo runs as a test |
| Allocation | `AllocationTests` | `IsMatch` stays at zero bytes |
| Coverage | `CoverageTests` and the rest | 100% line and branch coverage of the library |

Two practices have paid off: **randomized differential tests** (two engines, one rule, compare everything) catch the bugs hand-written tests miss, and **mutation checks** (delete a guard, confirm a test fails) show whether a test really protects what it claims to. Random rule generators must avoid rules that legitimately never terminate, such as an insert followed by a failure inside a loop that counts text growth as progress; the generators say so in their comments.

Run `dotnet test`. Benchmarks are in `benchmarks/RebolParse.Benchmarks` (`dotnet run -c Release --project benchmarks/RebolParse.Benchmarks -- --filter "*"`), and `--job short` is enough to see a regression.

## Adding a keyword

1. **Rule.cs**: add a record with an XML doc comment.
2. **P.cs / RuleExtensions.cs**: add the fluent builder.
3. **Dialect.cs**: read it in `ReadWord` and add it to the reference's keyword table.
4. **Matcher.cs**: handle it in the block interpreter.
5. **Compiled.cs**: add a node and a case in `Compiler.Compile`. Decide `Pure`, give it a `First` if it has a known first character, and make it restore `pos` on failure. If it moves `pos` backwards or reads text from before `pos`, add `Settle`.
6. **IlCompiler.cs**: emit it, or leave it out of `Supports` so a rule using it falls back. Add it to the fallback list in `CompiledRuleTests`.
7. **Tests**: a case in `ConformanceTests` or `FeatureTests`, parity rows if it works on both inputs, and mention it in the random generators if it fits.
8. **Docs**: the Reference keyword table, the README keyword map, and a tutorial line if learners will meet it.

## Known limits and ideas

- Recursion depth is bounded by the stack, and left recursion recurses forever (documented).
- Backtracking has no memoization, so a pathological grammar can be exponential.
- The IL back end could inline small non-recursive rules to help grammars of many tiny rules, and could read the original string for a buffered rule until its first edit instead of copying the input up front.
- Block input has no compiled path.
- A moving gap costs a tail copy whenever a rule rewinds behind an edit; a piece table would avoid that at the price of a non-contiguous text.
