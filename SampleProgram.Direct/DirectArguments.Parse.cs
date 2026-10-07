using System.Diagnostics.CodeAnalysis;

namespace SampleProgram.Direct;

// The command-line parser: the verb, the drive and the verb's flags.
internal sealed partial record DirectArguments
{
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
            Name = reader.Text("--name"),
            Exact = reader.Flag("--exact"),
            CaseSensitive = reader.Flag("--case-sensitive"),
            Under = reader.Text("--under"),
            Directories = directories ? true : files ? false : null,
            MinimumSize = reader.Number("--min-size"),
            MaximumSize = reader.Number("--max-size"),
            After = reader.Date("--after"),
            Before = reader.Date("--before"),
            Stream = reader.Flag("--stream"),
            IncludeFreed = reader.Flag("--include-freed"),
            Limit = (int?)reader.Number("--limit") ?? DefaultLimit,
            Path = reader.Text("--path"),
            Depth = (int?)reader.Number("--depth") ?? DefaultDepth,
            Count = (int?)reader.Number("--count") ?? DefaultCount
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
