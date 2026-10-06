namespace MFTLib.Index;

/// <summary>All entries across every current drive block that share one name.</summary>
public sealed record DuplicateGroup
{
    /// <summary>Creates a group of same-named entries.</summary>
    /// <param name="name">The shared name.</param>
    /// <param name="entries">Every entry carrying the name.</param>
    internal DuplicateGroup(string name, IReadOnlyList<FileEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(entries);
        Name = name;
        Entries = entries;
    }

    /// <summary>The file name every entry in this group carries, compared as the index compares names.</summary>
    public string Name { get; init; }

    /// <summary>Every entry carrying the name.</summary>
    public IReadOnlyList<FileEntry> Entries { get; init; }
}
