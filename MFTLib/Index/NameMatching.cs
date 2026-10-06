namespace MFTLib.Index;

/// <summary>
///     Span-based name comparison against the block's name pool. Case-insensitive comparison
///     folds with invariant upper-casing, which is what NTFS does for the ASCII range and is
///     stable across cultures, unlike the current culture's casing rules.
/// </summary>
internal static class NameMatching
{
    const string UnknownModeMessage = "Unknown name match mode.";

    public static bool EqualsName(ReadOnlySpan<char> left, ReadOnlySpan<char> right, bool caseSensitive)
    {
        return left.Equals(right, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    public static bool ContainsSubstring(ReadOnlySpan<char> name, ReadOnlySpan<char> substring, bool caseSensitive)
    {
        if (substring.IsEmpty)
        {
            return true;
        }

        return name.Contains(substring, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Compares one name with a pattern under the given mode.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode" /> is not a defined mode.</exception>
    public static bool Matches(ReadOnlySpan<char> name, ReadOnlySpan<char> pattern, NameMatchMode mode,
        bool caseSensitive)
    {
        return mode switch
        {
            NameMatchMode.Exact => EqualsName(name, pattern, caseSensitive),
            NameMatchMode.Substring => ContainsSubstring(name, pattern, caseSensitive),
            NameMatchMode.Glob => MatchesGlob(name, pattern, caseSensitive),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, UnknownModeMessage),
        };
    }

    /// <summary>Rejects a query whose mode is undefined before any row is read.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The query's mode is not a defined mode.</exception>
    public static void ThrowIfUndefined(SearchQuery query)
    {
        if (!Enum.IsDefined(query.MatchMode))
        {
            throw new ArgumentOutOfRangeException(nameof(query), UnknownModeMessage);
        }
    }

    /// <summary>
    ///     Iterative wildcard match with backtracking on the last star, so the worst case stays
    ///     linear in practice and no recursion depth depends on the pattern.
    /// </summary>
    public static bool MatchesGlob(ReadOnlySpan<char> name, ReadOnlySpan<char> pattern, bool caseSensitive)
    {
        var nameIndex = 0;
        var patternIndex = 0;
        var starPatternIndex = -1;
        var starNameIndex = 0;

        while (nameIndex < name.Length)
        {
            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starPatternIndex = patternIndex;
                starNameIndex = nameIndex;
                patternIndex++;
            }
            else if (patternIndex < pattern.Length && IsSingleCharacterMatch(name[nameIndex], pattern[patternIndex], caseSensitive))
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

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }

    public static int GetNameHashCode(ReadOnlySpan<char> name, bool caseSensitive)
    {
        return caseSensitive
            ? string.GetHashCode(name, StringComparison.Ordinal)
            : string.GetHashCode(name, StringComparison.OrdinalIgnoreCase);
    }

    static bool IsSingleCharacterMatch(char nameCharacter, char patternCharacter, bool caseSensitive)
    {
        if (patternCharacter == '?')
        {
            return true;
        }

        return caseSensitive
            ? nameCharacter == patternCharacter
            : char.ToUpperInvariant(nameCharacter) == char.ToUpperInvariant(patternCharacter);
    }
}
