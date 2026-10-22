namespace MFTLib.Index;

/// <summary>
///     How enumeration drives walk their tree. A pruned directory is written as a row, like any other
///     entry, but its children are not enumerated. The drive's root is never pruned. Hidden and system
///     entries are walked unless a rule prunes them, and a reparse point is always recorded and never
///     entered. MFT drives ignore these options. A cached enumeration block records the options that
///     shaped it, so a warm start under different options discards the block and scans again.
/// </summary>
public sealed record DirectoryWalkOptions
{
    /// <summary>
    ///     Directory names that are recorded but not entered, compared with the drive's path comparison:
    ///     ignoring case on Windows, ordinal elsewhere. <see cref="FileIndex.OpenAsync" /> rejects a null
    ///     collection and a null, blank, separator-containing or unpaired-surrogate name.
    /// </summary>
    public IReadOnlyCollection<string> PrunedDirectoryNames { get; init; } = [];

    /// <summary>
    ///     Fully qualified directory paths that are recorded but not entered. A directory is pruned when its
    ///     full path equals one of these after <see cref="Path.GetFullPath(string)" /> and
    ///     <see cref="Path.TrimEndingDirectorySeparator(string)" />, so everything beneath it is skipped too.
    ///     A path outside a drive's root has no effect on that drive. <see cref="FileIndex.OpenAsync" />
    ///     rejects a null collection and a null, blank, relative or unpaired-surrogate path.
    /// </summary>
    public IReadOnlyCollection<string> PrunedDirectoryPaths { get; init; } = [];

    /// <summary>
    ///     A directory carrying any of these attributes is recorded but not entered. On Unix, .NET reports
    ///     <see cref="FileAttributes.Hidden" /> for dot names. <see cref="FileIndex.OpenAsync" /> rejects
    ///     <see cref="FileAttributes.Directory" />, which would prune every directory, and
    ///     <see cref="FileAttributes.ReparsePoint" />, which is never entered already.
    /// </summary>
    public FileAttributes PrunedDirectoryAttributes { get; init; }

    /// <summary>When true, a directory whose name starts with '.' is recorded but not entered, on every platform.</summary>
    public bool PruneDotDirectories { get; init; }

    /// <summary>The comparison every name and path rule uses: ignoring case on Windows, ordinal elsewhere.</summary>
    internal static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    ///     Rejects options the walk cannot honour and returns a private copy whose paths are normalized, so the
    ///     index never re-evaluates or shares the caller's collections.
    /// </summary>
    /// <param name="walk">The options from <see cref="FileIndexOptions.DirectoryWalk" />.</param>
    /// <returns>The admitted copy.</returns>
    /// <exception cref="ArgumentException">An option is null or invalid.</exception>
    internal static DirectoryWalkOptions Admit(DirectoryWalkOptions? walk)
    {
        if (walk is null)
        {
            throw new ArgumentException("FileIndexOptions.DirectoryWalk must not be null.", nameof(walk));
        }

        const FileAttributes refused = FileAttributes.Directory | FileAttributes.ReparsePoint;
        if ((walk.PrunedDirectoryAttributes & refused) != 0)
        {
            throw new ArgumentException(
                "DirectoryWalkOptions.PrunedDirectoryAttributes must not contain Directory or ReparsePoint.", nameof(walk));
        }

        return walk with
        {
            PrunedDirectoryNames = AdmitNames(walk.PrunedDirectoryNames),
            PrunedDirectoryPaths = AdmitPaths(walk.PrunedDirectoryPaths)
        };
    }

    /// <summary>
    ///     The admitted pruned paths that can match a directory under <paramref name="rootDirectory" />: those
    ///     equal to it or beneath it, by the drive comparison.
    /// </summary>
    /// <param name="rootDirectory">The drive's root directory.</param>
    /// <returns>The paths that can affect the drive, in the admitted order.</returns>
    internal string[] PathsWithin(string rootDirectory)
    {
        var root = NormalizePath(rootDirectory);
        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return [.. PrunedDirectoryPaths.Where(path => string.Equals(path, root, comparison) || path.StartsWith(rootWithSeparator, comparison))];
    }

    /// <summary>
    ///     Resolves <paramref name="path" /> against the current directory and drops a trailing separator other than a
    ///     volume root's, so two spellings of one directory compare equal.
    /// </summary>
    internal static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    static string[] AdmitNames(IReadOnlyCollection<string>? names)
    {
        if (names is null)
        {
            throw new ArgumentException("DirectoryWalkOptions.PrunedDirectoryNames must not be null.", nameof(names));
        }

        var copy = new List<string>(names.Count);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(SeparatorCharacters) >= 0)
            {
                throw new ArgumentException(
                    $"DirectoryWalkOptions.PrunedDirectoryNames entry '{name}' must be a nonblank name without a directory separator.",
                    nameof(names));
            }

            if (!WellFormedText.IsWellFormed(name))
            {
                throw new ArgumentException(
                    "DirectoryWalkOptions.PrunedDirectoryNames must not contain an entry with an unpaired surrogate.",
                    nameof(names));
            }

            copy.Add(name);
        }

        return [.. copy];
    }

    static string[] AdmitPaths(IReadOnlyCollection<string>? paths)
    {
        if (paths is null)
        {
            throw new ArgumentException("DirectoryWalkOptions.PrunedDirectoryPaths must not be null.", nameof(paths));
        }

        var copy = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                throw new ArgumentException(
                    $"DirectoryWalkOptions.PrunedDirectoryPaths entry '{path}' must be a fully qualified path.", nameof(paths));
            }

            if (!WellFormedText.IsWellFormed(path))
            {
                throw new ArgumentException(
                    "DirectoryWalkOptions.PrunedDirectoryPaths must not contain an entry with an unpaired surrogate.",
                    nameof(paths));
            }

            copy.Add(NormalizePath(path));
        }

        return [.. copy];
    }

    static readonly char[] SeparatorCharacters =
        Path.DirectorySeparatorChar == Path.AltDirectorySeparatorChar
            ? [Path.DirectorySeparatorChar]
            : [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
}
