namespace MFTLib.Index;

/// <summary>
///     One applied journal change. <see cref="Entry" /> is a live handle whose values are
///     current, not frozen, so its own <c>Path</c> reflects every later entry in the same batch.
///     <see cref="Path" /> is the path this change happened at, captured at the mutation,
///     and <see cref="PreviousPath" /> is the full path a rename or move came from.
///     <see cref="Timestamp" /> is the UTC timestamp of the USN journal record that
///     produced this change, captured at the mutation like <see cref="Path" />: it does
///     not move when a later record in the same batch (including a coalesced close record)
///     restamps the row, and a delete's timestamp survives the tombstone, so it is the when of
///     the change rather than the row's current <see cref="FileEntry.Modified" />.
/// </summary>
public sealed record FileChange
{
    /// <summary>Creates one applied change.</summary>
    /// <param name="kind">What happened to the entry.</param>
    /// <param name="entry">A live handle to the changed entry.</param>
    /// <param name="path">The path this change happened at.</param>
    /// <param name="timestamp">The UTC timestamp of the journal record that produced this change.</param>
    /// <param name="previousPath">The full path a rename or move came from; otherwise null.</param>
    internal FileChange(FileChangeKind kind, FileEntry entry, string path, DateTime timestamp,
        string? previousPath = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        Kind = kind;
        Entry = entry;
        Path = path;
        Timestamp = timestamp;
        PreviousPath = previousPath;
    }

    /// <summary>What happened to the entry.</summary>
    public FileChangeKind Kind { get; init; }

    /// <summary>A live handle to the changed entry; its values are current, not frozen.</summary>
    public FileEntry Entry { get; init; }

    /// <summary>The path this change happened at, captured at the mutation.</summary>
    public string Path { get; init; }

    /// <summary>When the change happened, from the journal record rather than the clock at delivery; always UTC.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>The full path a rename or move came from; otherwise null.</summary>
    public string? PreviousPath { get; init; }
}
