# Samples

Eight small programs, each built from a parse grammar plus a few lines of C#. They print what they do and check their own results, so `dotnet test` runs all of them and they cannot go stale.

```bash
dotnet run --project samples/Samples              # run everything
dotnet run --project samples/Samples -- json      # run one
dotnet run --project samples/Samples -- list      # see the names
dotnet run -c Release --project samples/Samples -- clean   # timings are only meaningful in Release
```

| Demo | File | What it shows |
|---|---|---|
| `calc` | [Calculator.cs](Samples/Calculator.cs) | A recursive grammar written as `name = rule` text. Actions fire as each piece matches and drive a value stack, so the parse evaluates as it goes. Error positions come from a partial parse. |
| `json` | [JsonDemo.cs](Samples/JsonDemo.cs) | A complete JSON reader and writer. Actions build values on a stack, with a marker pushed when an array or object opens. Includes path queries (`author.name`, `tags[1]`) written as a parse rule, and rejects a dozen kinds of malformed input. |
| `ini` | [IniDemo.cs](Samples/IniDemo.cs) | Parsing one line at a time, so an error names its line and the rest of the file still loads. Comments, quoted values, typed lookups. |
| `csv` | [CsvDemo.cs](Samples/CsvDemo.cs) | RFC 4180: quoted fields holding commas, doubled quotes and line breaks, then a small report built from the rows. |
| `log` | [LogDemo.cs](Samples/LogDemo.cs) | Two rules, one for the fixed layout of a line and one for the `key=value` pairs hidden in the free text, then LINQ rollups over 50,000 lines. |
| `clean` | [SourceCleaner.cs](Samples/SourceCleaner.cs) | Editing input as it is parsed with `remove` and `change`: strip comments without touching string literals, trim trailing whitespace, squeeze blank lines. Runs over a multi-megabyte file in well under a second. |
| `robot` | [RobotDemo.cs](Samples/RobotDemo.cs) | **Block mode.** The input is a list of words, numbers and strings, and `into` descends into nested blocks. The parse produces a command tree that then drives a robot on a grid. Errors report the index of the bad value. |
| `template` | [TemplateDemo.cs](Samples/TemplateDemo.cs) | The fluent API with a computed replacement: `Change(tagRule, ctx => ...)` rewrites `{{ name \| upper \| plural:item }}` tags in place. Nested data, chained filters, escapes. |

## Patterns worth copying

- **Rules decide, actions do.** Keep the grammar free of logic and let `(@name)` actions collect values. Actions run as soon as their piece matches, so put them after the part that can fail.
- **Backtracking does not undo actions.** Every demo relies on grammars where a branch only fires an action once it can no longer fail, or where a failure means the whole input is rejected anyway. See [what is undone](../docs/Reference.md#what-is-undone-when-a-branch-fails).
- **Build rules once.** Each demo reads its grammar in the constructor and reuses it, so the rule compiles one time.
- **Locate errors with a partial parse.** When a full parse fails, `Parser.Parse(text, rule, partial: true).Position` is how far a valid prefix went.
- **Capture variables are shared.** `copy x` writes into one table for the whole parse, so read the value in an action right away, as the calculator and JSON demos do, rather than after a nested rule might have set it again.
