// Language and library features the code uses that .NET Standard 2.0 does not have. Each is only compiled for that target.

using System.Runtime.CompilerServices;

namespace RebolParse
{
    /// <summary>ASCII character tests, which <c>char.IsAsciiDigit</c> and friends only provide from .NET 7.</summary>
    internal static class Ascii
    {
        public static bool IsDigit(char c) => c >= '0' && c <= '9';
        public static bool IsLetter(char c) => (uint)((c | 0x20) - 'a') <= 'z' - 'a';
    }
}

#if NETSTANDARD2_0
namespace System.Runtime.CompilerServices
{
    // lets records and init-only properties compile
    internal static class IsExternalInit { }
}

namespace System
{
    // lets `x[^1]` and `x[a..b]` compile; the compiler only needs the types to exist
    internal readonly struct Index : IEquatable<Index>
    {
        private readonly int value;

        public Index(int value, bool fromEnd = false) => this.value = fromEnd ? ~value : value;

        public static Index Start => new(0);
        public static Index End => new(~0);
        public static Index FromStart(int value) => new(value);
        public static Index FromEnd(int value) => new(~value);

        public int Value => value < 0 ? ~value : value;
        public bool IsFromEnd => value < 0;

        public int GetOffset(int length) => IsFromEnd ? value + length + 1 : value;

        public static implicit operator Index(int value) => FromStart(value);
        public bool Equals(Index other) => value == other.value;
        public override bool Equals(object? obj) => obj is Index other && Equals(other);
        public override int GetHashCode() => value;
    }

    internal readonly struct Range : IEquatable<Range>
    {
        public Index Start { get; }
        public Index End { get; }

        public Range(Index start, Index end)
        {
            Start = start;
            End = end;
        }

        public static Range All => new(Index.Start, Index.End);
        public static Range StartAt(Index start) => new(start, Index.End);
        public static Range EndAt(Index end) => new(Index.Start, end);

        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            var start = Start.GetOffset(length);
            var end = End.GetOffset(length);
            if ((uint)end > (uint)length || (uint)start > (uint)end) throw new ArgumentOutOfRangeException(nameof(length));
            return (start, end - start);
        }

        public bool Equals(Range other) => Start.Equals(other.Start) && End.Equals(other.End);
        public override bool Equals(object? obj) => obj is Range other && Equals(other);
        public override int GetHashCode() => Start.GetHashCode() * 31 + End.GetHashCode();
    }
}

namespace RebolParse
{
    /// <summary>Members of <see cref="string"/>, <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/> and friends that .NET Standard 2.0 lacks.</summary>
    internal static class Polyfills
    {
        public static bool Contains(this string text, char value) => text.IndexOf(value) >= 0;
        public static bool StartsWith(this string text, char value) => text.Length > 0 && text[0] == value;
        public static bool EndsWith(this string text, char value) => text.Length > 0 && text[text.Length - 1] == value;

        public static TValue? GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key) =>
            dictionary.TryGetValue(key, out var value) ? value : default;

        public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
        {
            key = pair.Key;
            value = pair.Value;
        }
    }

    /// <summary>Compares objects by reference, as <c>ReferenceEqualityComparer</c> does from .NET 5.</summary>
    internal sealed class ReferenceEqualityComparer : IEqualityComparer<object?>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object? obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
#endif
