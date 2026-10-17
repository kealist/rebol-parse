namespace RebolParse;

/// <summary>A REBOL-style word value, for block-mode input.</summary>
public sealed record Word(string Name)
{
    public override string ToString() => Name;
}

/// <summary>Block input being parsed by the interpreter. Edits go to a private copy, so they never touch the caller's list.</summary>
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

    /// <summary>The current contents: the original input if nothing was edited, otherwise a new string or list.</summary>
    object ToResult();
}

public sealed class BlockSeries : ISeries
{
    private IReadOnlyList<object> source;
    private bool ignoreCase;
    private bool readOnly;
    private List<object>? edited;   // created on the first edit and kept for reuse
    private bool editing;

    public BlockSeries(IReadOnlyList<object> items, bool ignoreCase = false)
    {
        source = items;
        this.ignoreCase = ignoreCase;
    }

    internal void Reset(IReadOnlyList<object> items, bool newIgnoreCase, bool newReadOnly)
    {
        source = items;
        ignoreCase = newIgnoreCase;
        readOnly = newReadOnly;
        editing = false;
    }

    private IReadOnlyList<object> Items => editing ? edited! : source;

    public int Length => Items.Count;
    public object this[int index] => Items[index];
    public bool Modified => editing;

    public int MatchLiteral(int pos, object value) =>
        pos < Length && SameValue(Items[pos], value) ? 1 : -1;

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
        for (var i = start; i < end; i++) list.Add(Items[i]);
        return list;
    }

    private void CheckWritable()
    {
        if (readOnly) throw new InvalidOperationException("This parse is read-only (IsMatch); use Parser.Parse to edit input.");
    }

    private void BeginEdit()
    {
        CheckWritable();
        if (editing) return;
        edited ??= [];
        edited.Clear();
        for (var i = 0; i < source.Count; i++) edited.Add(source[i]);
        editing = true;
    }

    public void Remove(int start, int end)
    {
        if (end <= start) { CheckWritable(); return; }
        BeginEdit();
        edited!.RemoveRange(start, end - start);
    }

    public int Insert(int pos, object? value)
    {
        if (value is null)
        {
            CheckWritable();
            throw new ArgumentNullException(nameof(value), "Cannot insert null into a block.");
        }
        BeginEdit();
        edited!.Insert(pos, value);
        return 1;
    }

    public object ToResult() => editing ? new List<object>(edited!) : source;
}
