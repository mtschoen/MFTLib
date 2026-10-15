using System.Diagnostics;
using System.Globalization;

internal static class CommandLauncher
{
    internal static string? Find(string name)
    {
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(extension => new[] { ".exe", ".com", ".bat", ".cmd" }
                    .Contains(extension, StringComparer.OrdinalIgnoreCase)).ToArray()
            : new[] { "" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator))
        foreach (var extension in extensions)
        {
            var path = Path.GetFullPath(Path.Combine(directory.Trim('"'), name + extension));
            if (!File.Exists(path)) continue;
            if (!OperatingSystem.IsWindows())
            {
                var executable = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                if ((File.GetUnixFileMode(path) & executable) == 0) continue;
            }
            return path;
        }
        return null;
    }

    private static string FormatBatchArguments(IReadOnlyList<string> arguments)
    {
        // This transport receives the dispatcher's fixed argument vector.
        var quotedArguments = string.Join(" ", arguments.Select(argument => "\"" + argument + "\""));
        return string.Format(CultureInfo.InvariantCulture,
            "/d /v:off /s /c \"\"%MFTLIB_PROVISIONER_PATH%\" {0}\"", quotedArguments);
    }

    internal static int Run(string path, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true
        };
        if (OperatingSystem.IsWindows() &&
            (path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            start.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            // cmd expands this child-only variable once, preserving literal percent references.
            // No CALL reparsing; /v:off also preserves exclamation marks in the selected path.
            start.Environment["MFTLIB_PROVISIONER_PATH"] = path;
            start.Arguments = FormatBatchArguments(arguments);
        }
        else
        {
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)
            ?? throw new IOException("Could not start the selected provisioner");
        var output = process.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardError());
        process.WaitForExit();
        // Drain standard error stream copy task after child process termination.
        output.GetAwaiter().GetResult();
        return process.ExitCode;
    }
}
