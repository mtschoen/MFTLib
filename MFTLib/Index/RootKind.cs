namespace MFTLib.Index;

/// <summary>Path and capability policy for live volumes, directory walks and dump inventories.</summary>
sealed class RootKind
{
    enum Kind { LiveVolume, DirectoryWalk, Dump }

    static readonly RootKind LiveVolume = new(Kind.LiveVolume);
    internal static readonly RootKind DirectoryWalk = new(Kind.DirectoryWalk);
    static readonly RootKind Dump = new(Kind.Dump);
    readonly Kind _kind;

    internal static bool? _directoryCaseSensitiveOverride;

    RootKind(Kind kind) => _kind = kind;

    // Producer values have already been validated at option/block admission.
    internal static RootKind For(ProducerKind producer, MftIndexSource? source) =>
        For(producer, source?.DumpIdentity is not null);

    internal static RootKind For(ProducerKind producer, bool isDump) =>
        producer == ProducerKind.Enumeration ? DirectoryWalk :
        isDump ? Dump : LiveVolume;

    internal bool CanOpen => _kind != Kind.Dump;
    internal bool CanWatch => _kind == Kind.LiveVolume;
    internal bool HasJournalSettings => _kind != Kind.Dump;
    internal bool RequiresExistingRoot => _kind != Kind.Dump;
    internal bool IsCaseSensitive => _kind == Kind.DirectoryWalk &&
        (_directoryCaseSensitiveOverride ?? !OperatingSystem.IsWindows());
    bool UsesBackslashSeparators => _kind == Kind.Dump || OperatingSystem.IsWindows();
    internal ReadOnlySpan<char> Separators => UsesBackslashSeparators ? "\\/" : "/";
    internal StringComparison Comparison => IsCaseSensitive
        ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    ///     The single place a caller-supplied root is normalized, so path rendering and path
    ///     lookup can never disagree about what this block's root is. A bare root specifier such
    ///     as <c>C:</c> is root-relative on Windows: joined with a name chain it yields
    ///     <c>C:Windows</c>, which resolves against that root's per-process current directory
    ///     rather than its root, and which no lookup would then accept because the character after
    ///     the root is a name character instead of a separator. A separator is appended so the
    ///     result is rooted. Resolving through <c>Path.GetFullPath</c> would be wrong here, since
    ///     that resolves against the current process directory rather than the root's directory.
    /// </summary>
    internal string? NormalizeRoot(string? rootDirectoryPath)
    {
        if (_kind == Kind.Dump || rootDirectoryPath is not [_, ':'] specifier ||
            !char.IsAsciiLetter(specifier[0]))
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
        if (!Enum.IsDefined(options.Producer))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Producer,
                "Unknown producer selection.");
        }

        if (options.MftSource?.DumpIdentity is { } identity)
        {
            MftDumpSourceIdentity.ValidateOptions(options);
            return options with
            {
                Root = identity.Volume,
                CacheMode = CacheMode.Disabled,
                DirectoryWalk = DirectoryWalkOptions.Admit(options.DirectoryWalk)
            };
        }

        var root = FileIndexOptions.ValidateRoot(options);
        var directoryWalk = DirectoryWalkOptions.Admit(options.DirectoryWalk);
        if (options.CacheMode is { } mode && !Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Unknown cache mode.");
        }

        return options with
        {
            Root = root,
            CacheMode = options.CacheMode ?? CacheMode.Persistent,
            DirectoryWalk = directoryWalk
        };
    }
}
