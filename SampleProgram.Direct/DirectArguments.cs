namespace SampleProgram.Direct;

internal enum DirectVerb { Scan, Search, Tree, Open, Largest, DuplicateNames }

/// <summary>Where the index gets its MFT: the live volume of this elevated process, or a saved MFT file.</summary>
internal enum SourceKind { Local, Dump }

/// <summary>The parsed command line: a verb, one drive letter, the source and the verb's flags.</summary>
internal sealed partial record DirectArguments(DirectVerb Verb, string Drive)
{
    internal const int DefaultLimit = 50;
    internal const int DefaultCount = 10;
    internal const int DefaultDepth = 2;

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
        "  tree DRIVE [--path P] [--depth N]    open DRIVE --path P    largest DRIVE [--count N] [--under P]",
        "  duplicate-names DRIVE [--count N]    scan DRIVE",
        "  A flag not listed for a verb is accepted and ignored.");

    internal SourceKind Source { get; init; } = SourceKind.Local;
    internal string? DumpFile { get; init; }
    internal string? Name { get; init; }
    internal bool Exact { get; init; }
    internal bool CaseSensitive { get; init; }
    internal string? Under { get; init; }
    internal bool? Directories { get; init; }
    internal long? MinimumSize { get; init; }
    internal long? MaximumSize { get; init; }
    internal DateTime? After { get; init; }
    internal DateTime? Before { get; init; }
    internal bool Stream { get; init; }
    internal int Limit { get; init; } = DefaultLimit;
    internal bool IncludeFreed { get; init; }
    internal string? Path { get; init; }
    internal int Depth { get; init; } = DefaultDepth;
    internal int Count { get; init; } = DefaultCount;

    /// <summary>A local scan reads the volume itself, so the process must be elevated; a dump file needs nothing.</summary>
    internal bool RequiresElevation => Source is SourceKind.Local;
}
