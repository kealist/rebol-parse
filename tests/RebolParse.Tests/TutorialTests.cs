using static RebolParse.P;

namespace RebolParse.Tests;

/// <summary>Every exercise solution in docs/Tutorial.md, checked against the inputs the tutorial lists.</summary>
public class TutorialTests
{
    private static Rule R(string text, IReadOnlyDictionary<string, Action<Ctx>>? acts = null) => Dialect.Read(text, acts);

    private static void Check(string text, string[] accepts, string[] rejects, bool ignoreCase = false)
    {
        var rule = R(text);
        foreach (var s in accepts) Assert.True(Parser.Parse(s, rule, ignoreCase: ignoreCase).Matched, $"should accept '{s}'");
        foreach (var s in rejects) Assert.False(Parser.Parse(s, rule, ignoreCase: ignoreCase).Matched, $"should reject '{s}'");
    }

    private static List<object> L(params object[] items) => [.. items];

    // ---- 1 First parse ----

    [Fact]
    public void Ch1()
    {
        Check(""" "hello" """, ["hello"], ["hello!", "hell", "Hello"]);
        Assert.True(Parser.Parse("hello world", R(""" "hello" """), partial: true).Matched);
    }

    // ---- 2 Sequence, choice, repeat ----

    [Fact]
    public void Ch2()
    {
        Check("some digit opt alpha", ["7", "42x", "007"], ["x", "4x2", ""]);
        Check(""" "yes" | "no" | "maybe" """, ["yes", "no", "maybe"], ["ye", "nope", ""]);
        Check(""" 3 digit "-" 4 digit """, ["555-1234"], ["55-1234", "555-123", "5551234"]);
        Check(""" "#" 6 [digit | alpha] """, ["#00ff00", "#abcdef"], ["#abc", "00ff00", "#00ff000"]);
    }

    // ---- 3 Fluent ----

    [Fact]
    public void Ch3()
    {
        var fluent = Choice("a", Seq("b", "c"));
        foreach (var s in new[] { "a", "bc" }) Assert.True(Parser.Parse(s, fluent).Matched);
        foreach (var s in new[] { "b", "ab", "c" }) Assert.False(Parser.Parse(s, fluent).Matched);

        var rule = Some(Digit) + "." + Some(Digit);
        Assert.True(Parser.Parse("3.14", rule).Matched);
        // the pitfall: two plain strings concatenate before they ever become rules
        Assert.Equal("bc", "b" + "c");
        Assert.IsType<Seq>((Rule)"b" + "c");

        var third = Rep(1, 3, Digit) + Opt("." + Some(Digit));
        Check("1 3 digit opt [\".\" some digit]", ["7", "123", "12.5"], ["", "1234", "1."]);
        foreach (var s in new[] { "7", "123", "12.5" }) Assert.True(Parser.Parse(s, third).Matched);
        foreach (var s in new[] { "", "1234", "1." }) Assert.False(Parser.Parse(s, third).Matched);
    }

    // ---- 4 Rule tables and character classes ----

    [Fact]
    public void Ch4()
    {
        Check("alpha any alnum", ["x", "abc9", "R2D2"], ["9a", "", "a-b"]);
        Check(""" opt ["+" | "-"] some digit """, ["5", "+5", "-120"], ["+", "5-", ""]);

        var rules = Dialect.ReadRules(new Dictionary<string, string>
        {
            ["op"] = """ "+" | "-" | "*" | "/" """,
            ["calc"] = "some digit op some digit",
        });
        Assert.True(Parser.Parse("12*3", Ref("calc"), rules).Matched);
        Assert.False(Parser.Parse("12^3", Ref("calc"), rules).Matched);
        var fluent = Some(Digit) + CharIn("+-*/") + Some(Digit);
        Assert.True(Parser.Parse("12*3", fluent).Matched);

        Check("\"#\" 6 charset \"0-9a-fA-F\"", ["#00ff7A"], ["#00ff7G", "#00ff7", "00ff7A"]);
    }

    // ---- 5 to / thru / not / ahead ----

    [Fact]
    public void Ch5()
    {
        var quoted = Parser.Parse("\"hi\"", R("""#"^"" copy s to #"^"" skip"""));
        Assert.True(quoted.Matched);
        Assert.Equal("hi", quoted.Vars["s"]);

        var code = Parser.Parse("x=1 ; note", R("""copy code to ";" to end"""));
        Assert.Equal("x=1 ", code.Vars["code"]);

        Check("""not "#" to end""", ["x=1", ""], ["# comment"]);
        Check("""some [not "," skip]""", ["abc"], ["a,b"]);
        Check("""ahead "ab" some alpha""", ["abc", "ab"], ["ba", "a"]);
        Check("""thru "." some alpha""", ["file.txt", "a.b"], ["noext", "x."]);
    }

    // ---- 6 Capturing ----

    [Fact]
    public void Ch6()
    {
        var kv = Parser.Parse("name=joshua", R("""copy k to "=" "=" copy v to end"""));
        Assert.Equal("name", kv.Vars["k"]);
        Assert.Equal("joshua", kv.Vars["v"]);

        var first = Parser.Parse("xyz", R("set first skip to end"));
        Assert.Equal('x', first.Vars["first"]);

        var date = Parser.Parse("2026-10-17", R("""copy y 4 digit "-" copy m 2 digit "-" copy d 2 digit"""));
        Assert.True(date.Matched);
        Assert.Equal(("2026", "10", "17"), ((string)date.Vars["y"]!, (string)date.Vars["m"]!, (string)date.Vars["d"]!));
    }

    // ---- 7 Actions ----

    [Fact]
    public void Ch7()
    {
        var total = 0;
        var acts = new Dictionary<string, Action<Ctx>> { ["add"] = c => total += int.Parse((string)c.Vars["n"]!) };
        var rule = Dialect.Read("""any [copy n some digit (@add) opt ","]""", acts);
        Assert.True(Parser.Parse("1,2,30", rule).Matched);
        Assert.Equal(33, total);

        var positions = new List<int>();
        var track = new Dictionary<string, Action<Ctx>> { ["here"] = c => positions.Add(c.Pos) };
        Assert.True(Parser.Parse("ab", Dialect.Read("""(@here) "a" (@here) "b" (@here)""", track)).Matched);
        Assert.Equal([0, 1, 2], positions);
    }

    // ---- 8 Marks ----

    [Fact]
    public void Ch8()
    {
        // match "abc", rewind to the mark, match it again: the input is consumed only once
        Assert.True(Parser.Parse("abc", R("""m: "abc" :m "abc" """)).Matched);
        Assert.False(Parser.Parse("abcabc", R("""m: "abc" :m "abc" """)).Matched);
        // look at the next word without consuming it, then really consume it
        Assert.True(Parser.Parse("hello", R("here: some alpha :here copy w some alpha")).Matched);
    }

    // ---- 9 Recursion ----

    [Fact]
    public void Ch9()
    {
        var parens = Dialect.ReadRules(new Dictionary<string, string>
        {
            ["group"] = """ "(" any group ")" """,
        });
        foreach (var s in new[] { "()", "(())", "(()())" })
            Assert.True(Parser.Parse(s, Ref("group"), parens).Matched, s);
        foreach (var s in new[] { "(", "())", ")(", "" })
            Assert.False(Parser.Parse(s, Ref("group"), parens).Matched, s);

        var lists = Dialect.ReadRules(new Dictionary<string, string>
        {
            ["item"] = "some digit | list",
            ["list"] = """ "[" opt [item any ["," item]] "]" """,
        });
        foreach (var s in new[] { "[]", "[1]", "[1,22,3]", "[1,[2,3],4]", "[[],[[]]]" })
            Assert.True(Parser.Parse(s, Ref("list"), lists).Matched, s);
        foreach (var s in new[] { "[1,]", "[1", "[,1]", "1" })
            Assert.False(Parser.Parse(s, Ref("list"), lists).Matched, s);
    }

    [Fact]
    public void Ch9_grammar_text_and_ch3_chaining()
    {
        var rules = Dialect.ReadGrammar("""
            item = some digit | list
            list = "[" opt [item any ["," item]] "]"
            """);
        Assert.True(Parser.Parse("[1,[2,3]]", Ref("list"), rules).Matched);
        Assert.False(Parser.Parse("[1,]", Ref("list"), rules).Matched);

        var r = Parser.Parse("abc", Alpha.Some().Copy("word"));
        Assert.Equal("abc", r.Vars["word"]);
        Assert.True(Parser.Parse("123", Digit.Times(2, 4)).Matched);
        Assert.False(Parser.Parse("1", Digit.Times(2, 4)).Matched);
    }

    // ---- 10 Block mode ----

    [Fact]
    public void Ch10()
    {
        var cmd = R("""[quote add integer! integer!] | [quote say string!]""");
        Assert.True(Parser.Parse(L(new Word("add"), 1, 2), cmd).Matched);
        Assert.True(Parser.Parse(L(new Word("say"), "hi"), cmd).Matched);
        Assert.False(Parser.Parse(L(new Word("say"), 5), cmd).Matched);

        var point = R("quote point into [2 integer!]");
        Assert.True(Parser.Parse(L(new Word("point"), L(3, 4)), point).Matched);
        Assert.False(Parser.Parse(L(new Word("point"), L(3)), point).Matched);
        Assert.False(Parser.Parse(L(new Word("point"), L(3, "x")), point).Matched);

        var sum = R("some [integer! | string!]");
        Assert.True(Parser.Parse(L(1, "a", 2), sum).Matched);
        Assert.False(Parser.Parse(L(1, 2.5), sum).Matched);
    }

    // ---- 11 Editing ----

    [Fact]
    public void Ch11()
    {
        Assert.Equal("a    b", Parser.Parse("a\tb", R("""any [change tab "    " | skip]""")).Output);
        Assert.Equal("abc", Parser.Parse("a1b22c", R("any [remove some digit | skip]")).Output);
        Assert.Equal("a, b, c", Parser.Parse("a,b,c", R("""any ["," insert " " | skip]""")).Output);
        Assert.Equal("[hello]", Parser.Parse("hello", R("""insert "[" to end insert "]" """)).Output);

        var r = Parser.Parse(L(1, "junk", 2), R("any [remove string! | skip]"));
        Assert.Equal(new object[] { 1, 2 }, Assert.IsAssignableFrom<IReadOnlyList<object>>(r.Output));
    }

    // ---- 12 Collect ----

    [Fact]
    public void Ch12()
    {
        var words = Parser.Parse("hello big  world", R("collect words any [keep some alpha | skip]"));
        Assert.Equal(new object[] { "hello", "big", "world" }, Assert.IsAssignableFrom<IReadOnlyList<object>>(words.Vars["words"]));

        var nums = Parser.Parse(L(1, "a", 2), R("collect n any [keep integer! | skip]"));
        Assert.Equal(new object[] { 1, 2 }, Assert.IsAssignableFrom<IReadOnlyList<object>>(nums.Vars["n"]));

        var csv = Parser.Parse("a,bb,,c", R("""collect cells [keep any [not "," skip] any ["," keep any [not "," skip]]]"""));
        Assert.Equal(new object[] { "a", "bb", "", "c" }, Assert.IsAssignableFrom<IReadOnlyList<object>>(csv.Vars["cells"]));
    }

    // ---- 13 break / reject ----

    [Fact]
    public void Ch13()
    {
        var br = Parser.Parse("ab,cd", R("""any ["," break | alpha] copy rest to end"""));
        Assert.True(br.Matched);
        Assert.Equal("cd", br.Vars["rest"]);

        Check("""some ["q" reject | alpha]""", ["abc", "xyz"], ["abq", "q"]);
        Assert.True(Parser.Parse("abq", R("""[some ["q" reject | alpha]] | "abq" """)).Matched);
    }

    // ---- 14 Case ----

    [Fact]
    public void Ch14()
    {
        Check(""" "select" """, ["select", "SELECT", "SeLeCt"], ["selec"], ignoreCase: true);
        Assert.False(Parser.Parse("SELECT", R(""" "select" """)).Matched);
        Check("""upper""", ["A"], ["a"], ignoreCase: true);
    }

    // ---- 15 Capstone: INI ----

    [Fact]
    public void Ch15_ini_file()
    {
        var entries = new List<string>();
        var section = "";
        var acts = new Dictionary<string, Action<Ctx>>
        {
            ["section"] = c => section = (string)c.Vars["sec"]!,
            ["pair"] = c => entries.Add($"{section}.{c.Vars["k"]}={c.Vars["v"]}"),
        };
        var rules = Dialect.ReadRules(new Dictionary<string, string>
        {
            ["ws"] = """ any [" " | tab] """,
            ["section"] = """ "[" copy sec to "]" "]" (@section) """,
            ["pair"] = """ copy k some [not [space | "="] skip] ws "=" ws copy v any [not newline skip] (@pair) """,
            ["line"] = "ws [section | pair | none] ws",
            ["file"] = "line any [newline line]",
        }, acts);

        var ini = "[core]\nname=puter\n\n[extra]\n  x = 1\ny=2";
        Assert.True(Parser.Parse(ini, Ref("file"), rules).Matched);
        Assert.Equal(["core.name=puter", "extra.x=1", "extra.y=2"], entries);
    }
}
