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

    /// <summary>The current contents: the original input if nothing was edited, otherwise a new string or list.</summary>
    object ToResult();

    /// <summary>True for string input, where <see cref="CharAt"/> reads a character without boxing it.</summary>
    bool IsText { get; }

    /// <summary>The character at <paramref name="index"/>. Only valid when <see cref="IsText"/>.</summary>
    char CharAt(int index);
}

public sealed class StringSeries : ISeries
{
    private string text;
    private bool ignoreCase;
    private bool readOnly;
    private StringBuilder? sb;   // created on the first edit and kept for reuse
    private bool editing;

    public StringSeries(string text, bool ignoreCase = false)
    {
        this.text = text;
        this.ignoreCase = ignoreCase;
    }

    internal void Reset(string newText, bool newIgnoreCase, bool newReadOnly)
    {
        text = newText;
        ignoreCase = newIgnoreCase;
        readOnly = newReadOnly;
        editing = false;
    }

    public int Length => editing ? sb!.Length : text.Length;
    public bool IsText => true;
    public char CharAt(int index) => editing ? sb![index] : text[index];
    public object this[int index] => CharAt(index);
    public bool Modified => editing;

    public int MatchLiteral(int pos, object value)
    {
        switch (value)
        {
            case char c:
                return pos < Length && SameChar(CharAt(pos), c) ? 1 : -1;
            case string s:
                if (pos + s.Length > Length) return -1;
                for (var k = 0; k < s.Length; k++)
                    if (!SameChar(CharAt(pos + k), s[k])) return -1;
                return s.Length;
            default:
                return -1;
        }
    }

    private bool SameChar(char a, char b) =>
        a == b || (ignoreCase && char.ToUpperInvariant(a) == char.ToUpperInvariant(b));

    public object Slice(int start, int end) =>
        editing ? sb!.ToString(start, end - start) : text.Substring(start, end - start);

    private void CheckWritable()
    {
        if (readOnly) throw new InvalidOperationException("This parse is read-only (IsMatch); use Parser.Parse to edit input.");
    }

    private void BeginEdit()
    {
        CheckWritable();
        if (editing) return;
        sb ??= new StringBuilder();
        sb.Clear().Append(text);
        editing = true;
    }

    public void Remove(int start, int end)
    {
        if (end <= start) { CheckWritable(); return; }
        BeginEdit();
        sb!.Remove(start, end - start);
    }

    public int Insert(int pos, object? value)
    {
        var str = value?.ToString() ?? "";
        if (str.Length == 0) { CheckWritable(); return 0; }
        BeginEdit();
        sb!.Insert(pos, str);
        return str.Length;
    }

    public object ToResult() => editing ? sb!.ToString() : text;
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
    public bool IsText => false;
    public char CharAt(int index) => throw new NotSupportedException("Block input has no characters.");
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
