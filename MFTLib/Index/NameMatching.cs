namespace MFTLib.Index;

/// <summary>
///     Span-based name comparison against the block's name pool. Case-insensitive comparison
///     folds with invariant upper-casing, which is what NTFS does for the ASCII range and is
///     stable across cultures, unlike the current culture's casing rules.
/// </summary>
public static class NameMatching
{
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

    public static bool IsGlobPattern(ReadOnlySpan<char> pattern)
    {
        return pattern.IndexOfAny('*', '?') >= 0;
    }

    /// <summary>
    ///     Routes a caller-supplied pattern: anything containing a wildcard is a glob and must
    ///     match the whole name, anything else is a substring match. This is the rule the
    ///     public <c>SearchQuery.NamePattern</c> documents.
    /// </summary>
    public static bool Matches(ReadOnlySpan<char> name, ReadOnlySpan<char> pattern, bool caseSensitive)
    {
        return IsGlobPattern(pattern)
            ? MatchesGlob(name, pattern, caseSensitive)
            : ContainsSubstring(name, pattern, caseSensitive);
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
