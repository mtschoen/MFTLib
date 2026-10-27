namespace MFTLib.Index;

/// <summary>
///     Where an enumeration walk starts and which root key its entries report. It is a
///     display and lookup key, not a device identifier.
/// </summary>
sealed record EnumerationProducerOptions
{
    /// <summary>Directory from which recursive enumeration begins.</summary>
    public required string RootDirectory { get; init; }

    /// <summary>Root key reported by entries produced by this enumeration.</summary>
    public required RootKey Root { get; init; }

    /// <summary>
    ///     Null writes every entry. Empty writes directories only. A nonempty set also writes files whose
    ///     names match by the root's comparison: ignoring case on Windows, ordinal elsewhere. The root row
    ///     is always written.
    /// </summary>
    public IReadOnlyCollection<string>? RetainedFileNames { get; init; }

    /// <summary>The admitted walk options: which directories are recorded but not entered.</summary>
    public DirectoryWalkOptions DirectoryWalk { get; init; } = new();
}
