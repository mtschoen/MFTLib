using System.Diagnostics.CodeAnalysis;

namespace SampleProgram.Watch;

// The command-line parser: the verb name, then the drives and the verb's flags.
sealed partial record WatchArguments
{
    internal static bool TryParse(string[] arguments, [NotNullWhen(true)] out WatchArguments? parsed,
        [NotNullWhen(false)] out string? error)
    {
        parsed = null;
        var verb = WatchVerb.Watch;
        var start = 0;
        if (arguments.Length > 0 && WatchVerbs.Names.TryGetValue(arguments[0], out var named))
        {
            verb = named;
            start = 1;
        }

        var reader = new ArgumentReader(arguments.Skip(start));
        var keep = reader.Text("--keep-name");
        var directoriesOnly = reader.Flag("--directories-only");
        var seconds = reader.Number("--seconds");
        var connectTimeout = reader.Number("--connect-timeout");
        var candidate = new WatchArguments(verb, [])
        {
            RetainedFileNames = directoriesOnly ? [] : keep?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Seconds = seconds is >= 1 and <= MaximumSeconds ? (int)seconds : null,
            ConnectTimeoutSeconds = connectTimeout is >= 1 and <= MaximumSeconds ? (int)connectTimeout : null,
            MaximumSize = reader.Number("--maximum-size"),
            AllocationDelta = reader.Number("--allocation-delta"),
            CacheDirectory = reader.Text("--cache-directory"),
            Clear = reader.Flag("--clear")
        };
        var tokens = reader.Positionals();
        error = reader.Error ?? Validate(candidate, keep, directoriesOnly, seconds, connectTimeout) ?? NotApplicable(candidate.Verb, reader.Supplied) ?? NotADrive(tokens);
        if (error is not null)
        {
            return false;
        }

        parsed = candidate with { Drives = tokens.Select(token => char.ToUpperInvariant(token[0]).ToString()).Distinct().ToList() };
        return true;
    }

    static string? NotADrive(IEnumerable<string> tokens)
    {
        var stray = tokens.FirstOrDefault(token => !ArgumentReader.IsDrive(token));
        return stray is null ? null : $"'{stray}' is not a drive letter.";
    }

    // The first flag on the command line that the verb never reads, named with the verb as the user knows it.
    static string? NotApplicable(WatchVerb verb, IEnumerable<string> supplied)
    {
        var stray = supplied.FirstOrDefault(name => !VerbFlags[verb].Contains(name));
        return stray is null ? null : $"Option {stray} does not apply to {WatchVerbs.Names.First(pair => pair.Value == verb).Key}.";
    }

    static string? Validate(WatchArguments candidate, string? keep, bool directoriesOnly, long? seconds, long? connectTimeout)
    {
        return candidate switch
        {
            _ when directoriesOnly && keep is not null => "Options --directories-only and --keep-name cannot be combined.",
            _ when keep is not null && candidate.RetainedFileNames is { Count: 0 } => "Option --keep-name needs a file name; use --directories-only for directories alone.",
            _ when seconds is < 1 or > MaximumSeconds => $"Option --seconds must be from 1 to {MaximumSeconds}.",
            _ when connectTimeout is < 1 or > MaximumSeconds => $"Option --connect-timeout must be from 1 to {MaximumSeconds}.",
            { Verb: WatchVerb.Journal } when candidate.MaximumSize.HasValue != candidate.AllocationDelta.HasValue
                => "journal needs --maximum-size and --allocation-delta together.",
            _ => null
        };
    }
}
