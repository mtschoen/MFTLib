using System.Diagnostics.CodeAnalysis;

namespace SampleProgram.Direct;

internal enum DirectVerb { Scan, Search, Tree, Open, Largest, DuplicateNames }

/// <summary>Where the index gets its MFT: the live volume of this elevated process, or a saved MFT file.</summary>
internal enum SourceKind { Local, Dump }

/// <summary>The parsed command line: a verb, one drive letter, the source and the verb's flags.</summary>
internal sealed record DirectArguments(DirectVerb Verb, string Drive)
{
    internal const int DefaultLimit = 50;
    internal const int DefaultCount = 10;
    internal const int DefaultDepth = 2;

    static readonly Dictionary<string, DirectVerb> Verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["scan"] = DirectVerb.Scan, ["search"] = DirectVerb.Search, ["tree"] = DirectVerb.Tree,
        ["open"] = DirectVerb.Open, ["largest"] = DirectVerb.Largest, ["duplicate-names"] = DirectVerb.DuplicateNames
    };

    internal static string Usage => string.Join(Environment.NewLine,
        "Usage: SampleProgram.Direct VERB DRIVE [--source local|dump --dump-file PATH]",
        "  search DRIVE [--name P] [--exact] [--case-sensitive] [--under PATH] [--directories|--files]",
        "               [--min-size N] [--max-size N] [--after D] [--before D] [--stream] [--limit N] [--include-freed]",
        "  tree DRIVE [--path P] [--depth N]    open DRIVE --path P    largest DRIVE [--count N] [--under P]",
        "  duplicate-names DRIVE [--count N]    scan DRIVE");

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

    internal static bool TryParse(string[] arguments, [NotNullWhen(true)] out DirectArguments? parsed,
        [NotNullWhen(false)] out string? error)
    {
        parsed = null;
        if (arguments.Length == 0 || !Verbs.TryGetValue(arguments[0], out var verb))
        {
            error = arguments.Length == 0 ? "A verb is required." : $"Unknown verb {arguments[0]}.";
            return false;
        }

        var reader = new ArgumentReader(arguments.Skip(1));
        var source = reader.Text("--source") ?? "local";
        var directories = reader.Flag("--directories");
        var files = reader.Flag("--files");
        var candidate = new DirectArguments(verb, string.Empty)
        {
            Source = source.Equals("dump", StringComparison.OrdinalIgnoreCase) ? SourceKind.Dump : SourceKind.Local,
            DumpFile = reader.Text("--dump-file"),
            Name = reader.Text("--name"), Exact = reader.Flag("--exact"), CaseSensitive = reader.Flag("--case-sensitive"),
            Under = reader.Text("--under"), Directories = directories ? true : files ? false : null,
            MinimumSize = reader.Number("--min-size"), MaximumSize = reader.Number("--max-size"),
            After = reader.Date("--after"), Before = reader.Date("--before"),
            Stream = reader.Flag("--stream"), IncludeFreed = reader.Flag("--include-freed"),
            Limit = (int?)reader.Number("--limit") ?? DefaultLimit, Path = reader.Text("--path"),
            Depth = (int?)reader.Number("--depth") ?? DefaultDepth, Count = (int?)reader.Number("--count") ?? DefaultCount
        };
        var positionals = reader.Positionals();
        error = reader.Error ?? Validate(candidate, source, directories && files, positionals);
        if (error is not null)
        {
            return false;
        }

        parsed = candidate with { Drive = positionals[0] };
        return true;
    }

    static string? Validate(DirectArguments candidate, string source, bool bothKinds, IReadOnlyList<string> positionals)
    {
        return positionals switch
        {
            not { Count: 1 } => "Expected exactly one drive letter.",
            _ when !char.IsAsciiLetter(positionals[0][0]) => $"'{positionals[0]}' is not a drive letter.",
            _ when candidate.Source is SourceKind.Local && !source.Equals("local", StringComparison.OrdinalIgnoreCase) => $"Unknown source {source}.",
            _ when candidate.Source is SourceKind.Dump && candidate.DumpFile is null => "--source dump needs --dump-file PATH.",
            _ when candidate.Source is SourceKind.Local && candidate.DumpFile is not null => "--dump-file needs --source dump.",
            _ when candidate.IncludeFreed && candidate.Source is SourceKind.Dump => "--include-freed cannot be used with --source dump: a dump never yields freed rows.",
            _ when bothKinds => "--directories and --files exclude each other.",
            _ when candidate is { Verb: DirectVerb.Open, Path: null } => "open needs --path P.",
            _ => null
        };
    }
}
