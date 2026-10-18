using System.Collections;
using System.Globalization;
using RebolParse;
using static RebolParse.P;

namespace Samples;

/// <summary>
/// A tiny template engine: <c>{{ name | upper | plural:item }}</c> tags are matched with the text dialect and
/// replaced in place with <c>Change</c>, whose replacement is computed in C# from what the tag captured.
/// </summary>
public sealed class TemplateDemo : IDemo
{
    public string Name => "template";
    public string Title => "Templates: change with a computed replacement, filters, nested data";

    private readonly Dictionary<string, Rule> rules;
    private readonly Rule render;
    private readonly List<string> filters = [];
    private readonly List<string> missing = [];
    private IReadOnlyDictionary<string, object?> data = new Dictionary<string, object?>();

    public TemplateDemo()
    {
        rules = Dialect.ReadGrammar("""
            ws  = any space
            tag = "{{" (@reset) ws copy key [alpha any [alnum | "_" | "."]] ws
                  any ["|" ws copy f some [not [ws "}}" | ws "|"] skip] ws (@filter)]
                  "}}"
            """, new Dictionary<string, Action<Ctx>>
        {
            ["reset"] = _ => filters.Clear(),
            ["filter"] = c => filters.Add((string)c.Vars["f"]!),
        }).Compiled();
        render = Any(Choice(
            Change((Rule)"\\{{", "{{"),            // \{{ writes a literal {{
            Change(Ref("tag"), Resolve),
            Skip)).Compiled();
    }

    private object? Lookup(string path)
    {
        object? at = data;
        foreach (var part in path.Split('.'))
        {
            if (at is IReadOnlyDictionary<string, object?> d && d.TryGetValue(part, out var next)) at = next;
            else return null;
        }
        return at;
    }

    private string Resolve(Ctx ctx)
    {
        var key = (string)ctx.Vars["key"]!;
        var value = Lookup(key);
        foreach (var filter in filters)
        {
            var colon = filter.IndexOf(':');
            var name = colon < 0 ? filter : filter[..colon];
            var arg = colon < 0 ? "" : filter[(colon + 1)..];
            value = (name, value) switch
            {
                ("default", null) => arg,
                ("default", _) => value,
                (_, null) => null,
                ("upper", _) => value.ToString()!.ToUpperInvariant(),
                ("lower", _) => value.ToString()!.ToLowerInvariant(),
                ("title", _) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToString()!.ToLowerInvariant()),
                ("len", IEnumerable e and not string) => e.Cast<object>().Count(),
                ("len", _) => value.ToString()!.Length,
                ("plural", _) => Convert.ToInt32(value) == 1 ? arg : arg + "s",
                ("join", IEnumerable e) => string.Join(arg, e.Cast<object>()),
                _ => throw new FormatException($"unknown filter '{name}' in the tag for '{key}'"),
            };
        }
        if (value is null) missing.Add(key);
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    /// <summary>Fill in a template. A tag whose value is missing renders as nothing and is listed in <paramref name="missingKeys"/>.</summary>
    public string Render(string template, IReadOnlyDictionary<string, object?> values, out List<string> missingKeys)
    {
        data = values;
        missing.Clear();
        var result = Parser.Parse(template, render, rules);
        missingKeys = [.. missing];
        return (string)result.Output!;
    }

    public void Run(Report r)
    {
        var values = new Dictionary<string, object?>
        {
            ["name"] = "joshua",
            ["count"] = 3,
            ["user"] = new Dictionary<string, object?> { ["city"] = "Tampa", ["langs"] = new[] { "C#", "Dart", "REBOL" } },
            ["items"] = new[] { "a", "b", "c" },
        };

        var text = Render("Hello {{ name | upper }}! You have {{ count }} new {{ count | plural:message }} in {{user.city | lower}}.", values, out var none);
        r.Say(text);
        r.Expect("filters and a plural", text, "Hello JOSHUA! You have 3 new messages in tampa.");
        r.Expect("nothing was missing", none.Count, 0);

        r.Expect("nested data and join", Render("{{ user.langs | join:/ }} ({{ user.langs | len }})", values, out _), "C#/Dart/REBOL (3)");
        r.Expect("chained filters", Render("{{ name | upper | len }}", values, out _), "6");
        r.Expect("title case", Render("{{ user.city | upper | title }}", values, out _), "Tampa");
        r.Expect("a default for a missing value", Render("[{{ nope | default:n/a }}]", values, out var m1), "[n/a]");
        r.Expect("a default does not count as missing", m1.Count, 0);
        r.Expect("a missing value renders as nothing", Render("[{{ nope }}] [{{ user.zip }}]", values, out var m2), "[] []");
        r.Expect("and is reported", string.Join(",", m2), "nope,user.zip");
        r.Expect("an escaped tag stays literal", Render("\\{{ name }} and {{name}}", values, out _), "{{ name }} and joshua");
        r.Expect("text with no tags is untouched", Render("just { text } here", values, out _), "just { text } here");
        r.Expect("a half-written tag is left alone", Render("{{ name", values, out _), "{{ name");
        r.Expect("the singular", Render("{{ count }} {{ count | plural:thing }}", new Dictionary<string, object?> { ["count"] = 1 }, out _), "1 thing");
        try { Render("{{ name | shout }}", values, out _); r.Expect("an unknown filter throws", false); }
        catch (FormatException ex) { r.Expect("an unknown filter throws", true); r.Say(ex.Message); }

        // 10,000 tags in a 400 KB document: every change grows or shrinks the text in place
        var page = string.Concat(Enumerable.Repeat("<li>{{ name | upper }} has {{ count }} {{ count | plural:thing }} in {{ user.city }}</li>\n", 5000));
        var rendered = Render(page, values, out var absent);
        r.Expect("a long document renders", rendered, string.Concat(Enumerable.Repeat("<li>JOSHUA has 3 things in Tampa</li>\n", 5000)));
        r.Expect("with nothing missing", absent.Count, 0);
    }
}
