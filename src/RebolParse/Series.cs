namespace RebolParse;

/// <summary>A REBOL-style word value, for block-mode input.</summary>
public sealed record Word(string Name)
{
    public override string ToString() => Name;
}

/// <summary>Read-only view of the input being parsed: a string or a block of values.</summary>
public interface ISeries
{
    int Length { get; }
    object this[int index] { get; }

    /// <summary>Length consumed if <paramref name="value"/> matches at <paramref name="pos"/>, else -1.</summary>
    int MatchLiteral(int pos, object value);

    /// <summary>The input between two positions (a string, or a list for blocks).</summary>
    object Slice(int start, int end);
}

public sealed class StringSeries(string text) : ISeries
{
    public int Length => text.Length;
    public object this[int index] => text[index];

    public int MatchLiteral(int pos, object value) => value switch
    {
        char c => pos < text.Length && text[pos] == c ? 1 : -1,
        string s => text.AsSpan(pos).StartsWith(s, StringComparison.Ordinal) ? s.Length : -1,
        _ => -1,
    };

    public object Slice(int start, int end) => text[start..end];
}

public sealed class BlockSeries(IReadOnlyList<object> items) : ISeries
{
    public int Length => items.Count;
    public object this[int index] => items[index];

    public int MatchLiteral(int pos, object value) =>
        pos < items.Count && Equals(items[pos], value) ? 1 : -1;

    public object Slice(int start, int end)
    {
        var list = new List<object>(end - start);
        for (var i = start; i < end; i++) list.Add(items[i]);
        return list;
    }
}
