namespace MFTLib.Index;

/// <summary>All entries across every current drive block that share one name.</summary>
public sealed record DuplicateGroup(string Name, IReadOnlyList<FileEntry> Entries);
