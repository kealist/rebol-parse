using static RebolParse.P;

namespace RebolParse.Tests;

/// <summary>
/// Random rules full of edits and backtracking, run on text (compiled nodes with a gap buffer) and on a block
/// of one-character strings (the interpreter, which edits a plain list). Both must agree on everything.
/// </summary>
public class EditParityTests
{
    // No bare inserts: an insert that is followed by a failure stays in the text, so a loop or a scan around one can
    // grow the text as fast as it advances and never end. Change still exercises the insert path and never grows the text.
    private static Rule Gen(Random rng, int depth)
    {
        if (depth <= 0 || rng.Next(5) == 0)
            return rng.Next(6) switch
            {
                0 => Lit("a"),
                1 => Lit("b"),
                2 => Lit("c"),
                3 => Skip,
                4 => Skip,
                _ => Lit("a"),
            };
        var d = depth - 1;
        return rng.Next(17) switch
        {
            0 => Seq(Gen(rng, d), Gen(rng, d)),
            1 => Seq(Gen(rng, d), Gen(rng, d), Gen(rng, d)),
            2 => Choice(Gen(rng, d), Gen(rng, d)),
            3 => Choice(Gen(rng, d), Gen(rng, d), Gen(rng, d)),
            4 => Any(Gen(rng, d)),
            5 => Some(Gen(rng, d)),
            6 => Opt(Gen(rng, d)),
            7 => Remove(Gen(rng, d)),
            8 => Remove(Gen(rng, d)),
            9 => Change(Seq(Skip, Gen(rng, d)), "X"),
            10 => Not(Gen(rng, d)),
            11 => Ahead(Gen(rng, d)),
            12 => Copy("v", Gen(rng, d)),
            13 => rng.Next(2) == 0 ? To(Gen(rng, d)) : Thru(Gen(rng, d)),
            14 => Seq(Gen(rng, d), Gen(rng, d)),
            15 => Any(Choice(Seq(Gen(rng, d), Break), Gen(rng, d))),
            _ => Seq(Gen(rng, d), End),
        };
    }

    private static string Show(Rule r) => r switch
    {
        Lit l => $"\"{l.Value}\"",
        Seq q => "[" + string.Join(" ", q.Items.Select(Show)) + "]",
        Alt a => "(" + string.Join(" | ", a.Items.Select(Show)) + ")",
        SkipRule => "skip",
        EndRule => "end",
        BreakRule => "break",
        Repeat p => $"rep{p.Min},{p.Max}({Show(p.Body)})",
        Insert => "insert",
        Change c => $"change({Show(c.Body)})",
        Remove m => $"remove({Show(m.Body)})",
        Not n => $"not({Show(n.Body)})",
        Ahead h => $"ahead({Show(h.Body)})",
        Copy c => $"copy({Show(c.Body)})",
        To t => $"to({Show(t.Body)})",
        Thru t => $"thru({Show(t.Body)})",
        Mark => "mark",
        Seek => "seek",
        _ => r.GetType().Name,
    };

    private static (bool Matched, int Pos, string Output, string? Copy, string? Error) Run(Rule rule, string input, bool partial, bool asBlock)
    {
        try
        {
            ParseResult r;
            if (asBlock) r = Parser.Parse(input.Select(c => (object)c.ToString()).ToList(), rule, partial: partial);
            else r = Parser.Parse(input, rule, partial: partial);
            var output = r.Output is string s ? s : string.Concat(((IReadOnlyList<object>)r.Output!).Select(x => x.ToString()));
            var copy = r.Vars.TryGetValue("v", out var v)
                ? v is string vs ? vs : string.Concat(((IEnumerable<object>)v!).Select(x => x.ToString()))
                : null;
            return (r.Matched, r.Position, output, copy, null);
        }
        catch (Exception ex) { return (false, -1, "", null, ex.GetType().Name); }
    }

    // Each of these rewinds the position to before an edit the same rule just made, which the gap buffer must survive.
    [Theory]
    [InlineData("to [skip remove \"b\"] \"a\" \"c\"", "abc", "ac")]
    [InlineData("ahead [skip remove \"b\"] \"a\" \"c\"", "abc", "ac")]
    [InlineData("not [skip remove \"x\"] skip skip skip", "abc", "abc")]
    [InlineData("m: skip remove \"b\" :m \"a\" \"c\"", "abc", "ac")]
    [InlineData("[\"a\" remove \"b\" \"z\" | \"a\" \"c\"]", "abc", "ac")]
    [InlineData("copy x [skip remove \"b\" skip]", "abc", "ac")]
    [InlineData("any [remove some digit | skip]", "a1b22c333", "abc")]
    [InlineData("any [change some digit \"#\" | skip]", "a1b22c333", "a#b#c#")]
    [InlineData("any [remove \"a\" skip change \"c\" \"CC\" | skip]", "abcabc", "bCCbCC")]
    public void Rewinding_past_an_edit(string rule, string input, string expected)
    {
        var r = Parser.Parse(input, Dialect.Read(rule));
        Assert.True(r.Matched, "the rule should still match the edited text");
        Assert.Equal(expected, r.Output);
    }

    [Fact]
    public void Captures_that_span_an_edit_read_the_edited_text()
    {
        var edit = "[skip remove \"b\" skip]";
        Assert.Equal("ac", Parser.Parse("abc", Dialect.Read($"copy x {edit}")).Vars["x"]);
        Assert.Equal('a', Parser.Parse("abc", Dialect.Read($"set x {edit}")).Vars["x"]);
        var kept = (List<object>)Parser.Parse("abc", Dialect.Read($"collect o keep {edit}")).Vars["o"]!;
        Assert.Equal(["ac"], kept);
        var picked = (List<object>)Parser.Parse("abc", Dialect.Read($"collect o keep pick {edit}")).Vars["o"]!;
        Assert.Equal(['a'], picked);
    }

    [Fact]
    public void Random_edit_rules_agree_between_text_and_block_engines()
    {
        var rng = new Random(20261018);
        var checkedCases = 0;
        for (var i = 0; i < 6000; i++)
        {
            // A Seek inside a loop can undo a loop's progress while an insert grows the text, which never ends,
            // so marks only wrap the whole rule.
            var rule = rng.Next(3) == 0 ? Seq(Mark("m"), Gen(rng, 3), Seek("m"), Gen(rng, 3)) : Gen(rng, 4);
            var len = rng.Next(0, 13);
            var input = new string(Enumerable.Range(0, len).Select(_ => "abc"[rng.Next(3)]).ToArray());
            var partial = rng.Next(2) == 0;
            var text = Run(rule, input, partial, asBlock: false);
            var block = Run(rule, input, partial, asBlock: true);
            Assert.True(text == block, $"input '{input}' partial={partial}\n rule {Show(rule)}\n text  {text}\n block {block}");
            checkedCases++;
        }
        Assert.Equal(6000, checkedCases);
    }
}
