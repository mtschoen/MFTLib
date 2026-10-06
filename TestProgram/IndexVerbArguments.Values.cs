using System.Globalization;
using MFTLib.Index;

namespace TestProgram;

// The value parsers behind the typed options, shared by the parser, the option checks and the accessors.
internal sealed partial record IndexVerbArguments
{
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
