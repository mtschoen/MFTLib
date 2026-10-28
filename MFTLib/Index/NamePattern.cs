using System.Numerics;
using System.Text;

namespace MFTLib.Index;

/// <summary>
///     A query's name pattern, folded and encoded once and then matched against each interned entry. Ignore-case
///     matching is defined once: a name matches exactly when the ordinal match of <see cref="NameFolding" /> of the
///     name against the fold of the pattern succeeds. Every fast path below agrees with that definition. Case-sensitive
///     matching is ordinal against the original pattern, and a glob is always wildcard matching, never one literal test.
/// </summary>
sealed class NamePattern
{
    const string UnknownModeMessage = "Unknown name match mode.";

    readonly char[] _folded;
    readonly byte[]? _originalAscii;
    readonly byte[]? _foldedAscii;
    readonly NameMatchMode _mode;
    readonly bool _caseSensitive;

    internal NamePattern(string text, NameMatchMode mode, bool caseSensitive)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, UnknownModeMessage);
        }

        Text = text;
        _mode = mode;
        _caseSensitive = caseSensitive;
        _folded = new char[text.Length];
        NameFolding.Fold(text, _folded);
        _originalAscii = Ascii.IsValid(text) ? Encoding.ASCII.GetBytes(text) : null;
        _foldedAscii = NameFolding.IsAscii(_folded) ? Encoding.ASCII.GetBytes(_folded) : null;
    }

    /// <summary>The caller's spelling, which case-sensitive matching and the ordinal-spelling preference compare against.</summary>
    internal string Text { get; }

    /// <summary>Whether <paramref name="name" /> matches. <paramref name="scratch" /> is grown to fold UTF-16 entries.</summary>
    internal bool Matches(NameView name, ref char[] scratch)
    {
        if (name.IsUtf16)
        {
            return MatchesUtf16(name.Utf16Characters, ref scratch);
        }

        return _caseSensitive ? MatchesAsciiOrdinal(name.AsciiBytes) : MatchesAsciiIgnoreCase(name.AsciiBytes);
    }

    bool MatchesUtf16(ReadOnlySpan<char> name, ref char[] scratch)
    {
        if (_caseSensitive)
        {
            return MatchesCharacters(name, Text);
        }

        if (scratch.Length < name.Length)
        {
            scratch = new char[Math.Max(name.Length, scratch.Length * 2)];
        }

        var folded = scratch.AsSpan(0, name.Length);
        NameFolding.Fold(name, folded);
        return MatchesCharacters(folded, _folded);
    }

    bool MatchesCharacters(ReadOnlySpan<char> name, ReadOnlySpan<char> pattern)
    {
        return _mode switch
        {
            NameMatchMode.Exact => name.SequenceEqual(pattern),
            NameMatchMode.Substring => pattern.IsEmpty || name.IndexOf(pattern) >= 0,
            _ => MatchesGlob(name, pattern, uppercaseName: false)
        };
    }

    bool MatchesAsciiOrdinal(ReadOnlySpan<byte> name)
    {
        // A non-ASCII literal can never match an all-ASCII name, whatever the mode.
        if (_originalAscii is not { } pattern)
        {
            return false;
        }

        return _mode switch
        {
            NameMatchMode.Exact => name.SequenceEqual(pattern),
            NameMatchMode.Substring => pattern.Length == 0 || name.IndexOf(pattern) >= 0,
            _ => MatchesGlob(name, pattern, uppercaseName: false)
        };
    }

    bool MatchesAsciiIgnoreCase(ReadOnlySpan<byte> name)
    {
        // The fold of an ASCII name is ASCII, so a pattern whose fold is not ASCII cannot match it.
        if (_foldedAscii is not { } pattern)
        {
            return false;
        }

        return _mode switch
        {
            NameMatchMode.Exact => Ascii.EqualsIgnoreCase(name, pattern),
            NameMatchMode.Substring => ContainsAsciiIgnoreCase(name, pattern),
            _ => MatchesGlob(name, pattern, uppercaseName: true)
        };
    }

    static bool ContainsAsciiIgnoreCase(ReadOnlySpan<byte> name, ReadOnlySpan<byte> foldedPattern)
    {
        if (foldedPattern.IsEmpty)
        {
            return true;
        }

        var upper = foldedPattern[0];
        var lower = upper is >= (byte)'A' and <= (byte)'Z' ? (byte)(upper | 0x20) : upper;
        var remaining = name;
        while (remaining.Length >= foldedPattern.Length)
        {
            var index = lower == upper ? remaining.IndexOf(upper) : remaining.IndexOfAny(upper, lower);
            if (index < 0 || remaining.Length - index < foldedPattern.Length)
            {
                return false;
            }

            if (Ascii.EqualsIgnoreCase(remaining.Slice(index, foldedPattern.Length), foldedPattern))
            {
                return true;
            }

            remaining = remaining[(index + 1)..];
        }

        return false;
    }

    /// <summary>
    ///     Iterative wildcard match with backtracking on the last star, so the worst case stays linear in practice and no
    ///     recursion depth depends on the pattern. <c>?</c> matches one unit and <c>*</c> any run.
    /// </summary>
    static bool MatchesGlob<TUnit>(ReadOnlySpan<TUnit> name, ReadOnlySpan<TUnit> pattern, bool uppercaseName)
        where TUnit : unmanaged, IBinaryInteger<TUnit>
    {
        var star = TUnit.CreateTruncating('*');
        var question = TUnit.CreateTruncating('?');
        var nameIndex = 0;
        var patternIndex = 0;
        var starPatternIndex = -1;
        var starNameIndex = 0;

        while (nameIndex < name.Length)
        {
            if (patternIndex < pattern.Length && pattern[patternIndex] == star)
            {
                starPatternIndex = patternIndex;
                starNameIndex = nameIndex;
                patternIndex++;
            }
            else if (patternIndex < pattern.Length &&
                     (pattern[patternIndex] == question ||
                      (uppercaseName ? UppercaseAscii(name[nameIndex]) : name[nameIndex]) == pattern[patternIndex]))
            {
                nameIndex++;
                patternIndex++;
            }
            else if (starPatternIndex >= 0)
            {
                patternIndex = starPatternIndex + 1;
                starNameIndex++;
                nameIndex = starNameIndex;
            }
            else
            {
                return false;
            }
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == star)
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }

    static TUnit UppercaseAscii<TUnit>(TUnit unit) where TUnit : unmanaged, IBinaryInteger<TUnit>
    {
        return unit >= TUnit.CreateTruncating('a') && unit <= TUnit.CreateTruncating('z')
            ? unit - TUnit.CreateTruncating(0x20)
            : unit;
    }
}
