namespace MFTLib.Index;

/// <summary>
///     What a journal-driven row mutation did, as delivered on <see cref="FileChange" />. Only
///     <see cref="Renamed" /> ever populates <see cref="FileChange.PreviousName" />; every other
///     kind leaves it null.
/// </summary>
public enum FileChangeKind
{
    Created,
    Deleted,
    Renamed,
    Modified
}
