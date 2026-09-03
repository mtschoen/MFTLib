namespace MFTLib.Index;

/// <summary>
///     One search over the current snapshot. Every filter that is null is not applied.
///     <see cref="NamePattern" /> is a substring match by default; a pattern containing
///     <c>*</c> or <c>?</c> is treated as a glob that must match the whole name.
/// </summary>
/// <param name="NamePattern">Substring, or a glob when it contains a wildcard. Null matches every name.</param>
/// <param name="CaseSensitive">False folds case with invariant upper-casing, which is what NTFS does.</param>
/// <param name="Under">Restricts the result to this entry's subtree, inclusive.</param>
/// <param name="Directories">True for directories only, false for files only, null for both.</param>
/// <param name="MinimumSize">Inclusive lower bound on the size column.</param>
/// <param name="MaximumSize">Inclusive upper bound on the size column.</param>
/// <param name="ModifiedAfter">Inclusive lower bound on the modified column.</param>
/// <param name="ModifiedBefore">Inclusive upper bound on the modified column.</param>
public sealed record SearchQuery(
    string? NamePattern,
    bool CaseSensitive = false,
    FileEntry? Under = null,
    bool? Directories = null,
    long? MinimumSize = null,
    long? MaximumSize = null,
    DateTime? ModifiedAfter = null,
    DateTime? ModifiedBefore = null);
