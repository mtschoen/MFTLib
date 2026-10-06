using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using MFTLib.Index;

namespace TestProgram;

/// <summary>
///     The command line of one Index verb: the verb name, then drive letters, then options. Every option
///     is checked against the verb that received it, so a misspelled or misplaced option fails before any
///     index opens. Values stay as typed text; the accessors parse them, and the parser has already
///     proved they parse.
/// </summary>
internal sealed record IndexVerbArguments(
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

    /// <summary>Parses a command line whose first argument names an Index verb.</summary>
    internal static bool TryParse(string[] arguments, [NotNullWhen(true)] out IndexVerbArguments? parsed,
        [NotNullWhen(false)] out string? error)
    {
        parsed = null;
        var verb = IndexVerbSpecifications.Find(arguments[0])!;
        var drives = new List<char>();
        var options = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        for (var index = 1; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (!TryParseDrive(argument, out var letter))
                {
                    error = $"{argument} is not a drive letter.";
                    return false;
                }

                drives.Add(letter);
                continue;
            }

            var specification = verb.Options.FirstOrDefault(candidate => candidate.Name == argument);
            if (specification is null)
            {
                error = $"{argument} does not apply to {verb.Name}.";
                return false;
            }

            var value = string.Empty;
            if (specification.Kind != OptionKind.Flag)
            {
                if (index + 1 >= arguments.Length)
                {
                    error = $"{argument} needs {specification.Describe()}.";
                    return false;
                }

                value = arguments[++index];
                if (!specification.Accepts(value))
                {
                    error = $"{argument} needs {specification.Describe()}.";
                    return false;
                }
            }

            if (options.TryGetValue(argument, out var earlier))
            {
                if (!specification.Repeatable)
                {
                    error = $"{argument} was given twice.";
                    return false;
                }

                options[argument] = [.. earlier, value];
            }
            else
            {
                options[argument] = [value];
            }
        }

        var candidate = verb.WithDefaultDrive(new IndexVerbArguments(verb.Name, drives, options));
        var problem = verb.Validate(candidate);
        if (problem is not null)
        {
            error = problem;
            return false;
        }

        parsed = candidate;
        error = null;
        return true;
    }

    internal bool Has(string option)
    {
        return Options.ContainsKey(option);
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

    internal static bool TryParseDrive(string text, out char letter)
    {
        var trimmed = text.TrimEnd(':');
        letter = trimmed.Length == 1 ? char.ToUpperInvariant(trimmed[0]) : default;
        return trimmed.Length == 1 && char.IsAsciiLetter(trimmed[0]);
    }

    internal static bool TryParseLetters(string text, out IReadOnlyList<char> letters)
    {
        var parsed = new List<char>();
        letters = parsed;
        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!TryParseDrive(part, out var letter))
            {
                return false;
            }

            parsed.Add(letter);
        }

        return parsed.Count > 0;
    }

    static IReadOnlyList<char> ParseLetters(string text)
    {
        return TryParseLetters(text, out var letters) ? letters : [];
    }

    internal static bool TryParseDate(string text, out DateTime value)
    {
        return DateTime.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value);
    }

    static DateTime ParseDate(string text)
    {
        return TryParseDate(text, out var value) ? value : default;
    }

    internal static bool TryParseCacheTag(string text, out CacheTag tag)
    {
        tag = default;
        var parts = text.Split(':');
        if (parts.Length != 2 || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            return false;
        }

        try
        {
            tag = new CacheTag(parts[0], version);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static bool TryParseRecordKey(string text, out IndexRecordKey key)
    {
        key = default;
        var parts = text.Split(':');
        if (parts.Length != 3 || !TryParseDrive(parts[0], out var letter) ||
            !ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var record) ||
            !Enum.TryParse<ProducerKind>(parts[2], ignoreCase: true, out var producer) ||
            !Enum.IsDefined(producer))
        {
            return false;
        }

        key = new IndexRecordKey(letter, record, producer);
        return true;
    }
}
