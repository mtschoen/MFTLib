using System.Globalization;

#if SAMPLE_WATCH
namespace SampleProgram.Watch;
#else
namespace SampleProgram.Direct;
#endif

// The typed readers of the option reader: whole numbers, drive letters and positional arguments.
sealed partial class ArgumentReader
{
    internal long? Number(string name)
    {
        var text = Text(name);
        if (text is null)
        {
            return null;
        }

        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        Error ??= $"Option {name} needs a whole number, not '{text}'.";
        return null;
    }

    /// <summary>A drive token is one letter, or one letter and a colon; an empty or longer token is not a drive.</summary>
    internal static bool IsDrive(string text)
    {
        return text.Length is 1 or 2 && char.IsAsciiLetter(text[0]) && (text.Length == 1 || text[1] == ':');
    }

    /// <summary>Returns the positional arguments, after recording the first unknown option as the error.</summary>
    internal IReadOnlyList<string> Positionals()
    {
        var unknown = _remaining.Find(argument => argument.StartsWith("--", StringComparison.Ordinal));
        if (unknown is not null)
        {
            Error ??= $"Unknown option {unknown}.";
        }

        return _remaining;
    }
}
