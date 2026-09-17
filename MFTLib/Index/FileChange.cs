namespace MFTLib.Index;

/// <summary>
///     One applied journal change. <paramref name="Entry" /> is a live handle whose values are
///     current, not frozen, so its own <c>Path</c> reflects every later entry in the same batch.
///     <paramref name="Path" /> is the path this change happened at, captured at the mutation,
///     and <paramref name="PreviousPath" /> is the full path a rename or move came from.
///     <paramref name="Timestamp" /> is the UTC timestamp of the USN journal record that
///     produced this change, captured at the mutation like <paramref name="Path" />: it does
///     not move when a later record in the same batch (including a coalesced close record)
///     restamps the row, and a delete's timestamp survives the tombstone, so it is the when of
///     the change rather than the row's current <see cref="FileEntry.Modified" />.
/// </summary>
public sealed record FileChange(FileChangeKind Kind, FileEntry Entry, string Path, DateTime Timestamp,
    string? PreviousPath = null);
