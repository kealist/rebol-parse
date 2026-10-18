using System.Diagnostics;
using System.Text.RegularExpressions;
using RebolParse;

namespace Samples;

/// <summary>
/// Editing text as it is parsed: <c>remove</c> and <c>change</c> rewrite the input in place. This cleaner strips
/// comments from C-style source without touching string literals (which a naive regex gets wrong), trims
/// trailing whitespace and squeezes blank lines.
/// </summary>
public sealed class SourceCleaner : IDemo
{
    public string Name => "clean";
    public string Title => "Source cleaner: remove and change on real text, fast on big input";

    private static readonly Rule StripComments = Dialect.Read("""
        any [
            "^"" any ["\" skip | not "^"" skip] "^""      ; a string literal: keep it whole
          | remove ["//" to ["^/" | end]]                  ; line comment
          | remove ["/*" thru "*/"]                        ; block comment
          | skip
        ]
        """).Compiled();

    private static readonly Rule Tidy = Dialect.Read("""
        any [
            remove [some [" " | tab] ahead ["^/" | end]]   ; trailing whitespace
          | change ["^/" "^/" some "^/"] "^/^/"            ; three or more newlines become one blank line
          | skip
        ]
        """).Compiled();

    public static string Clean(string source)
    {
        var stripped = Parser.Parse(source, StripComments).Output as string ?? source;
        return Parser.Parse(stripped, Tidy).Output as string ?? stripped;
    }

    private const string Dirty = "int x = 1; // the count   \n" +
        "string url = \"http://example.com // not a comment\";\t\n" +
        "/* block\n   comment */ int y = 2;\n" +
        "\n\n\n\n" +
        "string s = \"a \\\" /* still a string */ b\"; // done\n" +
        "int z = 3;   \n";

    private const string Expected = "int x = 1;\n" +
        "string url = \"http://example.com // not a comment\";\n" +
        " int y = 2;\n" +
        "\n" +
        "string s = \"a \\\" /* still a string */ b\";\n" +
        "int z = 3;\n";

    public void Run(Report r)
    {
        var cleaned = Clean(Dirty);
        r.Say(cleaned);
        r.Expect("comments gone, strings intact, whitespace tidy", cleaned, Expected);
        r.Expect("already clean text is left alone", Clean(Expected), Expected);
        r.Expect("the input string is never modified", Dirty.Contains("// the count"));
        r.Expect("an unterminated block comment is left as written", Clean("a /* b"), "a /* b");

        // The same job on a big input. Every removal widens a gap in an edit buffer instead of shifting the text,
        // so this is linear; with a shifting buffer 100,000 edits over several MB would take seconds.
        var chunk = Dirty;
        var big = string.Concat(Enumerable.Repeat(chunk, 20000));
        var sw = Stopwatch.StartNew();
        var result = Clean(big);
        var parse = sw.Elapsed;
        r.Expect("a multi-megabyte source cleans to the cleaned chunk repeated", result == string.Concat(Enumerable.Repeat(Expected, 20000)));

        sw.Restart();
        var viaRegex = Regex.Replace(big, @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);
        var regex = sw.Elapsed;
#if DEBUG
        const string build = " (a Debug build: run with -c Release for real timings)";
#else
        const string build = "";
#endif
        r.Say($"{big.Length / 1024} KB cleaned in {parse.TotalMilliseconds:F0} ms; one regex pass that also breaks strings took {regex.TotalMilliseconds:F0} ms{build}");
        r.Expect("the regex shortcut mangles the string literals", viaRegex.Contains("not a comment") is false);
    }
}
