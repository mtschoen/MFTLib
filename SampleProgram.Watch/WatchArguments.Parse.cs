using System.Diagnostics.CodeAnalysis;

namespace SampleProgram.Watch;

// The command-line parser: the mode name, then the drives.
internal sealed partial record WatchArguments
{
    internal static bool TryParse(string[] arguments, out WatchArguments parsed,
        [NotNullWhen(false)] out string? error)
    {
        var mode = ProgramMode.ScanDrive;
        var start = 0;
        if (arguments.Length > 0 && ProgramModes.Names.TryGetValue(arguments[0], out var named))
        {
            mode = named;
            start = 1;
        }

        var drives = new List<string>();
        for (var index = start; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                parsed = new WatchArguments(ProgramMode.ScanDrive, [DefaultDrive]);
                error = $"Unknown option {argument}.";
                return false;
            }

            drives.Add(argument);
        }

        if (drives.Count == 0)
        {
            drives.Add(DefaultDrive);
        }

        parsed = new WatchArguments(mode, drives);
        error = null;
        return true;
    }
}
