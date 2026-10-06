using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace TestProgram;

/// <summary>
///     One Index verb's command line: the verb, then drive letters, then options. Options are checked only
///     for being known; a value that does not parse is reported by the verb that reads it.
/// </summary>
internal sealed partial record IndexVerbArguments(string Verb, IReadOnlyList<char> Drives, IReadOnlyDictionary<string, string> Options);

internal sealed partial record IndexVerbArguments
{
    internal static bool TryParse(string[] arguments, [NotNullWhen(true)] out IndexVerbArguments? parsed,
        [NotNullWhen(false)] out string? error)
    {
        var verb = arguments[0].ToLowerInvariant();
        var drives = new List<char>();
        var options = new Dictionary<string, string>();
        parsed = null;
        error = null;
        for (var index = 1; index < arguments.Length && error is null; index++)
        {
            var argument = arguments[index];
            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (!Flags.Contains(argument) && !ValueOptions.Contains(argument))
                {
                    error = $"{argument} is not an option of an Index verb or a drive letter.";
                    break;
                }

                if (!AllowedOptionsByVerb.TryGetValue(verb, out var allowed) || !allowed.Contains(argument))
                {
                    error = $"{argument} does not apply to {verb}.";
                    break;
                }

                if (Flags.Contains(argument))
                {
                    options[argument] = string.Empty;
                    continue;
                }

                if (index + 1 >= arguments.Length)
                {
                    error = $"{argument} needs a value.";
                    break;
                }

                var value = arguments[++index];
                if (!ValidateOptionValue(argument, value, out var normalizedValue, out error))
                {
                    break;
                }

                options[argument] = normalizedValue;
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

        if (error is null)
        {
            ValidateScopesAndDrives(drives, options, out error);
        }

        parsed = error is null ? new IndexVerbArguments(verb, drives, options) : null;
        return error is null;
    }

    static bool ValidateOptionValue(string argument, string value, [NotNullWhen(true)] out string? normalizedValue,
        [NotNullWhen(false)] out string? error)
    {
        normalizedValue = null;
        error = null;
        switch (argument)
        {
            case "--drive-scope":
                var letterPart = value.TrimEnd(':');
                if (letterPart.Length != 1 || !char.IsAsciiLetter(letterPart[0]))
                {
                    error = "--drive-scope needs a single drive letter.";
                    return false;
                }

                normalizedValue = char.ToUpperInvariant(letterPart[0]).ToString();
                return true;

            case "--drive-list":
                return ValidateDriveList(value, out normalizedValue, out error);

            case "--count" or "--limit" or "--depth" or "--seconds":
                if (!int.TryParse(value, CultureInfo.InvariantCulture, out var number) || number <= 0)
                {
                    error = $"{argument} needs a positive whole number.";
                    return false;
                }

                normalizedValue = value;
                return true;

            case "--maximum-size" or "--allocation-delta":
                if (!long.TryParse(value, CultureInfo.InvariantCulture, out var size) || size <= 0)
                {
                    error = $"{argument} needs a positive whole number.";
                    return false;
                }

                normalizedValue = value;
                return true;

            case "--min-size" or "--max-size":
                if (!long.TryParse(value, CultureInfo.InvariantCulture, out var bound) || bound < 0)
                {
                    error = $"{argument} needs a non-negative whole number.";
                    return false;
                }

                normalizedValue = value;
                return true;

            case "--connection-timeout":
                if (!double.TryParse(value, CultureInfo.InvariantCulture, out var timeout) || timeout <= 0)
                {
                    error = $"{argument} needs a positive number.";
                    return false;
                }

                normalizedValue = value;
                return true;

            case "--after" or "--before":
                if (!DateTime.TryParse(value, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _))
                {
                    error = $"{argument} needs an ISO date (e.g. 2026-01-01).";
                    return false;
                }

                normalizedValue = value;
                return true;

            case "--cache-tag":
                if (value.Split(':') is not [_, var versionText] ||
                    !uint.TryParse(versionText, CultureInfo.InvariantCulture, out _))
                {
                    error = $"{argument} needs FOURCC:VERSION.";
                    return false;
                }

                normalizedValue = value;
                return true;

            default:
                normalizedValue = value;
                return true;
        }
    }

    static bool ValidateDriveList(string value, [NotNullWhen(true)] out string? normalizedValue,
        [NotNullWhen(false)] out string? error)
    {
        var parts = value.Split(',');
        var seenLetters = new HashSet<char>();
        var normalizedLetters = new List<char>();
        foreach (var part in parts)
        {
            var trimmed = part.TrimEnd(':');
            if (trimmed.Length != 1 || !char.IsAsciiLetter(trimmed[0]))
            {
                normalizedValue = null;
                error = "--drive-list contains an invalid drive letter.";
                return false;
            }

            var driveChar = char.ToUpperInvariant(trimmed[0]);
            if (!seenLetters.Add(driveChar))
            {
                normalizedValue = null;
                error = "--drive-list contains duplicate drive letters.";
                return false;
            }

            normalizedLetters.Add(driveChar);
        }

        normalizedValue = string.Join(",", normalizedLetters);
        error = null;
        return true;
    }

    static void ValidateScopesAndDrives(List<char> drives, Dictionary<string, string> options,
        out string? error)
    {
        error = null;
        if (options.ContainsKey("--drive-scope") && options.ContainsKey("--drive-list"))
        {
            error = "--drive-scope and --drive-list cannot be used together.";
            return;
        }

        if (options.ContainsKey("--root") && drives.Count > 1)
        {
            error = "--root supports only one drive.";
            return;
        }

        var openedDrives = drives.Count > 0 ? drives : [TestProgramArguments.DefaultDrive[0]];
        if (options.TryGetValue("--drive-scope", out var scopeText) && !openedDrives.Contains(scopeText[0]))
        {
            error = $"--drive-scope {scopeText[0]} is not a configured drive.";
            return;
        }

        if (options.TryGetValue("--drive-list", out var listText))
        {
            foreach (var letter in listText.Split(','))
            {
                if (!openedDrives.Contains(letter[0]))
                {
                    error = $"--drive-list contains unconfigured drive {letter[0]}.";
                    return;
                }
            }
        }
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
