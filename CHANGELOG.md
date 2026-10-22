# Changelog

Versions follow [semantic versioning](https://semver.org). Until 1.0 the API may change between minor versions.

## Unreleased

- **Security fixes.**
  - Deeply nested rule text throws `FormatException` (limit `Dialect.MaxDepth`, 256) instead of overflowing the stack.
  - Deeply nested input and left-recursive grammars throw `InsufficientExecutionStackException` instead of crashing the process. This applies to all three engines.
  - A rule shared by threads that pass different rule tables could run a named rule from the other thread's table. It now uses its own table every time.
  - The edit buffer is cleared when it goes back to the shared `ArrayPool`, so edited text no longer reaches the next code to rent from the pool.
- The package also targets `netstandard2.0` (.NET Framework 4.6.1+, .NET Core 2.0+, Mono, Unity), with `System.Memory` as its only dependency. Same engines and results; `net10.0` keeps the vectorized `SearchValues` scans.

## 0.1.2

First release on nuget.org. The tags `v0.1.0` and `v0.1.1` were cut while the release workflow was being set up and published nothing.

- The REBOL parse dialect for text and for lists of values: sequence, choice, repeats, `to`/`thru`, `not`/`ahead`, `copy`/`set`, marks, `into`, `collect`/`keep`, `break`/`reject`, `insert`/`change`/`remove`, `if`, named rules and actions, with ignore-case mode.
- Rules from text (`Dialect.Read`, `ReadRules`, `ReadGrammar`) or the fluent API.
- `Parser.IsMatch` that allocates nothing.
- `rule.Compiled()` to generate IL for text rules; it beats `RegexOptions.Compiled` on the benchmarks here.
- Edits that stay linear on large inputs.
