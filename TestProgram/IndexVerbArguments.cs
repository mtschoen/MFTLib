using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace TestProgram;

/// <summary>
///     One Index verb's command line: the verb, then drive letters, then options. Options are checked only
///     for being known; a value that does not parse is reported by the verb that reads it.
/// </summary>
internal sealed partial record IndexVerbArguments(string Verb, IReadOnlyList<char> Drives, IReadOnlyDictionary<string, string> Options)
{
    internal static bool TryParse(string[] arguments, [NotNullWhen(true)] out IndexVerbArguments? parsed,
        [NotNullWhen(false)] out string? error)
    {
        var drives = new List<char>();
        var options = new Dictionary<string, string>();
        parsed = null;
        error = null;
        for (var index = 1; index < arguments.Length && error is null; index++)
        {
            var argument = arguments[index];
            if (Flags.Contains(argument))
            {
                options[argument] = string.Empty;
            }
            else if (ValueOptions.Contains(argument))
            {
                error = index + 1 < arguments.Length ? null : $"{argument} needs a value.";
                options[argument] = error is null ? arguments[++index] : string.Empty;
            }
            else if (argument.TrimEnd(':') is { Length: 1 } letter && char.IsAsciiLetter(letter[0]))
            {
                drives.Add(char.ToUpperInvariant(letter[0]));
            }
            else
            {
                error = $"{argument} is not an option of an Index verb or a drive letter.";
            }
        }

        parsed = error is null ? new IndexVerbArguments(arguments[0].ToLowerInvariant(), drives, options) : null;
        return error is null;
    }

    internal bool Has(string option)
    {
        return Options.ContainsKey(option);
    }

    internal string? Text(string option)
    {
        return Options.GetValueOrDefault(option);
    }

    internal string Require(string option)
    {
        return Text(option) ?? throw new ArgumentException($"{Verb} needs {option}.");
    }

    internal long? Number(string option)
    {
        return Text(option) is { } text ? long.Parse(text, CultureInfo.InvariantCulture) : null;
    }

    internal DateTime? Date(string option)
    {
        return Text(option) is { } text
            ? DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
            : null;
    }

    /// <summary>The drives to open: those given, or the sample drive.</summary>
    internal IReadOnlyList<char> OpenedDrives => Drives.Count > 0 ? Drives : [TestProgramArguments.DefaultDrive[0]];
}
