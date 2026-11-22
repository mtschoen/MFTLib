// In-process checks of the helper's own discovery and batch transport (CommandLauncher, ProvisioningDispatcher).
// These behaviors belong to the helper, not to an init entry point, so they run here against temporary
// directories instead of through a fresh shell and a full init for every state.
internal static class LauncherTests
{
    private static readonly string[] Arguments = { "apply", "auto-memory" };

    internal static void Run()
    {
        CheckDiscovery();
        if (OperatingSystem.IsWindows()) CheckBatchDiscovery();
        else CheckNonExecutableSkipped();
        if (OperatingSystem.IsWindows())
        {
            CheckBatchStartInfo();
            CheckBatchTransport();
        }
        Console.WriteLine("Launcher discovery and transport tests passed.");
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MFTLib launcher " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string Touch(string directory, string name, bool executable = true)
    {
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, executable
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    // PATHEXT supplies the extension's case on Windows, where paths compare without case.
    private static string? Normalize(string? path) => OperatingSystem.IsWindows() ? path?.ToLowerInvariant() : path;

    // Runs the real discovery over a PATH made of one directory; the previous values are always restored.
    private static T WithPath<T>(string directory, Func<T> action)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        var extensions = Environment.GetEnvironmentVariable("PATHEXT");
        Environment.SetEnvironmentVariable("PATH", directory);
        Environment.SetEnvironmentVariable("PATHEXT", ".COM;.EXE;.BAT;.CMD");
        try { return action(); }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
            Environment.SetEnvironmentVariable("PATHEXT", extensions);
        }
    }

    private static (int Status, string[][] Calls) Dispatch(string directory)
    {
        var calls = new List<string[]>();
        using var output = new StringWriter();
        using var diagnostics = new StringWriter();
        var status = WithPath(directory, () => ProvisioningDispatcher.Run(CommandLauncher.Find,
            (path, arguments) => { calls.Add(new[] { path }.Concat(arguments).ToArray()); return 0; },
            output, diagnostics));
        return (status, calls.ToArray());
    }

    // Preferred wins, fallback alone is used, nothing installed is silent, and the unqualified `onboard` is never run.
    private static void CheckDiscovery()
    {
        foreach (var installed in new[] { 0, 1, 2, 3 })
        {
            var directory = NewDirectory();
            try
            {
                var suffix = ProcessFixture.ExecutableSuffix;
                var preferred = (installed & 1) != 0 ? Touch(directory, "schoen-lab-onboard" + suffix) : null;
                var fallback = (installed & 2) != 0 ? Touch(directory, "schoen-lab" + suffix) : null;
                Touch(directory, "onboard" + suffix);
                var (status, calls) = Dispatch(directory);
                TestProgram.Equal(installed == 0 ? 10 : 0, status, $"discovery status (installed {installed})");
                TestProgram.Equal(installed == 0 ? 0 : 1, calls.Length, $"one provisioner (installed {installed})");
                if (installed == 0) continue;
                var selected = preferred ?? fallback!;
                TestProgram.Equal(string.Join('|', new[] { Normalize(selected) }.Concat(Arguments)),
                    string.Join('|', new[] { Normalize(calls.Single()[0]) }.Concat(calls.Single().Skip(1))),
                    $"selected path and argv (installed {installed})");
            }
            finally { ProcessFixture.DeleteOwnedTree(directory); }
        }
    }

    private static void CheckNonExecutableSkipped()
    {
        var directory = NewDirectory();
        try
        {
            Touch(directory, "schoen-lab-onboard", executable: false);
            var fallback = Touch(directory, "schoen-lab");
            var (status, calls) = Dispatch(directory);
            TestProgram.Equal(0, status, "non-executable preferred falls back");
            TestProgram.Equal(fallback, calls.Single()[0], "non-executable preferred is skipped");
        }
        finally { ProcessFixture.DeleteOwnedTree(directory); }
    }

    private static void CheckBatchDiscovery()
    {
        foreach (var extension in new[] { ".cmd", ".bat" })
        {
            var directory = NewDirectory();
            try
            {
                var shim = Touch(directory, "schoen-lab-onboard" + extension);
                TestProgram.Equal(Normalize(shim), Normalize(WithPath(directory, () => CommandLauncher.Find("schoen-lab-onboard"))),
                    $"{extension} provisioner is discovered through PATHEXT");
            }
            finally { ProcessFixture.DeleteOwnedTree(directory); }
        }
    }

    private static void CheckBatchStartInfo()
    {
        foreach (var extension in new[] { ".cmd", ".bat" })
        {
            var path = Path.Combine(Path.GetTempPath(), "schoen-lab-onboard" + extension);
            var start = CommandLauncher.CreateStartInfo(path, Arguments);
            TestProgram.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), start.FileName,
                $"{extension} dispatches through cmd.exe");
            TestProgram.Equal(path, start.Environment["MFTLIB_PROVISIONER_PATH"], $"{extension} path reaches the child environment");
        }
    }

    // The provisioner path reaches cmd.exe through a child-only variable. One path combines every hostile case:
    // spaces, a literal %NAME%, !, ^ and &. The shim records the complete argv and any injected command writes a marker.
    private static void CheckBatchTransport()
    {
        var root = NewDirectory();
        var original = Environment.GetEnvironmentVariable("MFTLIB_REVIEW_EXPANSION");
        try
        {
            const string name = "commands with spaces literal%MFTLIB_REVIEW_EXPANSION% !caret^and&";
            var shim = Path.Combine(root, name, "schoen-lab-onboard.cmd");
            Directory.CreateDirectory(Path.GetDirectoryName(shim)!);
            File.WriteAllText(shim, "@echo off\r\n>\"%~dp0observed.txt\" echo [%*]\r\nexit /b 7\r\n");
            var marker = Path.Combine(root, "injected.txt");
            Environment.SetEnvironmentVariable("MFTLIB_REVIEW_EXPANSION",
                "expanded\" & echo injected>\"" + marker + "\" & rem ");
            var status = CommandLauncher.Run(shim, Arguments);
            TestProgram.Equal(false, File.Exists(marker), "no injected command");
            TestProgram.Equal(7, status, "shim exit code propagates");
            TestProgram.Equal("[\"apply\" \"auto-memory\"]", File.ReadAllText(Path.Combine(root, name, "observed.txt")).Trim(),
                "complete argv arrives intact without trailing arguments");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MFTLIB_REVIEW_EXPANSION", original);
            ProcessFixture.DeleteOwnedTree(root);
        }
    }
}
