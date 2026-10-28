using System.Text;

namespace MFTLib.Index;

/// <summary>
/// The one case fold used by the name hash and by every ignore-case comparison of non-ASCII text: invariant simple
/// uppercase per UTF-16 unit, with <see cref="Rune"/> for valid surrogate pairs. Unpaired surrogates fold to themselves.
/// </summary>
static class NameFolding
{
    internal static char Fold(char unit) => char.ToUpperInvariant(unit);

    /// <summary>Folds <paramref name="source"/> into <paramref name="destination"/> of the same length.</summary>
    internal static void Fold(ReadOnlySpan<char> source, Span<char> destination)
    {
        if (destination.Length != source.Length)
        {
            throw new ArgumentException("The destination must have the source length.", nameof(destination));
        }

        for (var index = 0; index < source.Length;)
        {
            var consumed = FoldNext(source, index, out var first, out var second);
            destination[index] = first;
            if (consumed == 2)
            {
                destination[index + 1] = second;
            }

            index += consumed;
        }
    }

    /// <summary>Folds the unit or valid pair at <paramref name="index"/>; returns the units consumed (1 or 2).</summary>
    internal static int FoldNext(ReadOnlySpan<char> text, int index, out char first, out char second)
    {
        var unit = text[index];
        if (IsPairAt(text, index))
        {
            var upper = Rune.ToUpperInvariant(new Rune(unit, text[index + 1])).Value - 0x10000;
            first = (char)((upper >> 10) + 0xD800);
            second = (char)((upper & 0x3FF) + 0xDC00);
            return 2;
        }

        first = Fold(unit);
        second = default;
        return 1;
    }

    internal static bool IsAscii(ReadOnlySpan<char> text) => Ascii.IsValid(text);

    static bool IsPairAt(ReadOnlySpan<char> text, int index) =>
        index >= 0 && index + 1 < text.Length && char.IsHighSurrogate(text[index]) && char.IsLowSurrogate(text[index + 1]);
}
