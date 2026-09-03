namespace MFTLib.Index;

/// <summary>
///     One applied change. The entry is a live handle onto the mutated row, so a Recent Changes
///     feed can still name a deleted file: the row keeps its name and reads as deleted.
/// </summary>
public sealed record FileChange(FileChangeKind Kind, FileEntry Entry, string? PreviousName = null);
