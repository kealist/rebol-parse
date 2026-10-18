using static RebolParse.P;

namespace RebolParse.Tests;

/// <summary>keep pick, keep (value), and if (condition).</summary>
public class MoreFeatureTests
{
    private static readonly IReadOnlyDictionary<string, Func<Ctx, object?>> Fns = new Dictionary<string, Func<Ctx, object?>>
    {
        ["pos"] = c => c.Pos,
        ["nothing"] = _ => null,
        ["always"] = _ => true,
        ["never"] = _ => false,
        ["truthy"] = _ => "yes",
        ["small"] = c => c.Vars.TryGetValue("n", out var n) && int.Parse((string)n!) < 10,
    };

    private static ParseResult Run(string input, string text, bool partial = false) =>
        Parser.Parse(input, Dialect.Read(text, values: Fns), partial: partial);

    private static object[] Kept(ParseResult r, string name = "out") =>
        [.. Assert.IsAssignableFrom<IReadOnlyList<object>>(r.Vars[name])];

    // ---- keep pick ----

    [Fact]
    public void Keep_pick_keeps_only_the_first_item()
    {
        var r = Run("ab12", "collect out any [keep pick some alpha | keep pick some digit]");
        Assert.True(r.Matched);
        Assert.Equal(new object[] { 'a', '1' }, Kept(r));
    }

    [Fact]
    public void Keep_pick_in_block_mode_and_on_empty_matches()
    {
        var r = Parser.Parse(new List<object> { 1, 2, "x" }, Dialect.Read("collect out some [keep pick some integer! | keep pick string!]"));
        Assert.Equal(new object[] { 1, "x" }, Kept(r));
        Assert.Empty(Kept(Run("", "collect out keep pick none")));
    }

    [Fact]
    public void Keep_pick_fluent_and_failure_rules()
    {
        Assert.Equal(new object[] { 'x' }, Kept(Parser.Parse("xyz", Collect("out", KeepPick(Some(Alpha))))));
        Assert.Empty(Kept(Run("a", "collect out [keep pick digit | skip]")));
        Assert.Empty(Kept(Run("ab", "collect out [[keep pick \"a\" \"z\"] | skip skip]")));
        Assert.Throws<InvalidOperationException>(() => Run("a", "keep pick skip"));
    }

    // ---- keep (value) ----

    [Fact]
    public void Keep_value_adds_a_computed_item_without_consuming_input()
    {
        var r = Run("ab", "collect out [\"a\" keep (@pos) \"b\" keep (@pos)]");
        Assert.True(r.Matched);
        Assert.Equal(new object[] { 1, 2 }, Kept(r));
    }

    [Fact]
    public void Keep_value_of_null_keeps_nothing()
    {
        Assert.Empty(Kept(Run("a", "collect out [keep (@nothing) \"a\"]")));
    }

    [Fact]
    public void Keep_value_is_dropped_when_its_branch_fails()
    {
        var r = Run("b", "collect out [[keep (@pos) \"z\"] | \"b\"]");
        Assert.Empty(Kept(r));
    }

    [Fact]
    public void Keep_value_fluent_and_outside_collect()
    {
        var r = Parser.Parse("a", Collect("out", Keep(c => "v") + "a"));
        Assert.Equal(new object[] { "v" }, Kept(r));
        Assert.Throws<InvalidOperationException>(() => Run("a", "keep (@pos) \"a\""));
    }

    // ---- if ----

    [Fact]
    public void If_gates_a_branch_on_a_condition()
    {
        Assert.True(Run("a", "if (@always) \"a\"").Matched);
        Assert.False(Run("a", "if (@never) \"a\"").Matched);
        Assert.True(Run("a", "[if (@never) \"b\"] | \"a\"").Matched);
    }

    [Fact]
    public void If_treats_null_as_false_and_other_values_as_true()
    {
        Assert.False(Run("a", "if (@nothing) \"a\"").Matched);
        Assert.True(Run("a", "if (@truthy) \"a\"").Matched);
    }

    [Fact]
    public void If_can_read_captures_made_earlier()
    {
        var rule = Dialect.Read("copy n some digit if (@small)", values: Fns);
        Assert.True(Parser.Parse("7", rule).Matched);
        Assert.False(Parser.Parse("70", rule).Matched);
    }

    [Fact]
    public void If_consumes_nothing_and_sees_the_current_position()
    {
        var seen = -1;
        var r = Parser.Parse("abc", Skip + Skip + If(c => { seen = c.Pos; return true; }) + "c");
        Assert.True(r.Matched);
        Assert.Equal(2, seen);
    }

    // ---- reader errors and bindings ----

    [Theory]
    [InlineData("keep pick", "needs a rule after it")]
    [InlineData("if", "'if' needs a condition")]
    [InlineData("if \"a\"", "'if' needs a condition")]
    [InlineData("keep (x)", "bind a named function")]
    [InlineData("if (x)", "bind a named function")]
    public void Malformed_new_keywords_throw(string text, string fragment)
    {
        var ex = Assert.Throws<FormatException>(() => Dialect.Read(text, values: Fns));
        Assert.Contains(fragment, ex.Message);
    }

    [Fact]
    public void Unregistered_value_function_throws()
    {
        Assert.Throws<KeyNotFoundException>(() => Dialect.Read("if (@nope)"));
        Assert.Contains("(@nope)", Assert.Throws<KeyNotFoundException>(() => Dialect.Read("keep (@nope)", values: Fns)).Message);
    }

    [Fact]
    public void ReadRules_passes_value_functions_through()
    {
        var rules = Dialect.ReadRules(new Dictionary<string, string> { ["r"] = "collect out [keep (@pos)]" }, values: Fns);
        Assert.Equal(new object[] { 0 }, Kept(Parser.Parse("", Ref("r"), rules)));
    }

    [Fact]
    public void Datatype_scanning_with_to_and_thru_in_block_mode()
    {
        var input = new List<object> { "a", 5, new Word("w") };
        Assert.True(Parser.Parse(input, Dialect.Read("to integer! integer! word!")).Matched);
        Assert.True(Parser.Parse(input, Dialect.Read("thru string! integer! word!")).Matched);
        Assert.True(Parser.Parse(input, Dialect.Read("to word! word!")).Matched);
        Assert.False(Parser.Parse(input, Dialect.Read("to decimal! skip")).Matched);
    }
}
