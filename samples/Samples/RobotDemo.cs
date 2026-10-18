using System.Text;
using RebolParse;
using static RebolParse.P;

namespace Samples;

/// <summary>
/// Block mode: the input is a list of values (words, integers, strings, nested lists), not characters. A tiny
/// robot language is parsed into a command tree with <c>into</c> for nested blocks, then run on a grid.
/// </summary>
public sealed class RobotDemo : IDemo
{
    public string Name => "robot";
    public string Title => "Block mode: a robot language read from values, not characters";

    public abstract record Cmd;
    public sealed record Move(int Steps) : Cmd;
    public sealed record Turn(bool Right) : Cmd;
    public sealed record Say(string Text) : Cmd;
    public sealed record Repeat(int Times, List<Cmd> Body) : Cmd;

    private readonly Dictionary<string, Rule> rules;
    private readonly Stack<List<Cmd>> frames = new();
    private readonly Stack<int> counts = new();

    public RobotDemo()
    {
        var actions = new Dictionary<string, Action<Ctx>>
        {
            ["move"] = c => frames.Peek().Add(new Move((int)c.Vars["n"]!)),
            ["left"] = _ => frames.Peek().Add(new Turn(false)),
            ["right"] = _ => frames.Peek().Add(new Turn(true)),
            ["say"] = c => frames.Peek().Add(new Say((string)c.Vars["s"]!)),
            ["enter"] = c => { counts.Push((int)c.Vars["times"]!); frames.Push([]); },
            ["leave"] = _ => { var body = frames.Pop(); frames.Peek().Add(new Repeat(counts.Pop(), body)); },
        };
        rules = Dialect.ReadGrammar("""
            program = any command
            command = move | turn | repeat | say
            move    = quote move set n integer! (@move)
            turn    = quote turn [quote left (@left) | quote right (@right)]
            say     = quote say set s string! (@say)
            repeat  = quote repeat set times integer! (@enter) into program (@leave)
            """, actions);
    }

    /// <summary>Turn source text such as <c>move 5 repeat 2 [turn left]</c> into a block of values.</summary>
    public static List<object> Tokenize(string source)
    {
        var stack = new Stack<List<object>>();
        var current = new List<object>();
        var i = 0;
        while (i < source.Length)
        {
            var ch = source[i];
            if (char.IsWhiteSpace(ch)) { i++; continue; }
            if (ch == '[') { stack.Push(current); current = []; i++; continue; }
            if (ch == ']')
            {
                if (stack.Count == 0) throw new FormatException("unmatched ']'");
                var inner = current;
                current = stack.Pop();
                current.Add(inner);
                i++;
                continue;
            }
            if (ch == '"')
            {
                var end = source.IndexOf('"', i + 1);
                if (end < 0) throw new FormatException("unterminated string");
                current.Add(source[(i + 1)..end]);
                i = end + 1;
                continue;
            }
            var start = i;
            while (i < source.Length && !char.IsWhiteSpace(source[i]) && source[i] != '[' && source[i] != ']') i++;
            var word = source[start..i];
            current.Add(int.TryParse(word, out var n) ? n : new Word(word));
        }
        return stack.Count == 0 ? current : throw new FormatException("missing ']'");
    }

    /// <summary>Parse a program. Returns null when it is not valid; <paramref name="stoppedAt"/> is the index of the first bad value.</summary>
    public List<Cmd>? Read(List<object> program, out int stoppedAt)
    {
        frames.Clear();
        counts.Clear();
        frames.Push([]);
        var result = Parser.Parse(program, Ref("program"), rules, partial: true);
        stoppedAt = result.Position;
        return result.Matched && result.Position == program.Count ? frames.Peek() : null;
    }

    /// <summary>The robot's state after running commands.</summary>
    public sealed class Robot
    {
        private static readonly (int X, int Y)[] Heading = [(0, -1), (1, 0), (0, 1), (-1, 0)];
        public int X, Y, Facing = 1;   // starts facing east
        public readonly HashSet<(int, int)> Trail = [(0, 0)];
        public readonly List<string> Said = [];

        public void Run(IEnumerable<Cmd> commands)
        {
            foreach (var cmd in commands)
                switch (cmd)
                {
                    case Move m:
                        for (var s = 0; s < m.Steps; s++) { X += Heading[Facing].X; Y += Heading[Facing].Y; Trail.Add((X, Y)); }
                        break;
                    case Turn t: Facing = (Facing + (t.Right ? 1 : 3)) % 4; break;
                    case Say s: Said.Add(s.Text); break;
                    case Repeat rep: for (var k = 0; k < rep.Times; k++) Run(rep.Body); break;
                }
        }

        public string Draw()
        {
            var minX = Trail.Min(t => t.Item1); var maxX = Trail.Max(t => t.Item1);
            var minY = Trail.Min(t => t.Item2); var maxY = Trail.Max(t => t.Item2);
            var sb = new StringBuilder();
            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                    sb.Append(x == X && y == Y ? 'R' : x == 0 && y == 0 ? 'S' : Trail.Contains((x, y)) ? '#' : '.');
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }
    }

    public void Run(Report r)
    {
        // a square spiral drawn with nested repeats
        const string square = "repeat 4 [move 3 turn right] say \"square done\"";
        var commands = Read(Tokenize(square), out _);
        r.Expect("a program parses", commands is not null);
        r.Expect("the tree has a repeat and a say", string.Join(",", commands!.Select(c => c.GetType().Name)), "Repeat,Say");
        var bot = new Robot();
        bot.Run(commands!);
        r.Say(bot.Draw());
        r.Expect("a square ends where it started", (bot.X, bot.Y), (0, 0));
        r.Expect("it said its piece", string.Join("|", bot.Said), "square done");
        r.Expect("12 cells walked plus the start", bot.Trail.Count, 12);

        // nesting: repeat inside repeat
        var nested = Read(Tokenize("repeat 2 [repeat 3 [move 1] turn right turn right]"), out _);
        r.Expect("nested repeats parse", nested is [Repeat { Times: 2, Body: [Repeat { Times: 3 }, Turn, Turn] }]);
        var walker = new Robot();
        walker.Run(nested!);
        r.Expect("it walks out and back", (walker.X, walker.Y), (0, 0));

        // a staircase using two nested repeats
        var stairs = Read(Tokenize("repeat 4 [move 1 turn right move 1 turn left]"), out _)!;
        var climber = new Robot();
        climber.Run(stairs);
        r.Say(climber.Draw());
        r.Expect("the staircase ends at (8, 4)", (climber.X, climber.Y), (4, 4));

        // errors name the offending value
        foreach (var (source, badIndex) in new[] { ("move \"ten\"", 0), ("move 3 fly 2", 2), ("repeat 2 [move 1 jump]", 0), ("turn up", 0), ("repeat [move 1]", 0) })
        {
            var parsed = Read(Tokenize(source), out var at);
            r.Expect($"'{source}' is rejected", parsed is null);
            r.Say($"'{source}' stopped at value {at}");
            r.Expect($"'{source}' stops at value {badIndex}", at, badIndex);
        }
        try { Tokenize("move [1"); r.Expect("unbalanced brackets are rejected", false); }
        catch (FormatException) { r.Expect("unbalanced brackets are rejected", true); }
    }
}
