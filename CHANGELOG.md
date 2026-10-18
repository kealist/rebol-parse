# Changelog

Versions follow [semantic versioning](https://semver.org). Until 1.0 the API may change between minor versions.

## Unreleased

## 0.1.0

First release.

- The REBOL parse dialect for text and for lists of values: sequence, choice, repeats, `to`/`thru`, `not`/`ahead`, `copy`/`set`, marks, `into`, `collect`/`keep`, `break`/`reject`, `insert`/`change`/`remove`, `if`, named rules and actions, with ignore-case mode.
- Rules from text (`Dialect.Read`, `ReadRules`, `ReadGrammar`) or the fluent API.
- `Parser.IsMatch` that allocates nothing.
- `rule.Compiled()` to generate IL for text rules; it beats `RegexOptions.Compiled` on the benchmarks here.
- Edits that stay linear on large inputs.
