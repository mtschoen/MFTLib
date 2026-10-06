namespace MFTLib.Index;

/// <summary>
///     One search over the current snapshot. Every filter that is null is not applied.
///     <see cref="MatchMode" /> says how <see cref="NamePattern" /> is compared with a name.
/// </summary>
/// <param name="NamePattern">
///     The text to match, compared as <paramref name="MatchMode" /> says. Null disables name filtering.
///     An empty pattern matches only an empty name when exact or glob, and every name when substring.
/// </param>
/// <param name="MatchMode">
///     Exact compares the whole name, substring looks inside it and glob matches the whole name against
///     <c>*</c> and <c>?</c>. An undefined value makes the search throw <see cref="ArgumentOutOfRangeException" />.
/// </param>
/// <param name="CaseSensitive">False folds case with invariant upper-casing, which is what NTFS does.</param>
/// <param name="Under">Restricts the result to this entry's subtree, inclusive.</param>
/// <param name="Directories">True for directories only, false for files only, null for both.</param>
/// <param name="MinimumSize">Inclusive lower bound on the size column.</param>
/// <param name="MaximumSize">Inclusive upper bound on the size column.</param>
/// <param name="ModifiedAfter">Inclusive lower bound on the modified column.</param>
/// <param name="ModifiedBefore">Inclusive upper bound on the modified column.</param>
public sealed record SearchQuery(
    string? NamePattern,
    NameMatchMode MatchMode = NameMatchMode.Substring,
    bool CaseSensitive = false,
    FileEntry? Under = null,
    bool? Directories = null,
    long? MinimumSize = null,
    long? MaximumSize = null,
    DateTime? ModifiedAfter = null,
    DateTime? ModifiedBefore = null);
