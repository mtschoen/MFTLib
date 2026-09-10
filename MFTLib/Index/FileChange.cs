namespace MFTLib.Index;

/// <summary>
///     One applied journal change. <paramref name="Entry" /> is a live handle whose values are
///     current, not frozen, so its own <c>Path</c> reflects every later entry in the same batch.
///     <paramref name="Path" /> is the path this change happened at, captured at the mutation,
///     and <paramref name="PreviousPath" /> is the full path a rename or move came from.
/// </summary>
public sealed record FileChange(FileChangeKind Kind, FileEntry Entry, string Path, string? PreviousPath = null);
