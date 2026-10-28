namespace MFTLib.Index;

/// <summary>
///     Name comparison helpers outside the query path. Query matching goes through <see cref="NamePattern" />, which
///     folds with <see cref="NameFolding" />.
/// </summary>
static class NameMatching
{
    const string UnknownModeMessage = "Unknown name match mode.";

    public static bool EqualsName(ReadOnlySpan<char> left, ReadOnlySpan<char> right, bool caseSensitive)
    {
        return left.Equals(right, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
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
}
