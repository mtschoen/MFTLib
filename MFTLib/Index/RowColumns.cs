namespace MFTLib.Index;

/// <summary>
///     Every column of a row except the name, grouped so <see cref="BlockWriter.TryWriteRow" />
///     takes one descriptor instead of a long parameter list. The name stays a separate
///     <c>ReadOnlySpan&lt;char&gt;</c> argument because it is appended to the name pool rather
///     than stored in the row. This is a readonly struct passed by reference, so grouping the
///     columns costs no allocation on the per-row write path.
/// </summary>
/// <param name="ParentRow">Row index of the parent directory. The volume root points at itself.</param>
/// <param name="Flags">Per-row state, including the in-use bit.</param>
/// <param name="Attributes">Raw NTFS file attributes.</param>
/// <param name="Size">Size in bytes. Zero for directories and for size-unknown rows.</param>
/// <param name="ModifiedTicks">Last modified time as UTC ticks.</param>
public readonly record struct RowColumns(
    uint ParentRow,
    RowFlags Flags,
    uint Attributes,
    long Size,
    long ModifiedTicks);
