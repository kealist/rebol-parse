using System.Text;

namespace RebolParse;

/// <summary>A REBOL-style word value, for block-mode input.</summary>
public sealed record Word(string Name)
{
    public override string ToString() => Name;
}

/// <summary>The input being parsed: a string or a block of values. A parse works on a private copy, so edits never touch the caller's data.</summary>
public interface ISeries
{
    int Length { get; }
    object this[int index] { get; }

    /// <summary>Length consumed if <paramref name="value"/> matches at <paramref name="pos"/>, else -1.</summary>
    int MatchLiteral(int pos, object value);

    /// <summary>The input between two positions (a string, or a list for blocks).</summary>
    object Slice(int start, int end);

    /// <summary>Remove the input in [start, end).</summary>
    void Remove(int start, int end);

    /// <summary>Insert a value at <paramref name="pos"/> and return how many positions it occupies (its length for text, 1 for a block item).</summary>
    int Insert(int pos, object? value);

    /// <summary>True once an edit has changed the input.</summary>
    bool Modified { get; }

    /// <summary>The current contents as a new string or list.</summary>
    object ToResult();
}

public sealed class StringSeries(string text, bool ignoreCase = false) : ISeries
{
    private readonly StringBuilder sb = new(text);

    public int Length => sb.Length;
    public object this[int index] => sb[index];
    public bool Modified { get; private set; }

    public int MatchLiteral(int pos, object value)
    {
        switch (value)
        {
            case char c:
                return pos < sb.Length && SameChar(sb[pos], c) ? 1 : -1;
            case string s:
                if (pos + s.Length > sb.Length) return -1;
                for (var k = 0; k < s.Length; k++)
                    if (!SameChar(sb[pos + k], s[k])) return -1;
                return s.Length;
            default:
                return -1;
        }
    }

    private bool SameChar(char a, char b) =>
        a == b || (ignoreCase && char.ToUpperInvariant(a) == char.ToUpperInvariant(b));

    public object Slice(int start, int end) => sb.ToString(start, end - start);

    public void Remove(int start, int end)
    {
        if (end <= start) return;
        sb.Remove(start, end - start);
        Modified = true;
    }

    public int Insert(int pos, object? value)
    {
        var str = value?.ToString() ?? "";
        if (str.Length == 0) return 0;
        sb.Insert(pos, str);
        Modified = true;
        return str.Length;
    }

    public object ToResult() => sb.ToString();
}

public sealed class BlockSeries(IReadOnlyList<object> items, bool ignoreCase = false) : ISeries
{
    private readonly List<object> items = [.. items];

    public int Length => items.Count;
    public object this[int index] => items[index];
    public bool Modified { get; private set; }

    public int MatchLiteral(int pos, object value) =>
        pos < items.Count && SameValue(items[pos], value) ? 1 : -1;

    private bool SameValue(object a, object b) => (a, b) switch
    {
        (string x, string y) when ignoreCase => string.Equals(x, y, StringComparison.OrdinalIgnoreCase),
        (char x, char y) when ignoreCase => char.ToUpperInvariant(x) == char.ToUpperInvariant(y),
        (Word x, Word y) when ignoreCase => string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase),
        _ => Equals(a, b),
    };

    public object Slice(int start, int end)
    {
        var list = new List<object>(end - start);
        for (var i = start; i < end; i++) list.Add(items[i]);
        return list;
    }

    public void Remove(int start, int end)
    {
        if (end <= start) return;
        items.RemoveRange(start, end - start);
        Modified = true;
    }

    public int Insert(int pos, object? value)
    {
        items.Insert(pos, value ?? throw new ArgumentNullException(nameof(value), "Cannot insert null into a block."));
        Modified = true;
        return 1;
    }

    public object ToResult() => new List<object>(items);
}
