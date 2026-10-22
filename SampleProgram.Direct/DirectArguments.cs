namespace SampleProgram.Direct;

internal enum DirectVerb { Scan, Search, Tree, Open, Largest, DuplicateNames }

/// <summary>Where the index gets its MFT: the live volume of this elevated process, or a saved MFT file.</summary>
internal enum SourceKind { Local, Dump }

/// <summary>The parsed command line: a verb, one drive letter, the source and the verb's flags.</summary>
internal sealed partial record DirectArguments(DirectVerb Verb, string Drive)
{
    internal const int DefaultLimit = 50;
    const int DefaultCount = 10;
    const int DefaultDepth = 2;

    static readonly Dictionary<string, DirectVerb> Verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["scan"] = DirectVerb.Scan,
        ["search"] = DirectVerb.Search,
        ["tree"] = DirectVerb.Tree,
        ["open"] = DirectVerb.Open,
        ["largest"] = DirectVerb.Largest,
        ["duplicate-names"] = DirectVerb.DuplicateNames
    };

    internal static string Usage => string.Join(Environment.NewLine,
        "Usage: SampleProgram.Direct VERB DRIVE [--source local|dump --dump-file PATH]",
        "  search DRIVE [--name P] [--exact] [--case-sensitive] [--under PATH] [--directories|--files]",
        "               [--min-size N] [--max-size N] [--after D] [--before D] [--stream] [--limit N] [--include-freed]",
        "  --after and --before are inclusive UTC bounds; use a timestamp ending in Z, such as 2026-08-01T00:00:00Z. No Kind conversion is performed.",
        "  tree DRIVE [--path P] [--depth N]    open DRIVE --path P    largest DRIVE [--count N] [--under P]",
        "  duplicate-names DRIVE [--count N]    scan DRIVE",
        "  exclude-defender                     add this executable to the Windows Defender process exclusions (needs elevation; takes no drive)",
        "  --source and --dump-file apply to every verb; --include-freed applies to every verb but is a parse error with --source dump; any other flag a verb does not list is refused.");

    // The flags every verb reads (the source and the scan it asks for), and the ones each verb adds.
    static readonly string[] CommonFlags = ["--source", "--dump-file", "--include-freed"];

    static readonly Dictionary<DirectVerb, string[]> VerbFlags = new()
    {
        [DirectVerb.Scan] = [],
        [DirectVerb.Search] = ["--name", "--exact", "--case-sensitive", "--under", "--directories", "--files", "--min-size", "--max-size", "--after", "--before", "--stream", "--limit"],
        [DirectVerb.Tree] = ["--path", "--depth"],
        [DirectVerb.Open] = ["--path"],
        [DirectVerb.Largest] = ["--count", "--under"],
        [DirectVerb.DuplicateNames] = ["--count"]
    };

    internal SourceKind Source { get; private init; } = SourceKind.Local;
    internal string? DumpFile { get; private init; }
    internal string? Name { get; private init; }
    internal bool Exact { get; private init; }
    internal bool CaseSensitive { get; private init; }
    internal string? Under { get; private init; }
    internal bool? Directories { get; private init; }
    internal long? MinimumSize { get; private init; }
    internal long? MaximumSize { get; private init; }
    internal DateTime? After { get; private init; }
    internal DateTime? Before { get; private init; }
    internal bool Stream { get; private init; }
    internal int Limit { get; private init; } = DefaultLimit;
    internal bool IncludeFreed { get; private init; }
    internal string? Path { get; private init; }
    internal int Depth { get; private init; } = DefaultDepth;
    internal int Count { get; private init; } = DefaultCount;

    /// <summary>A local scan reads the volume itself, so the process must be elevated; a dump file needs nothing.</summary>
    internal bool RequiresElevation => Source is SourceKind.Local;
}
