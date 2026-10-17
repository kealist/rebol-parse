using static RebolParse.P;

namespace RebolParse.Tests;

/// <summary>reject, insert, change, remove, collect/keep and case-insensitive matching.</summary>
public class FeatureTests
{
    private static ParseResult Run(string input, string text, bool partial = false, bool ignoreCase = false,
        IReadOnlyDictionary<string, Action<Ctx>>? acts = null) =>
        Parser.Parse(input, Dialect.Read(text, acts), partial: partial, ignoreCase: ignoreCase);

    // ---- case-insensitive mode ----

    [Fact]
    public void Matching_is_case_sensitive_by_default()
    {
        Assert.False(Run("HELLO", "\"hello\"").Matched);
        Assert.False(Run("A", "#\"a\"").Matched);
    }

    [Fact]
    public void Ignore_case_applies_to_strings_chars_to_and_thru()
    {
        Assert.True(Run("HeLLo", "\"hello\"", ignoreCase: true).Matched);
        Assert.True(Run("A", "#\"a\"", ignoreCase: true).Matched);
        Assert.True(Run("KEY=v", "to \"=\" \"=\" \"V\"", ignoreCase: true).Matched);
        Assert.True(Run("xxABCyy", "thru \"abc\" \"yy\"", ignoreCase: true).Matched);
        Assert.False(Run("HELLO", "\"help\"", ignoreCase: true).Matched);
        Assert.False(Run("HEL", "\"hello\"", ignoreCase: true).Matched);
    }

    [Fact]
    public void Ignore_case_in_block_mode_covers_strings_chars_and_words_but_not_numbers()
    {
        var input = new List<object> { "Add", 'X', new Word("Foo"), 3 };
        Assert.True(Parser.Parse(input, Dialect.Read("\"add\" #\"x\" quote foo quote 3"), ignoreCase: true).Matched);
        Assert.False(Parser.Parse(input, Dialect.Read("\"add\" #\"x\" quote foo quote 3")).Matched);
        Assert.False(Parser.Parse(input, Dialect.Read("\"add\" #\"y\" quote foo quote 3"), ignoreCase: true).Matched);
    }

    [Fact]
    public void Ignore_case_reaches_nested_blocks()
    {
        var input = new List<object> { new List<object> { "ONE" } };
        Assert.True(Parser.Parse(input, Dialect.Read("into [\"one\"]"), ignoreCase: true).Matched);
        Assert.False(Parser.Parse(input, Dialect.Read("into [\"one\"]")).Matched);
    }

    [Fact]
    public void Charsets_stay_case_sensitive()
    {
        Assert.False(Run("a", "upper", ignoreCase: true).Matched);
    }

    // ---- reject ----

    [Fact]
    public void Reject_makes_the_enclosing_loop_fail_and_restores_position()
    {
        var r = Run("aab", "some [\"a\" | \"b\" reject]", partial: true);
        Assert.False(r.Matched);
        Assert.Equal(0, r.Position);
    }

    [Fact]
    public void Reject_lets_an_outer_alternative_run()
    {
        Assert.True(Run("aab", "[some [\"a\" | \"b\" reject]] | \"aab\"").Matched);
    }

    [Fact]
    public void Reject_outside_a_loop_is_plain_failure()
    {
        Assert.False(Run("a", "reject").Matched);
        Assert.True(Run("a", "reject | \"a\"").Matched);
    }

    [Fact]
    public void Reject_inside_a_loop_that_still_meets_its_minimum_still_fails()
    {
        Assert.False(Run("ab", "any [\"a\" \"b\" reject]", partial: true).Matched);
    }

    [Fact]
    public void Reject_from_an_abandoned_branch_does_not_leak()
    {
        Assert.True(Run("aaa", "some [reject \"z\" | \"a\"]").Matched);
    }

    [Fact]
    public void Reject_fluent_builder()
    {
        Assert.False(Parser.Parse("b", Some((Rule)"a" | ("b" + Reject)), partial: true).Matched);
    }

    [Fact]
    public void Reject_after_a_loop_has_finished_is_plain_failure()
    {
        Assert.False(Run("a", "some \"a\" reject").Matched);
        Assert.False(Run("aa", "some \"a\" any \"b\" reject", partial: true).Matched);
    }

    // ---- insert ----

    [Fact]
    public void Insert_adds_text_and_moves_past_it()
    {
        var r = Run("abc", "\"a\" insert \"X\" to end");
        Assert.True(r.Matched);
        Assert.Equal("aXbc", r.Output);
    }

    [Fact]
    public void Insert_at_start_char_integer_and_variable()
    {
        Assert.Equal("[abc", Run("abc", "insert #\"[\" to end").Output);
        Assert.Equal("7abc", Run("abc", "insert 7 to end").Output);
        Assert.Equal("abcabc", Run("abc", "copy w to end insert w").Output);
    }

    [Fact]
    public void Insert_into_a_block_adds_one_item()
    {
        var r = Parser.Parse(new List<object> { 1, 3 }, Dialect.Read("integer! insert 2 integer!"));
        Assert.True(r.Matched);
        Assert.Equal(new object[] { 1, 2, 3 }, Assert.IsAssignableFrom<IReadOnlyList<object>>(r.Output));
    }

    [Fact]
    public void Insert_of_an_empty_string_changes_nothing()
    {
        var r = Run("ab", "insert \"\" to end");
        Assert.True(r.Matched);
        Assert.Equal("ab", r.Output);
    }

    [Fact]
    public void Insert_of_a_null_variable_into_text_is_empty_but_throws_for_blocks()
    {
        Assert.Equal("ab", Run("ab", "set c opt \"z\" insert c to end").Output);
        Assert.Throws<ArgumentNullException>(() =>
            Parser.Parse(new List<object> { 1 }, Dialect.Read("set c opt \"z\" insert c skip")));
    }

    [Fact]
    public void Insert_with_an_undefined_variable_throws()
    {
        var ex = Assert.Throws<KeyNotFoundException>(() => Run("a", "insert nope"));
        Assert.Contains("'nope'", ex.Message);
    }

    [Fact]
    public void Insert_fluent_builders()
    {
        Assert.Equal("-a", Parser.Parse("a", Insert("-") + To(End)).Output);
        Assert.Equal("1a", Parser.Parse("a", Insert(_ => 1) + To(End)).Output);
        Assert.Equal("[a", Parser.Parse("a", Insert((object?)null) + Insert("[") + To(End)).Output);
    }

    // ---- change ----

    [Fact]
    public void Change_replaces_the_matched_text()
    {
        var r = Run("a-b-c", "any [change \"-\" \"+\" | skip]");
        Assert.True(r.Matched);
        Assert.Equal("a+b+c", r.Output);
    }

    [Fact]
    public void Change_can_grow_and_shrink_the_text()
    {
        Assert.Equal("a--b", Run("a-b", "any [change \"-\" \"--\" | skip]").Output);
        Assert.Equal("ab", Run("a--b", "any [change \"--\" \"\" | skip]").Output);
    }

    [Fact]
    public void Change_fails_without_editing_when_the_rule_does_not_match()
    {
        var r = Run("abc", "change \"x\" \"y\" | to end");
        Assert.True(r.Matched);
        Assert.Equal("abc", r.Output);
    }

    [Fact]
    public void Change_in_block_mode_and_with_captured_values()
    {
        var r = Parser.Parse(new List<object> { 1, "old", 3 }, Dialect.Read("integer! change string! 2 integer!"));
        Assert.True(r.Matched);
        Assert.Equal(new object[] { 1, 2, 3 }, Assert.IsAssignableFrom<IReadOnlyList<object>>(r.Output));

        Assert.Equal("[aa]", Parser.Parse("ab", Insert("[") + Copy("x", Skip) + Change(Skip, c => c.Vars["x"] + "]") + To(End)).Output);
    }

    [Fact]
    public void Change_fluent_builders()
    {
        Assert.Equal("Xb", Parser.Parse("ab", Change("a", "X") + "b").Output);
        Assert.Equal("Xb", Parser.Parse("ab", Change("a", _ => "X") + "b").Output);
    }

    // ---- remove ----

    [Fact]
    public void Remove_deletes_the_matched_text()
    {
        var r = Run("a1b22c", "any [remove some digit | skip]");
        Assert.True(r.Matched);
        Assert.Equal("abc", r.Output);
    }

    [Fact]
    public void Remove_loops_keep_going_even_though_the_position_does_not_move()
    {
        Assert.Equal("", Run("aaa", "any [remove \"a\"]").Output);
    }

    [Fact]
    public void Remove_of_a_zero_width_match_stops_the_loop()
    {
        Assert.Equal("abc", Run("abc", "any [remove none]", partial: true).Output);
    }

    [Fact]
    public void Remove_in_block_mode_and_failure_case()
    {
        var r = Parser.Parse(new List<object> { 1, "x", 2 }, Dialect.Read("any [remove string! | skip]"));
        Assert.Equal(new object[] { 1, 2 }, Assert.IsAssignableFrom<IReadOnlyList<object>>(r.Output));
        Assert.Equal("abc", Run("abc", "remove \"z\" | to end").Output);
    }

    [Fact]
    public void Remove_fluent_builder()
    {
        Assert.Equal("b", Parser.Parse("ab", Remove("a") + "b").Output);
    }

    [Fact]
    public void Edits_never_touch_the_callers_input()
    {
        var items = new List<object> { 1, 2 };
        var r = Parser.Parse(items, Dialect.Read("remove integer! integer!"));
        Assert.True(r.Matched);
        Assert.Equal(2, items.Count);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<object>>(r.Output));
    }

    [Fact]
    public void Edits_inside_into_are_written_back_to_the_parent()
    {
        var input = new List<object> { "a", new List<object> { 1, "junk", 2 }, "z" };
        var r = Parser.Parse(input, Dialect.Read("\"a\" into [any [remove string! | skip]] \"z\""));
        Assert.True(r.Matched);
        var output = Assert.IsAssignableFrom<IReadOnlyList<object>>(r.Output);
        Assert.Equal(new object[] { 1, 2 }, Assert.IsAssignableFrom<IReadOnlyList<object>>(output[1]));
        Assert.Equal(3, ((List<object>)input[1]).Count); // original nested list untouched
    }

    [Fact]
    public void Unedited_into_leaves_the_parent_alone()
    {
        var inner = new List<object> { 1 };
        var r = Parser.Parse(new List<object> { inner }, Dialect.Read("into [integer!]"));
        Assert.Same(inner, Assert.IsAssignableFrom<IReadOnlyList<object>>(r.Output)[0]);
    }

    [Fact]
    public void Output_is_present_even_without_edits()
    {
        Assert.Equal("abc", Run("abc", "to end").Output);
        Assert.Equal("abc", Parser.Parse("abc", Fail).Output);
    }

    [Fact]
    public void Series_edit_members()
    {
        var s = new StringSeries("abc");
        Assert.False(s.Modified);
        Assert.Equal(1, s.Insert(0, "x"));
        Assert.True(s.Modified);
        s = new StringSeries("abc");
        s.Remove(1, 1);
        Assert.Equal(0, s.Insert(1, ""));
        Assert.False(s.Modified);
        s.Remove(0, 1);
        Assert.True(s.Modified);
        Assert.Equal("bc", s.ToResult());

        var b = new BlockSeries(new List<object> { 1, 2 });
        b.Remove(1, 1);
        Assert.False(b.Modified);
        Assert.Contains("null", Assert.Throws<ArgumentNullException>(() => b.Insert(0, null)).Message);
        Assert.Equal(1, b.Insert(0, "z"));
        Assert.True(b.Modified);
        Assert.Equal(new object[] { "z", 1, 2 }, Assert.IsAssignableFrom<IReadOnlyList<object>>(b.ToResult()));
    }

    // ---- collect / keep ----

    [Fact]
    public void Collect_gathers_kept_text_in_order()
    {
        var r = Run("a1b22c", "collect out any [keep some digit | skip]");
        Assert.True(r.Matched);
        Assert.Equal(new object[] { "1", "22" }, Assert.IsAssignableFrom<IReadOnlyList<object>>(r.Vars["out"]));
    }

    [Fact]
    public void Keep_in_block_mode_keeps_single_items_and_lists_for_longer_matches()
    {
        var input = new List<object> { 1, 2, "x", 3 };
        var r = Parser.Parse(input, Dialect.Read("collect out any [keep some integer! | skip]"));
        var kept = Assert.IsAssignableFrom<IReadOnlyList<object>>(r.Vars["out"]);
        Assert.Equal(2, kept.Count);
        Assert.Equal(new object[] { 1, 2 }, Assert.IsAssignableFrom<IReadOnlyList<object>>(kept[0]));
        Assert.Equal(3, kept[1]);

        var singles = Parser.Parse(new List<object> { 1, 2 }, Dialect.Read("collect out some [keep integer!]"));
        Assert.Equal(new object[] { 1, 2 }, Assert.IsAssignableFrom<IReadOnlyList<object>>(singles.Vars["out"]));
    }

    [Fact]
    public void Failed_branches_drop_their_kept_values()
    {
        // the first branch keeps "a" then fails on "z"; only the second branch's keep should survive
        var r = Run("ab", "collect out [[keep \"a\" \"z\"] | keep \"a\" keep \"b\"]");
        Assert.True(r.Matched);
        Assert.Equal(new object[] { "a", "b" }, Assert.IsAssignableFrom<IReadOnlyList<object>>(r.Vars["out"]));
    }

    [Fact]
    public void Failed_collect_stores_nothing()
    {
        var r = Run("a", "collect out [keep \"a\" \"z\"] | \"a\"");
        Assert.True(r.Matched);
        Assert.False(r.Vars.ContainsKey("out"));
    }

    [Fact]
    public void Failed_keep_adds_nothing()
    {
        var r = Run("b", "collect out [keep \"a\" | skip]");
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<object>>(r.Vars["out"]));
    }

    [Fact]
    public void Nested_collects_keep_to_their_own_list()
    {
        var r = Run("ab", "collect outer [keep \"a\" collect inner [keep \"b\"]]");
        Assert.Equal(new object[] { "a" }, Assert.IsAssignableFrom<IReadOnlyList<object>>(r.Vars["outer"]));
        Assert.Equal(new object[] { "b" }, Assert.IsAssignableFrom<IReadOnlyList<object>>(r.Vars["inner"]));
    }

    [Fact]
    public void A_failed_inner_collect_leaves_the_outer_list_intact()
    {
        var r = Run("ax", "collect outer [keep \"a\" [collect inner [keep \"x\" \"z\"] | \"x\"]]");
        Assert.True(r.Matched);
        Assert.Equal(new object[] { "a" }, Assert.IsAssignableFrom<IReadOnlyList<object>>(r.Vars["outer"]));
    }

    [Fact]
    public void Keep_after_a_collect_has_finished_throws()
    {
        Assert.Throws<InvalidOperationException>(() => Run("ab", "[collect x [keep \"a\"]] keep \"b\""));
    }

    [Fact]
    public void Keep_outside_collect_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Run("a", "keep \"a\""));
        Assert.Contains("collect", ex.Message);
    }

    [Fact]
    public void Collect_and_keep_fluent_builders()
    {
        var r = Parser.Parse("a1", Collect("xs", Keep(Alpha) + Keep(Digit)));
        Assert.Equal(new object[] { "a", "1" }, Assert.IsAssignableFrom<IReadOnlyList<object>>(r.Vars["xs"]));
    }

    // ---- reader errors ----

    [Theory]
    [InlineData("insert", "'insert' needs a value")]
    [InlineData("insert [", "'insert' cannot take '['")]
    [InlineData("change \"a\"", "'change' needs a value")]
    [InlineData("change \"a\" |", "'change' cannot take '|'")]
    [InlineData("change", "ended")]
    [InlineData("remove", "ended")]
    [InlineData("keep", "ended")]
    [InlineData("collect", "'collect' needs a variable")]
    [InlineData("collect x", "ended")]
    public void New_keywords_report_malformed_rules(string text, string fragment)
    {
        var ex = Assert.Throws<FormatException>(() => Dialect.Read(text));
        Assert.Contains(fragment, ex.Message);
    }

    [Fact]
    public void New_keywords_are_case_insensitive_in_text()
    {
        Assert.Equal("Xa", Run("a", "INSERT \"X\" To End").Output);
        Assert.True(Run("a", "[Collect x [Keep skip]]").Matched);
    }
}
