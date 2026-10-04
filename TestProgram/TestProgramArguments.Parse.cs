using System.Diagnostics.CodeAnalysis;

namespace TestProgram;

// The command-line parser: the mode name, then positionals, then options checked against the mode.
internal sealed partial record TestProgramArguments
{
    internal static bool TryParse(string[] arguments, out TestProgramArguments parsed,
        [NotNullWhen(false)] out string? error)
    {
        var mode = ProgramMode.FindGit;
        var start = 0;
        if (arguments.Length > 0 && ProgramModes.Names.TryGetValue(arguments[0], out var named))
        {
            mode = named;
            start = 1;
        }

        var positionals = new List<string>();
        var options = new ModeOptions();
        var watchSeconds = DefaultWatchSeconds;
        for (var index = start; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (argument == SecondsOption)
            {
                if (mode != ProgramMode.UsnWatch)
                {
                    return Fail($"{SecondsOption} applies only to usn-watch.", out parsed, out error);
                }

                if (index + 1 >= arguments.Length || !int.TryParse(arguments[index + 1], out watchSeconds) ||
                    watchSeconds <= 0)
                {
                    return Fail($"{SecondsOption} needs a positive whole number.", out parsed, out error);
                }

                index++;
            }
            else if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (!ModeOptions.TryApply(mode, arguments, ref index, options, out options, out var optionError))
                {
                    return Fail(optionError, out parsed, out error);
                }
            }
            else
            {
                positionals.Add(argument);
            }
        }

        var problem = options.Validate(mode, positionals.Count);
        if (problem is not null)
        {
            return Fail(problem, out parsed, out error);
        }

        if (mode == ProgramMode.ParseFile)
        {
            options = options with { FilePath = positionals[0] };
            positionals.Clear();
        }
        else if (positionals.Count == 0)
        {
            positionals.Add(DefaultDrive);
        }

        parsed = new TestProgramArguments(mode, positionals, watchSeconds) { Options = options };
        error = null;
        return true;
    }

    static bool Fail(string message, out TestProgramArguments parsed, [NotNullWhen(false)] out string? error)
    {
        parsed = new TestProgramArguments(ProgramMode.FindGit, [DefaultDrive], DefaultWatchSeconds);
        error = message;
        return false;
    }
}
