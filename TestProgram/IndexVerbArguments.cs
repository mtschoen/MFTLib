using System.Globalization;
using MFTLib.Index;

namespace TestProgram;

/// <summary>
///     The command line of one Index verb: the verb name, then drive letters, then options. Every option
///     is checked against the verb that received it, so a misspelled or misplaced option fails before any
///     index opens. Values stay as typed text; the accessors parse them, and the parser has already
///     proved they parse.
/// </summary>
internal sealed partial record IndexVerbArguments(
    string Verb,
    IReadOnlyList<char> Drives,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Options)
{
    internal const string BrokerSource = "broker";
    internal const string EnumerationSource = "enumeration";
    internal const string UnavailableSource = "unavailable";
    internal const string FullProfile = "full";
    internal const string DirectoriesProfile = "directories";

    internal static readonly IReadOnlyList<string> VerbNames = IndexVerbSpecifications.All.Select(verb => verb.Name).ToArray();

    internal static string Usage => IndexVerbSpecifications.Usage;

    internal static bool IsVerb(string name)
    {
        return IndexVerbSpecifications.Find(name) is not null;
    }

    internal bool Has(string option)
    {
        return Options.ContainsKey(option);
    }

    /// <summary>A text option the verb's validation guarantees is present; a miss is a bug in the specification.</summary>
    internal string RequiredText(string option)
    {
        return Text(option) ?? throw MissingOption(option);
    }

    internal string? Text(string option)
    {
        return Options.TryGetValue(option, out var values) ? values[0] : null;
    }

    internal IReadOnlyList<string> TextList(string option)
    {
        return Options.TryGetValue(option, out var values) ? values : [];
    }

    internal long? Number(string option)
    {
        return Text(option) is { } text ? long.Parse(text, CultureInfo.InvariantCulture) : null;
    }

    internal long RequiredNumber(string option)
    {
        return Number(option) ?? throw MissingOption(option);
    }

    static InvalidOperationException MissingOption(string option)
    {
        return new InvalidOperationException($"{option} was not supplied, and argument validation should have required it.");
    }

    internal DateTime? Date(string option)
    {
        return Text(option) is { } text ? ParseDate(text) : null;
    }

    /// <summary>The letters of a comma-separated list option, upper-cased, or null when the option is absent.</summary>
    internal IReadOnlyList<char>? Letters(string option)
    {
        return Text(option) is { } text ? ParseLetters(text) : null;
    }

    internal CacheTag? Tag()
    {
        return Text("--cache-tag") is { } text && TryParseCacheTag(text, out var tag) ? tag : null;
    }

    /// <summary>The drive:row:producer tuple of --record-key, built with the public key constructor.</summary>
    internal IndexRecordKey? RecordKey()
    {
        return Text("--record-key") is { } text && TryParseRecordKey(text, out var key) ? key : null;
    }

    internal string Source => Text("--source") ?? BrokerSource;
}
