# RebolParse tutorial

Fifteen short lessons, each with exercises. By the end you will have written an INI-file parser from scratch.

**How to work through it.** Read a lesson, try the exercises before opening the solution, and run your rule against the listed inputs. Every solution in this tutorial is checked by `tests/RebolParse.Tests/TutorialTests.cs`, so the answers are known to work.

A scratch file is the easiest place to experiment. From the repo root:

```bash
dotnet new console -o ../rebol-scratch && dotnet add ../rebol-scratch reference src/RebolParse
```

Every lesson uses this helper. Paste it into `../rebol-scratch/Program.cs` and run it with `dotnet run --project ../rebol-scratch`:

```csharp
using RebolParse;
using static RebolParse.P;

static void Try(string rule, params string[] inputs)
{
    var r = Dialect.Read(rule);
    foreach (var s in inputs)
        Console.WriteLine($"{(Parser.Parse(s, r).Matched ? "match   " : "no match")}  \"{s}\"");
}

Try("\"hello\"", "hello", "hello!", "Hello");
```

In your own code you will mostly write rules as C# strings. In this tutorial a rule is shown as plain text, and you wrap it in quotes yourself.

---

## 1. Your first parse

A rule is a description of what input looks like. `Parser.Parse(input, rule)` answers yes or no.

```csharp
Parser.Parse("hello", Dialect.Read("\"hello\"")).Matched;   // true
```

A string in the rule matches that exact text. **The rule must consume the whole input**, so `"hello!"` does not match `"hello"`. To allow leftovers, pass `partial: true`:

```csharp
Parser.Parse("hello world", rule, partial: true).Matched;   // true
```

`ParseResult` also tells you `Position` (how far matching got) and `Vars` (captures, lesson 6).

### Exercises

1. Write a rule that matches exactly `hello`. It must reject `hello!`, `hell` and `Hello`.
2. Make `hello world` match it too, without changing the rule.

<details><summary>Solutions</summary>

1. `"hello"`
2. Same rule, with `partial: true`.

</details>

---

## 2. Sequence, choice and repeat

| Rule | Meaning |
|---|---|
| `a b` | `a`, then `b` |
| `a \| b` | `a`, or if that fails `b` |
| `some a` | one or more `a` |
| `any a` | zero or more `a` |
| `opt a` | zero or one `a` |
| `3 a`, `2 4 a` | exactly 3, or between 2 and 4 |
| `[ ... ]` | grouping |
| `digit alpha alnum upper lower space` | character classes |

Choice is **ordered**: `a | b` tries `a` first and only tries `b` if `a` fails. Repeats are greedy and never give back.

```
some digit opt alpha        ;  "42" "42x" but not "x"
```

Comments start with `;` and run to the end of the line.

### Exercises

1. One or more digits, then an optional letter. Accept `7`, `42x`, `007`. Reject `x`, `4x2`, the empty string.
2. Accept `yes`, `no` or `maybe` only. Reject `ye`, `nope`, the empty string.
3. A phone number: three digits, a dash, four digits. Accept `555-1234`. Reject `55-1234`, `555-123`, `5551234`.
4. A colour: `#` followed by exactly six letters or digits. Accept `#00ff00`, `#abcdef`. Reject `#abc`, `00ff00`, `#00ff000`.

<details><summary>Solutions</summary>

```
some digit opt alpha
"yes" | "no" | "maybe"
3 digit "-" 4 digit
"#" 6 [digit | alpha]
```

</details>

---

## 3. The fluent builder

Everything you can write as text you can build in C#. `using static RebolParse.P;` makes it read almost the same.

```csharp
var rule = Some(Digit) + "." + Some(Digit);     // some digit "." some digit
Parser.Parse("3.14", rule).Matched;             // true
```

`+` is sequence and `|` is choice. Strings and chars convert to rules implicitly. There is one trap: `"b" + "c"` is two plain strings, so C# joins them into `"bc"` before any rule exists. At least one side must already be a `Rule`:

```csharp
(Rule)"b" + "c"          // a sequence
Seq("b", "c")            // also fine
Choice("a", Seq("b", "c"))
```

`Choice(...)` and `Seq(...)` take any number of rules and avoid the problem entirely.

### Exercises

1. Build `"a" | "b" "c"` with the fluent API. It accepts `a` and `bc` and rejects `b`, `ab`, `c`.
2. Build "one to three digits, then optionally a dot and more digits". Accept `7`, `123`, `12.5`. Reject the empty string, `1234`, `1.`.

<details><summary>Solutions</summary>

```csharp
var one = Choice("a", Seq("b", "c"));
var two = Rep(1, 3, Digit) + Opt("." + Some(Digit));   // "." + Some(..) works: Some returns a Rule
```

</details>

---

## 4. Rule tables and naming things

A bare word that is not a keyword is a **reference** to a named rule. You supply the table of names when you parse:

```csharp
var rules = Dialect.ReadRules(new Dictionary<string, string>
{
    ["op"]   = "\"+\" | \"-\" | \"*\" | \"/\"",
    ["calc"] = "some digit op some digit",
});
Parser.Parse("12*3", Ref("calc"), rules).Matched;   // true
```

Names let you reuse pieces and, in lesson 9, recurse. The text dialect has no character-set literal, so a set of characters is either a choice of strings, or `CharIn("+-*/")` from the fluent side.

### Exercises

1. An identifier: a letter followed by any letters or digits. Accept `x`, `abc9`, `R2D2`. Reject `9a`, the empty string, `a-b`.
2. An optionally signed integer. Accept `5`, `+5`, `-120`. Reject `+`, `5-`, the empty string.
3. Using a rule table, write `calc` so `12*3` matches and `12^3` does not. Then write the same thing fluently with `CharIn`.

<details><summary>Solutions</summary>

```
alpha any alnum
opt ["+" | "-"] some digit
```

For 3, see the table above. The fluent version is `Some(Digit) + CharIn("+-*/") + Some(Digit)`.

</details>

---

## 5. Scanning: `to`, `thru`, `not`, `ahead`

| Rule | Meaning |
|---|---|
| `to r` | skip forward until `r` would match, **without** consuming it |
| `thru r` | the same, but consume `r` as well |
| `not r` | succeeds if `r` does not match; consumes nothing |
| `ahead r` | succeeds if `r` matches; consumes nothing |
| `skip` | any one item |
| `end` | only at the end of the input |

`to end` is the idiom for "the rest of the input".

Be careful: `to` scans across newlines and everything else. You will meet that bug in lesson 15.

### Exercises

1. Match everything up to a `;`, then the rest. Accept `x=1 ; note` (partial is fine).
2. Match a line that does not start with `#`. Accept `x=1`, the empty string. Reject `# comment`.
3. Match any text with no comma in it. Accept `abc`. Reject `a,b`.
4. Match a word of letters, but only if it starts with `ab`, using `ahead`. Accept `abc`, `ab`. Reject `ba`, `a`.
5. Match a filename that has an extension. Accept `file.txt`, `a.b`. Reject `noext`, `x.` (an extension needs letters).

<details><summary>Solutions</summary>

```
to ";" to end
not "#" to end
some [not "," skip]
ahead "ab" some alpha
thru "." some alpha
```

Exercise 3 shows the standard "any character except X" pattern: `not x skip`.

</details>

---

## 6. Capturing

`copy name rule` stores the matched text in `result.Vars["name"]`. `set name rule` stores just the first item (a `char` in text mode).

```csharp
var r = Parser.Parse("name=joshua", Dialect.Read("copy k to \"=\" \"=\" copy v to end"));
r.Vars["k"];   // "name"
r.Vars["v"];   // "joshua"
```

### Exercises

1. Parse `key=value` into `k` and `v`.
2. Capture the first character of `xyz` into `first`, and accept the rest.
3. Parse a date like `2026-10-17` into `y`, `m` and `d`.

<details><summary>Solutions</summary>

```
copy k to "=" "=" copy v to end
set first skip to end
copy y 4 digit "-" copy m 2 digit "-" copy d 2 digit
```

</details>

---

## 7. Actions

Real parsers do something when they match. Put `(@name)` in a rule and bind `name` to a C# delegate. It runs at that point in the match, and `ctx.Vars` holds the captures so far. `ctx.Pos` is the current position.

```csharp
var total = 0;
var acts = new Dictionary<string, Action<Ctx>>
{
    ["add"] = c => total += int.Parse((string)c.Vars["n"]!),
};
var rule = Dialect.Read("any [copy n some digit (@add) opt \",\"]", acts);
Parser.Parse("1,2,30", rule);   // total is now 33
```

Paren code is never evaluated as text. Only `(@name)` is allowed, so a rule from an untrusted source cannot run code.

### Exercises

1. Sum the comma-separated integers in `1,2,30` as above.
2. Record the position at three places in `"a" "b"`: before `a`, between them, and after `b`. You should get `[0, 1, 2]`.

<details><summary>Solutions</summary>

1. The rule above.
2. `(@here) "a" (@here) "b" (@here)` with `["here"] = c => positions.Add(c.Pos)`.

</details>

---

## 8. Marks

`name:` saves the current position and `:name` jumps back to it. Rewinding lets you look at the same text twice.

```
m: "abc" :m "abc"      ; matches "abc" -- the input is only consumed once
```

### Exercises

1. Write the rule above. It accepts `abc` and rejects `abcabc`. Why does `abcabc` fail?
2. Write a rule that matches a word, rewinds, then captures the same word into `w`.

<details><summary>Solutions</summary>

1. After the jump back, the second `"abc"` leaves the position at 3, not 6, so a whole-input parse of `abcabc` has leftovers.
2. `here: some alpha :here copy w some alpha`

</details>

---

## 9. Recursion

A rule can refer to itself through the rule table. That is what lets parse handle nesting.

```csharp
var rules = Dialect.ReadRules(new Dictionary<string, string>
{
    ["group"] = "\"(\" any group \")\"",
});
Parser.Parse("(()())", Ref("group"), rules).Matched;   // true
```

Avoid **left recursion** (`expr = expr "+" digit`). The rule calls itself without consuming anything, so it recurses until the stack overflows.

### Exercises

1. Match balanced parentheses. Accept `()`, `(())`, `(()())`. Reject `(`, `())`, `)(`, the empty string.
2. Match nested number lists. Accept `[]`, `[1]`, `[1,22,3]`, `[1,[2,3],4]`, `[[],[[]]]`. Reject `[1,]`, `[1`, `[,1]`, `1`.

<details><summary>Solutions</summary>

```
group = "(" any group ")"

item = some digit | list
list = "[" opt [item any ["," item]] "]"
```

Parse starting from `Ref("group")` and `Ref("list")`. In exercise 2 an `item` is a number or another list, and `opt` allows the empty list.

</details>

---

## 10. Block mode

Parse does not care whether the input is text. Hand it a list of values and rules match items instead of characters.

```csharp
var input = new List<object> { new Word("add"), 1, 2 };
Parser.Parse(input, Dialect.Read("quote add integer! integer!")).Matched;   // true
```

- A string, char or number in a rule matches an equal item.
- `quote add` matches the `Word("add")` item.
- `integer! decimal! string! word! block!` match by type.
- `into r` goes inside a nested list and matches it with `r`; the whole nested list must be consumed.

### Exercises

1. Accept `[add 1 2]` and `[say "hi"]`, reject `[say 5]`.
2. Accept `[point [3 4]]` (a word, then a list of exactly two integers). Reject `[point [3]]` and `[point [3 "x"]]`.
3. Accept a list of any mix of integers and strings. Reject one that holds a decimal.

<details><summary>Solutions</summary>

```
[quote add integer! integer!] | [quote say string!]
quote point into [2 integer!]
some [integer! | string!]
```

</details>

---

## 11. Editing

`insert`, `change` and `remove` edit the input as it is parsed. Your original is never touched. The edited text (or list) comes back in `result.Output`.

| Rule | Does |
|---|---|
| `insert v` | puts `v` at the current position and steps past it |
| `change r v` | matches `r`, replaces it with `v`, steps past `v` |
| `remove r` | matches `r` and deletes it |

A value is a string, char or integer, or the name of a captured variable. The common shape is a loop that edits when it can and skips otherwise:

```
any [change "-" "+" | skip]       ; "a-b-c"  ->  "a+b+c"
```

Edits are not undone if the rule later backtracks, and marks saved before an edit are not adjusted.

### Exercises

1. Replace every tab with four spaces.
2. Remove all digits from `a1b22c` to get `abc`.
3. Put a space after every comma: `a,b,c` becomes `a, b, c`.
4. Wrap `hello` in brackets: `[hello]`.
5. In block mode, remove every string from `[1 "junk" 2]`.

<details><summary>Solutions</summary>

```
any [change tab "    " | skip]
any [remove some digit | skip]
any ["," insert " " | skip]
insert "[" to end insert "]"
any [remove string! | skip]
```

In 3, `insert` already steps past the new space, so the loop moves on correctly.

</details>

---

## 12. Collecting

`collect name rule` runs `rule` and stores everything it `keep`s in a list at `Vars["name"]`. `keep r` matches `r` and adds the match.

```
collect words any [keep some alpha | skip]       ; "hello big world" -> ["hello", "big", "world"]
```

In text mode you collect strings. In block mode a one-item match keeps the item itself, and a longer match keeps a list. A `keep` made in a branch that later fails is dropped, so alternatives are safe. `keep pick r` keeps only the first item of the match, and `keep (@name)` keeps a value computed by a C# function you pass in the `values` table.

### Exercises

1. Collect the words of `hello big  world` (two spaces in the middle).
2. Collect just the integers of `[1 "a" 2]`.
3. Split `a,bb,,c` on commas into `["a", "bb", "", "c"]`. Empty cells count.

<details><summary>Solutions</summary>

```
collect words any [keep some alpha | skip]
collect n any [keep integer! | skip]
collect cells [keep any [not "," skip] any ["," keep any [not "," skip]]]
```

Exercise 3 keeps a cell even when it is empty, because `keep any [...]` still matches.

</details>

---

## 13. `break` and `reject`

Both leave the enclosing `some`, `any` or `opt`.

- `break` leaves it as a **success**. Whatever matched so far counts.
- `reject` leaves it as a **failure**, as if the loop never matched.

Outside any loop, `reject` is just `fail`.

```
any ["," break | alpha]       ; stops after the first comma
```

### Exercises

1. For `ab,cd`: consume letters up to and including the first comma, then capture the rest into `rest`. It should hold `cd`.
2. Accept words of letters that contain no `q`, using `reject`. Accept `abc`, `xyz`. Reject `abq`, `q`.

<details><summary>Solutions</summary>

```
any ["," break | alpha] copy rest to end
some ["q" reject | alpha]
```

</details>

---

## 14. Case-insensitive matching

Matching is case-sensitive unless you ask otherwise.

```csharp
Parser.Parse("SELECT", Dialect.Read("\"select\""), ignoreCase: true).Matched;   // true
```

It covers literal strings, chars and words, including inside `into`. Character classes like `upper` stay case-sensitive, because their meaning is the case.

### Exercises

1. Accept `select`, `SELECT`, `SeLeCt`; reject `selec`.
2. Predict: does `upper` match `a` with `ignoreCase: true`? (No.)

<details><summary>Solutions</summary>

1. `"select"` with `ignoreCase: true`.
2. No. Only literals ignore case.

</details>

---

## 15. Capstone: an INI parser

Put everything together. Parse this and report each key with its section:

```ini
[core]
name=puter

[extra]
  x = 1
y=2
```

Expected: `core.name=puter`, `extra.x=1`, `extra.y=2`.

The grammar, line by line:

- `ws`: optional spaces and tabs.
- `section`: `[`, the name up to `]`, then `]`, and the `section` action remembers the name.
- `pair`: a key, `=`, a value, then the `pair` action records it.
- `line`: leading whitespace, then a section, a pair or nothing (a blank line), then trailing whitespace.
- `file`: a line, then any number of newline + line.

### Exercise

Write the rule table and the two actions, then check the three results. **Hint:** your first attempt at `pair` will probably use `copy k to "="`. Try it on the input above and see what the blank line does to the next section. (`to` happily scans across newlines.) The fix is to say what a key is made of instead of scanning for what ends it.

<details><summary>Solution</summary>

```csharp
var entries = new List<string>();
var section = "";
var acts = new Dictionary<string, Action<Ctx>>
{
    ["section"] = c => section = (string)c.Vars["sec"]!,
    ["pair"]    = c => entries.Add($"{section}.{c.Vars["k"]}={c.Vars["v"]}"),
};
var rules = Dialect.ReadRules(new Dictionary<string, string>
{
    ["ws"]      = "any [\" \" | tab]",
    ["section"] = "\"[\" copy sec to \"]\" \"]\" (@section)",
    ["pair"]    = "copy k some [not [space | \"=\"] skip] ws \"=\" ws copy v any [not newline skip] (@pair)",
    ["line"]    = "ws [section | pair | none] ws",
    ["file"]    = "line any [newline line]",
}, acts);

Parser.Parse(ini, Ref("file"), rules);
```

The key is `some [not [space | "="] skip]`: any characters up to whitespace or `=`. The value is `any [not newline skip]`: everything to the end of the line.

</details>

### Where next

- Add `;` comment lines to the INI grammar.
- Turn the parser into one that rewrites a file: `change` the value of one key and read `Output`.
- Read the keyword table in the [README](../README.md) for anything this tutorial skipped, and `samples/Samples/Program.cs` for runnable examples.
