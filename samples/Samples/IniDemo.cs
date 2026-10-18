using System.Globalization;
using RebolParse;

namespace Samples;

/// <summary>
/// A forgiving INI reader that parses one line at a time, so an error names its line number and the rest of
/// the file still loads. Supports comments, quoted values, and typed lookups.
/// </summary>
public sealed class IniDemo : IDemo
{
    public string Name => "ini";
    public string Title => "INI: line-by-line config with typed values and error reports";

    /// <summary>The result of reading a file.</summary>
    public sealed class Ini
    {
        public Dictionary<string, Dictionary<string, string>> Sections { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Errors { get; } = [];

        public string? Get(string section, string key) =>
            Sections.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : null;

        public int Int(string section, string key, int fallback = 0) =>
            int.TryParse(Get(section, key), CultureInfo.InvariantCulture, out var v) ? v : fallback;

        public bool Bool(string section, string key, bool fallback = false) => Get(section, key)?.ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" => false,
            _ => fallback,
        };
    }

    private readonly Dictionary<string, Rule> rules;
    private Ini current = new();
    private string section = "";

    public IniDemo()
    {
        var actions = new Dictionary<string, Action<Ctx>>
        {
            ["section"] = c =>
            {
                section = (string)c.Vars["name"]!;
                current.Sections.TryAdd(section, new(StringComparer.OrdinalIgnoreCase));
            },
            ["pair"] = c =>
            {
                current.Sections.TryAdd(section, new(StringComparer.OrdinalIgnoreCase));
                current.Sections[section][(string)c.Vars["key"]!] = (string)c.Vars["val"]!;
            },
        };
        rules = Dialect.ReadGrammar("""
            ws      = any [" " | tab]
            line    = ws [comment | section | pair | none] ws end
            comment = [";" | "#"] to end
            section = "[" ws copy name some [not [ws "]"] skip] ws "]" (@section)
            pair    = copy key some [not [ws "="] skip] ws "=" ws
                      ["^"" copy val to "^"" "^"" | copy val any [not [ws end] skip]] (@pair)
            """, actions);
    }

    public Ini Read(string text)
    {
        current = new Ini();
        section = "";
        var number = 0;
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            number++;
            if (!Parser.Parse(line, Ref("line"), rules).Matched)
                current.Errors.Add($"line {number}: cannot read '{line.Trim()}'");
        }
        return current;
    }

    private static Rule Ref(string name) => RebolParse.P.Ref(name);

    private const string Sample = """
        ; server settings
        [server]
        host = example.com
        port=8080
        debug = yes
        motto = "keep = and ; inside quotes"

        # a different section
        [ database ]
        url   =   postgres://db/main
        pool = 12
        this line is nonsense
        [broken
        = no key
        timeout = 30
        """;

    public void Run(Report r)
    {
        var ini = Read(Sample);
        foreach (var (name, pairs) in ini.Sections)
            r.Say($"[{name}] " + string.Join(", ", pairs.Select(p => $"{p.Key}={p.Value}")));
        foreach (var e in ini.Errors) r.Say("error: " + e);

        r.Expect("string value", ini.Get("server", "host"), "example.com");
        r.Expect("no spaces around =", ini.Get("server", "port"), "8080");
        r.Expect("int value", ini.Int("server", "port"), 8080);
        r.Expect("bool value", ini.Bool("server", "debug"), true);
        r.Expect("quoted value keeps = and ;", ini.Get("server", "motto"), "keep = and ; inside quotes");
        r.Expect("section names are trimmed", ini.Get("database", "url"), "postgres://db/main");
        r.Expect("lookups ignore case", ini.Get("DATABASE", "POOL"), "12");
        r.Expect("keys after a broken line still load", ini.Get("database", "timeout"), "30");
        r.Expect("a missing key falls back", ini.Int("server", "nothing", 7), 7);
        r.Expect("bad lines are reported with numbers", string.Join(" | ", ini.Errors),
            "line 12: cannot read 'this line is nonsense' | line 13: cannot read '[broken' | line 14: cannot read '= no key'");
    }
}
