namespace MFTLib.Index;

/// <summary>
///     What a journal-driven row mutation did, as delivered on <see cref="FileChange" />. Only
///     <see cref="Renamed" /> ever populates <see cref="FileChange.PreviousPath" />; every other
///     kind leaves it null.
/// </summary>
public enum FileChangeKind
{
    /// <summary>Emitted when a live entry first appears, so consumers can add it to their active view.</summary>
    Created,
    /// <summary>Emitted with a retained tombstone, so consumers should remove the entry from active results.</summary>
    Deleted,
    /// <summary>A row changed names; the previous path is supplied with the change.</summary>
    Renamed,
    /// <summary>An existing row changed without being created, deleted, or renamed.</summary>
    Modified
}
