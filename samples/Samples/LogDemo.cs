using System.Globalization;
using RebolParse;
using static RebolParse.P;

namespace Samples;

/// <summary>
/// Parse a plain-text log, then summarize it. One rule reads the fixed part of each line; a second rule picks
/// the <c>key=value</c> pairs out of the free-text message. Lines that do not parse are counted, not fatal.
/// </summary>
public sealed class LogDemo : IDemo
{
    public string Name => "log";
    public string Title => "Log analysis: structured fields out of free text";

    public sealed record Entry(DateTime Time, string Level, string Component, string Message, Dictionary<string, string> Fields);

    private readonly Rule line;
    private readonly Rule pairs;
    private Dictionary<string, string> found = [];
    private Entry? entry;

    public LogDemo()
    {
        var lineActions = new Dictionary<string, Action<Ctx>>
        {
            ["entry"] = c =>
            {
                var time = DateTime.ParseExact((string)c.Vars["date"]! + " " + (string)c.Vars["time"]!,
                    c.Vars["time"]!.ToString()!.Contains('.') ? "yyyy-MM-dd HH:mm:ss.fff" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                entry = new Entry(time, (string)c.Vars["level"]!, (string)c.Vars["comp"]!, (string)c.Vars["msg"]!, []);
            },
        };
        line = Dialect.Read("""
            copy date [4 digit "-" 2 digit "-" 2 digit] some space
            copy time [2 digit ":" 2 digit ":" 2 digit opt ["." 3 digit]] some space
            copy level some upper some space
            "[" copy comp some [not "]" skip] "]" some space
            copy msg to end (@entry)
            """, lineActions).Compiled();
        pairs = Dialect.Read("""
            any [copy k [alpha any [alnum | "_"]] "=" copy v some [not space skip] (@kv) | skip]
            """, new Dictionary<string, Action<Ctx>> { ["kv"] = c => found[(string)c.Vars["k"]!] = (string)c.Vars["v"]! }).Compiled();
    }

    public (List<Entry> Entries, int Skipped) Read(string log)
    {
        var entries = new List<Entry>();
        var skipped = 0;
        foreach (var text in log.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            entry = null;
            if (!Parser.Parse(text, line).Matched || entry is null) { skipped++; continue; }
            found = [];
            Parser.Parse(entry.Message, pairs);
            entries.Add(entry with { Fields = found });
        }
        return (entries, skipped);
    }

    private const string Sample = """
        2026-10-18 14:03:22.417 INFO  [web] request served path=/home status=200 ms=12
        2026-10-18 14:03:23.002 WARN  [web] slow request path=/search status=200 ms=950
        2026-10-18 14:03:25.118 ERROR [db] connection refused host=10.0.0.5 retry=1
        2026-10-18 14:03:26.120 ERROR [db] connection refused host=10.0.0.5 retry=2
        --- a stack trace line that is not a log entry ---
        2026-10-18 14:03:27.500 INFO  [db] connection restored host=10.0.0.5
        2026-10-18 14:04:01 INFO  [web] request served path=/home status=200 ms=9
        2026-10-18 14:04:09 ERROR [web] upstream failed path=/api status=502 ms=3004
        2026-10-18 14:05:00.000 DEBUG [cache] evicted keys=14
        """;

    public void Run(Report r)
    {
        var (entries, skipped) = Read(Sample);
        r.Expect("entries read", entries.Count, 8);
        r.Expect("unparseable lines counted", skipped, 1);
        r.Expect("timestamps with and without milliseconds", entries[5].Time, new DateTime(2026, 10, 18, 14, 4, 1));
        r.Expect("component", entries[2].Component, "db");
        r.Expect("message", entries[0].Message, "request served path=/home status=200 ms=12");
        r.Expect("key=value pairs", entries[1].Fields["ms"], "950");

        var byLevel = entries.GroupBy(e => e.Level).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).ToList();
        r.Say(string.Join("  ", byLevel.Select(g => $"{g.Key}:{g.Count()}")));
        r.Expect("INFO count", byLevel.First(g => g.Key == "INFO").Count(), 3);
        r.Expect("ERROR count", byLevel.First(g => g.Key == "ERROR").Count(), 3);

        var slowest = entries.Where(e => e.Fields.ContainsKey("ms")).MaxBy(e => int.Parse(e.Fields["ms"]))!;
        r.Say($"slowest request: {slowest.Fields["path"]} took {slowest.Fields["ms"]} ms");
        r.Expect("slowest request", slowest.Fields["path"], "/api");

        var avg = entries.Where(e => e.Component == "web" && e.Fields.ContainsKey("ms")).Average(e => int.Parse(e.Fields["ms"]));
        r.Expect("average web latency", Math.Round(avg, 1, MidpointRounding.AwayFromZero), 993.8);

        var perComponent = entries.Where(e => e.Level == "ERROR").GroupBy(e => e.Component).ToDictionary(g => g.Key, g => g.Count());
        r.Expect("errors in db", perComponent["db"], 2);
        r.Expect("errors in web", perComponent["web"], 1);
        r.Expect("span covered", entries[^1].Time - entries[0].Time, new TimeSpan(0, 0, 1, 37, 583));

        // 50,000 lines
        var big = string.Join("\n", Enumerable.Range(0, 50000).Select(i =>
            $"2026-10-18 10:{i / 60 % 60:00}:{i % 60:00}.000 {(i % 10 == 0 ? "ERROR" : "INFO ")} [svc{i % 4}] handled id={i} ms={i % 97}"));
        var (many, _) = Read(big);
        r.Expect("50,000 lines read", many.Count, 50000);
        r.Expect("errors", many.Count(e => e.Level == "ERROR"), 5000);
    }
}
