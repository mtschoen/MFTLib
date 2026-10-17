using System.Globalization;

namespace SampleProgram.Direct;

internal sealed partial class ArgumentReader
{
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
}
