namespace RebolParse;

/// <summary>
/// A set of characters written like a regex class body: <c>"a-zA-Z_"</c> is the letters and the underscore.
/// A <c>-</c> between two characters makes a range; a <c>-</c> first or last is just a hyphen. Membership is a
/// bit test for ASCII and a short range scan beyond it, with no allocation.
/// </summary>
public sealed class CharSet
{
    private readonly ulong low;    // bits for chars 0-63
    private readonly ulong high;   // bits for chars 64-127
    private readonly (char Lo, char Hi)[] wide;   // ranges that reach beyond ASCII

    private CharSet(ulong low, ulong high, (char, char)[] wide)
    {
        this.low = low;
        this.high = high;
        this.wide = wide;
    }

    /// <summary>Reads a set from its spec, e.g. <c>"0-9a-fA-F"</c> or <c>"+-*/"</c>.</summary>
    /// <exception cref="FormatException">A range runs backwards, like <c>"z-a"</c>.</exception>
    public static CharSet Parse(string spec)
    {
        ulong low = 0, high = 0;
        var wide = new List<(char, char)>();
        for (var i = 0; i < spec.Length; i++)
        {
            var lo = spec[i];
            var hi = lo;
            if (i + 2 < spec.Length && spec[i + 1] == '-')
            {
                hi = spec[i + 2];
                if (hi < lo) throw new FormatException($"Character range '{lo}-{hi}' runs backwards.");
                i += 2;
            }
            for (var c = (int)lo; c <= hi && c < 128; c++)
            {
                if (c < 64) low |= 1UL << c; else high |= 1UL << (c - 64);
            }
            if (hi >= 128) wide.Add(((char)Math.Max(lo, (char)128), hi));
        }
        return new CharSet(low, high, [.. wide]);
    }

    /// <summary>Whether <paramref name="c"/> is in the set.</summary>
    public bool Contains(char c)
    {
        if (c < 64) return (low >> c & 1) != 0;
        if (c < 128) return (high >> (c - 64) & 1) != 0;
        foreach (var (lo, hi) in wide)
            if (c >= lo && c <= hi) return true;
        return false;
    }
}
