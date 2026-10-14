namespace MFTLib.Index;

/// <summary>
///     What a journal event does to the scanned size and last-write observations of an existing row. A journal
///     record carries neither value, so an event that can have changed one invalidates it instead of replacing it
///     with the record's own time.
/// </summary>
internal enum RowFreshness
{
    /// <summary>The event cannot have changed size or last-write time: both observations are kept.</summary>
    Unchanged,

    /// <summary>The event changed basic information: the last-write observation becomes unknown, the size is kept.</summary>
    LastWriteInvalidated,

    /// <summary>The event changed file content: both the size and last-write observations become unknown.</summary>
    ContentInvalidated
}
