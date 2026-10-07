using System.Globalization;

#if SAMPLE_WATCH
namespace SampleProgram.Watch;
#else
namespace SampleProgram.Direct;
#endif

// The typed readers of the option reader: whole numbers, bounded integers and dates.
internal sealed partial class ArgumentReader
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

    internal int? Integer(string name)
    {
        var value = Number(name);
        if (value > int.MaxValue)
        {
            Error ??= $"Option {name} must be at most {int.MaxValue}.";
            return null;
        }

        return (int?)value;
    }

    internal DateTime? Date(string name)
    {
        var text = Text(name);
        if (text is null)
        {
            return null;
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
        {
            return value;
        }

        Error ??= $"Option {name} needs a date such as 2026-10-01, not '{text}'.";
        return null;
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
