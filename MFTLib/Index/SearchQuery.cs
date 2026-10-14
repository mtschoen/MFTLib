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
/// <param name="CaseSensitive">False folds case with invariant upper-casing, which is what NTFS does for the ASCII range.</param>
/// <param name="Subtree">Restricts the result to the subtree below this entry, the entry itself included.</param>
/// <param name="IsDirectory">True returns only directories, false returns only files, null applies no filter.</param>
/// <param name="SizeMinimum">
///     Inclusive lower bound on the size in bytes. A row whose size is unknown is excluded even when its stored
///     size satisfies the comparison.
/// </param>
/// <param name="SizeMaximum">
///     Inclusive upper bound on the size in bytes. A row whose size is unknown is excluded even when its stored
///     size satisfies the comparison.
/// </param>
/// <param name="LastWriteTimeMinimum">
///     Inclusive lower bound on <see cref="FileEntry.LastWriteTime" />, which is UTC. Supply a UTC
///     <see cref="DateTime" />: its raw ticks are compared directly, with no <see cref="DateTime.Kind" />
///     conversion. A Local-kind value is compared as if its ticks were UTC. A row whose UTC last-write
///     time equals the supplied UTC bound matches.
/// </param>
/// <param name="LastWriteTimeMaximum">
///     Inclusive upper bound on <see cref="FileEntry.LastWriteTime" />, which is UTC. Supply a UTC
///     <see cref="DateTime" />: its raw ticks are compared directly, with no <see cref="DateTime.Kind" />
///     conversion. A Local-kind value is compared as if its ticks were UTC. A row whose UTC last-write
///     time equals the supplied UTC bound matches.
/// </param>
/// <param name="IncludeDeleted">
///     True also returns deleted rows: those the journal deleted and those a scan imported as freed records.
///     Every other filter still applies. The default returns live rows only.
/// </param>
public sealed record SearchQuery(
    string? NamePattern,
    NameMatchMode MatchMode = NameMatchMode.Substring,
    bool CaseSensitive = false,
    FileEntry? Subtree = null,
    bool? IsDirectory = null,
    long? SizeMinimum = null,
    long? SizeMaximum = null,
    DateTime? LastWriteTimeMinimum = null,
    DateTime? LastWriteTimeMaximum = null,
    bool IncludeDeleted = false);
