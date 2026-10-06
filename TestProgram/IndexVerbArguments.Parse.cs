using System.Diagnostics.CodeAnalysis;

namespace TestProgram;

// The command-line parser: the verb name, then drive letters and options checked against that verb.
internal sealed partial record IndexVerbArguments
{
    /// <summary>Parses a command line whose first argument names an Index verb.</summary>
    internal static bool TryParse(string[] arguments, [NotNullWhen(true)] out IndexVerbArguments? parsed,
        [NotNullWhen(false)] out string? error)
    {
        parsed = null;
        if (IndexVerbSpecifications.Find(arguments[0]) is not { } verb)
        {
            error = $"{arguments[0]} is not an Index verb.";
            return false;
        }

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
}
