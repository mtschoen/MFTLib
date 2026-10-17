namespace MFTLib.Index;

/// <summary>Path and capability policy for live volumes, directory walks and dump inventories.</summary>
internal sealed class DriveKind
{
    enum Kind { LiveVolume, DirectoryWalk, Dump }

    static readonly DriveKind LiveVolume = new(Kind.LiveVolume);
    static readonly DriveKind DirectoryWalk = new(Kind.DirectoryWalk);
    static readonly DriveKind Dump = new(Kind.Dump);
    readonly Kind _kind;

    DriveKind(Kind kind) => _kind = kind;

    // Producer values have already been validated at option/block admission.
    internal static DriveKind For(ProducerKind producer, MftIndexSource? source) =>
        producer == ProducerKind.Enumeration ? DirectoryWalk :
        source?.DumpIdentity is not null ? Dump : LiveVolume;

    internal bool CanOpen => _kind != Kind.Dump;
    internal bool CanWatch => _kind == Kind.LiveVolume;
    internal bool HasJournalSettings => _kind != Kind.Dump;
    internal bool RequiresExistingRoot => _kind != Kind.Dump;
    internal bool IsCaseSensitive => _kind == Kind.DirectoryWalk && !OperatingSystem.IsWindows();
    bool UsesBackslashSeparators => _kind == Kind.Dump || OperatingSystem.IsWindows();
    internal ReadOnlySpan<char> Separators => UsesBackslashSeparators ? "\\/" : "/";
    internal StringComparison Comparison => IsCaseSensitive
        ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    ///     The single place a caller-supplied root is normalized, so path rendering and path
    ///     lookup can never disagree about what this block's root is. A bare drive specifier such
    ///     as <c>C:</c> is drive-relative on Windows: joined with a name chain it yields
    ///     <c>C:Windows</c>, which resolves against that drive's per-process current directory
    ///     rather than its root, and which no lookup would then accept because the character after
    ///     the root is a name character instead of a separator. A separator is appended so the
    ///     result is rooted. Resolving through <c>Path.GetFullPath</c> would be wrong here, since
    ///     that resolves against the current process directory rather than the drive's root.
    /// </summary>
    internal string? NormalizeRoot(string? rootDirectoryPath)
    {
        if (_kind == Kind.Dump || rootDirectoryPath is not { Length: 2 } specifier ||
            specifier[1] != ':' || !char.IsAsciiLetter(specifier[0]))
        {
            return rootDirectoryPath;
        }

        return specifier + Path.DirectorySeparatorChar;
    }

    internal string? ToMatchablePrefix(string? rootDirectoryPath)
    {
        if (_kind == Kind.Dump)
        {
            return MftDumpPaths.ToMatchablePrefix(rootDirectoryPath);
        }

        if (rootDirectoryPath is not { Length: > 0 } root)
        {
            return null;
        }

        return OperatingSystem.IsWindows()
            ? root.Replace('\\', '/').TrimEnd('/')
            : root.TrimEnd(Path.DirectorySeparatorChar);
    }

    internal string ComparablePath(string path, ref string? foldedPath) =>
        UsesBackslashSeparators ? foldedPath ??= MftDumpPaths.Fold(path) : path;

    internal bool IsSeparator(char character) => Separators.Contains(character);

    internal string JoinPath(string[] components) => _kind == Kind.Dump
        ? MftDumpPaths.Join(components[0], components.Skip(1).ToArray())
        : Path.Combine(components);

    internal static FileIndexOptions PrepareOpenOptions(FileIndexOptions options)
    {
        if (options.MftSource?.DumpIdentity is { } identity)
        {
            MftDumpSourceIdentity.ValidateOptions(options);
            return options with { Drives = [identity.VirtualDrive], CacheMode = CacheMode.Disabled };
        }

        var drives = FileIndexOptions.ValidateDrives(options);
        if (options.CacheMode is { } mode && !Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Unknown cache mode.");
        }

        return options with { Drives = drives, CacheMode = options.CacheMode ?? CacheMode.Persistent };
    }
}
