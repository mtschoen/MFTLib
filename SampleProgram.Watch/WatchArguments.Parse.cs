using System.Diagnostics.CodeAnalysis;
using MFTLib.Index;

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
        _ = reader.Text("--keep-name");
        _ = reader.Flag("--directories-only");
        var seconds = reader.Number("--seconds");
        var connectTimeout = reader.Number("--connect-timeout");
        var producerText = reader.Text("--producer");
        var candidate = new WatchArguments(verb, [])
        {
            Producer = ParseProducer(producerText),
            Seconds = seconds is >= 1 and <= MaximumSeconds ? (int)seconds : null,
            ConnectTimeoutSeconds = connectTimeout is >= 1 and <= MaximumSeconds ? (int)connectTimeout : null,
            MaximumSize = reader.Number("--maximum-size"),
            AllocationDelta = reader.Number("--allocation-delta"),
            CacheDirectory = reader.Text("--cache-directory"),
            Clear = reader.Flag("--clear")
        };
        var tokens = reader.Positionals();
        error = reader.Error ?? Validate(candidate, seconds, connectTimeout) ?? UnknownProducer(producerText)
            ?? NotApplicable(candidate.Verb, reader.Supplied) ?? NotARoot(tokens);
        if (error is not null)
        {
            return false;
        }

        parsed = candidate with { Roots = tokens.Select(token => RootKey.TryParse(token, out var root) ? root : default).Distinct().ToList() };
        return true;
    }

    static ProducerSelection ParseProducer(string? text) => text?.ToLowerInvariant() switch
    {
        "mft" => ProducerSelection.MftOnly,
        "walk" => ProducerSelection.WalkOnly,
        _ => ProducerSelection.Automatic
    };

    static string? UnknownProducer(string? text) =>
        text is null || text.ToLowerInvariant() is "automatic" or "mft" or "walk"
            ? null
            : "Option --producer must be automatic, mft or walk.";

    static string? NotARoot(IEnumerable<string> tokens)
    {
        var stray = tokens.FirstOrDefault(token => !RootKey.TryParse(token, out _));
        return stray is null ? null : $"Root '{stray}' is neither a drive nor a full path.";
    }

    // The first flag on the command line that the verb never reads, named with the verb as the user knows it.
    static string? NotApplicable(WatchVerb verb, IEnumerable<string> supplied)
    {
        var stray = supplied.FirstOrDefault(name => !VerbFlags[verb].Contains(name));
        return stray is null ? null : $"Option {stray} does not apply to {WatchVerbs.Names.First(pair => pair.Value == verb).Key}.";
    }

    static string? Validate(WatchArguments candidate, long? seconds, long? connectTimeout)
    {
        return candidate switch
        {
            _ when seconds is < 1 or > MaximumSeconds => $"Option --seconds must be from 1 to {MaximumSeconds}.",
            _ when connectTimeout is < 1 or > MaximumSeconds => $"Option --connect-timeout must be from 1 to {MaximumSeconds}.",
            { Verb: WatchVerb.Journal } when candidate.MaximumSize.HasValue != candidate.AllocationDelta.HasValue
                => "journal needs --maximum-size and --allocation-delta together.",
            _ => null
        };
    }
}
