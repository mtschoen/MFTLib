using System.Diagnostics;
using System.Text.Json;

internal sealed record ProcessResult(int ExitCode, string Output, string Error);

internal sealed class ProcessFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "MFTLib init " + Guid.NewGuid().ToString("N"));
    internal string Repository => Path.Combine(Root, "repository with spaces");
    internal string Commands => Path.Combine(Root, "commands with spaces");
    internal string Log => Path.Combine(Root, "calls.jsonl");
    internal Dictionary<string, string> EnvironmentValues { get; } = new();
    internal static string ExecutableSuffix => OperatingSystem.IsWindows() ? ".exe" : "";
    internal static string Host => OperatingSystem.IsWindows() ? FindHost("pwsh.exe") : "/bin/bash";
    internal static string RealDotnet
    {
        get
        {
            var path = FindHost("dotnet" + ExecutableSuffix);
            return File.ResolveLinkTarget(path, true)?.FullName ?? path;
        }
    }
    internal static readonly TimeSpan ProcessWatchdog = TimeSpan.FromSeconds(
        int.Parse(Environment.GetEnvironmentVariable("MFTLIB_INIT_TEST_TIMEOUT_SECONDS") ?? "600"));

    internal static string FindHost(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var path = Path.Combine(directory.Trim('"'), name);
            if (File.Exists(path)) return Path.GetFullPath(path);
        }
        throw new FileNotFoundException("Required test host not provisioned", name);
    }

    internal static ProcessResult Run(string executable, IEnumerable<string> arguments,
        string workingDirectory, IReadOnlyDictionary<string, string>? environment = null,
        string? commandLine = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (commandLine is null)
        {
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
        }
        else start.Arguments = commandLine;
        if (environment != null)
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start) ?? throw new IOException("Could not start fixture process");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)ProcessWatchdog.TotalMilliseconds))
        {
            process.Kill(true);
            process.WaitForExit();
            throw new IOException($"Fixture process did not exit: {executable}");
        }
        // Drain standard output and error read tasks after process exit.
        return new(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }

    internal static int Forward(string executable, string[] arguments)
    {
        var result = Run(executable, arguments, Environment.CurrentDirectory);
        Console.Out.Write(result.Output);
        Console.Error.Write(result.Error);
        return result.ExitCode;
    }

    internal static int FakeCommand(string[] arguments)
    {
        var command = Path.GetFileNameWithoutExtension(Environment.ProcessPath!);
        if (command == "fixture-preferred") command = "schoen-lab-onboard";
        File.AppendAllText(Environment.GetEnvironmentVariable("MFTLIB_INIT_LOG")!,
            JsonSerializer.Serialize(new[] { command }.Concat(arguments)) + Environment.NewLine);
        switch (command)
        {
            case "schoen-lab-onboard":
            case "schoen-lab":
                Console.WriteLine("fixture provisioner stdout");
                Console.Error.WriteLine("fixture provisioner stderr");
                return int.Parse(Environment.GetEnvironmentVariable("MFTLIB_INIT_PROVISIONER_EXIT")!);
            case "onboard": throw new InvalidOperationException("Unqualified onboard was invoked");
            case "dotnet" when arguments.FirstOrDefault() == "run":
                if (Environment.GetEnvironmentVariable("MFTLIB_INIT_LAUNCHER_FAILURE") == "1")
                {
                    Console.WriteLine("fixture launcher failure");
                    return 31;
                }
                if (!arguments.Any(argument => argument.EndsWith("OptionalProvisioning.csproj", StringComparison.Ordinal)))
                    throw new InvalidOperationException("Unexpected dotnet run target");
                return Forward(Environment.GetEnvironmentVariable("MFTLIB_INIT_REAL_DOTNET")!, arguments);
            case "dotnet" when arguments.SequenceEqual(new[] { "--version" }):
                Console.WriteLine("10.0.100"); return 0;
            case "dotnet" when arguments.FirstOrDefault() == "test": return 0;
            case "dotnet" when arguments.FirstOrDefault() is "restore" or "build":
                return Environment.GetEnvironmentVariable("MFTLIB_INIT_REQUIRED_FAILURE") == "1" ||
                    (arguments[0] == "build" && Environment.GetEnvironmentVariable("MFTLIB_INIT_BUILD_FAILURE") == "1") ? 17 : 0;
            case "pwsh":
            case "powershell":
                return Forward(Environment.GetEnvironmentVariable("MFTLIB_INIT_REAL_SHELL")!, arguments);
            case "vswhere": Console.WriteLine(Environment.GetEnvironmentVariable("MFTLIB_INIT_VISUAL_STUDIO")); return 0;
            case "aislop" when arguments.SequenceEqual(new[] { "--version" }): Console.WriteLine("fixture"); return 0;
            case "aislop" when arguments.SequenceEqual(new[] { "hook", "install", "claude", "--project" }):
                Directory.CreateDirectory(".claude");
                File.WriteAllText(".claude/AISLOP.md", "fixture instructions");
                File.WriteAllText(".claude/CLAUDE.md", "fixture instructions");
                return 0;
            case "git" when arguments.Contains("diff") || arguments.Contains("status"): return 0;
            case "g++" when arguments.SequenceEqual(new[] { "-dumpversion" }): Console.WriteLine("14"); return 0;
            case "cmake":
            case "MSBuild": return Environment.GetEnvironmentVariable("MFTLIB_INIT_BUILD_FAILURE") == "1" ? 17 : 0;
            default: throw new InvalidOperationException("Unexpected fake command: " + command);
        }
    }

    internal void InstallDouble(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        foreach (var source in Directory.GetFiles(AppContext.BaseDirectory))
        {
            var destination = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileName(source));
            File.Copy(source, destination, true);
        }
        var apphost = Path.Combine(AppContext.BaseDirectory, "OptionalProvisioning.Tests" + ExecutableSuffix);
        File.Copy(apphost, path, true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal ProcessFixture(string sourceRoot, int installed, int provisionerExit, bool realDependencies = false)
    {
        Directory.CreateDirectory(Repository);
        Directory.CreateDirectory(Commands);
        var home = Path.Combine(Root, "home");
        Directory.CreateDirectory(home);
        EnvironmentValues["MFTLIB_INIT_FIXTURE"] = "1";
        EnvironmentValues["MFTLIB_INIT_LOG"] = Log;
        EnvironmentValues["MFTLIB_INIT_PROVISIONER_EXIT"] = provisionerExit.ToString();
        EnvironmentValues["MFTLIB_INIT_REAL_DOTNET"] = RealDotnet;
        EnvironmentValues["MFTLIB_INIT_REAL_SHELL"] = Host;
        EnvironmentValues["DOTNET_ROOT"] = Path.GetDirectoryName(RealDotnet)!;
        EnvironmentValues["DOTNET_NOLOGO"] = "1";
        // The fixture HOME is a throwaway directory, so the SDK first-run step would append
        // <fixture home>/.dotnet/tools to the persistent Windows user PATH on every run.
        EnvironmentValues["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false";
        // Reusable MSBuild nodes and build servers outlive the build, keep the fixture profile busy and would
        // make disposal race with a still-running child.
        EnvironmentValues["MSBUILDDISABLENODEREUSE"] = "1";
        EnvironmentValues["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        EnvironmentValues["UseSharedCompilation"] = "false";
        foreach (var name in new[] { "HOME", "USERPROFILE", "DOTNET_CLI_HOME", "XDG_CONFIG_HOME", "APPDATA", "LOCALAPPDATA" })
            EnvironmentValues[name] = home;
        var realProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        EnvironmentValues["AISLOP_HOME"] = Environment.GetEnvironmentVariable("AISLOP_HOME")
            ?? Path.Combine(realProfile, "aislop");
        var realNuget = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(realProfile, ".nuget", "packages");
        if (Directory.Exists(realNuget)) EnvironmentValues["NUGET_PACKAGES"] = realNuget;
        EnvironmentValues["PATH"] = Commands + (realDependencies
            ? Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") : "");
        EnvironmentValues["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
        InstallDouble(Path.Combine(Commands, "onboard" + ExecutableSuffix));
        if ((installed & 1) != 0) InstallDouble(Path.Combine(Commands, "schoen-lab-onboard" + ExecutableSuffix));
        if ((installed & 2) != 0) InstallDouble(Path.Combine(Commands, "schoen-lab" + ExecutableSuffix));
        if (realDependencies) return;

        foreach (var name in new[] { "dotnet", "aislop", "git", "reportgenerator", "cmake", "ninja", "g++", "gcovr" })
            InstallDouble(Path.Combine(Commands, name + ExecutableSuffix));
        foreach (var relative in new[] { "init.ps1", "init.sh", "init.bat", "scripts/build-windows.ps1", "scripts/windows-tools.ps1", "scripts/build-linux.sh" })
            CopyFile(sourceRoot, relative);
        foreach (var relative in new[] { "Directory.Build.props", "NuGet.Config", "OptionalProvisioning.csproj", "Program.cs", "ProvisioningDispatcher.cs", "CommandLauncher.cs" })
            CopyFile(sourceRoot, "scripts/optional-provisioning/" + relative);
        if (OperatingSystem.IsWindows())
        {
            InstallDouble(Path.Combine(Commands, "pwsh.exe"));
            InstallDouble(Path.Combine(Commands, "powershell.exe"));
            File.Copy(Path.Combine(Environment.SystemDirectory, "where.exe"), Path.Combine(Commands, "where.exe"));
            var programFiles = Path.Combine(Root, "Program Files (x86)");
            var visualStudio = Path.Combine(Root, "Visual Studio");
            EnvironmentValues["ProgramFiles(x86)"] = programFiles;
            EnvironmentValues["MFTLIB_INIT_VISUAL_STUDIO"] = visualStudio;
            InstallDouble(Path.Combine(programFiles, "Microsoft Visual Studio/Installer/vswhere.exe"));
            InstallDouble(Path.Combine(visualStudio, "MSBuild/Current/Bin/amd64/MSBuild.exe"));
        }
        else
        {
            foreach (var name in new[] { "bash", "dirname", "mkdir", "mktemp", "rm" })
                File.CreateSymbolicLink(Path.Combine(Commands, name), FindHost(name));
        }
    }

    private void CopyFile(string sourceRoot, string relative)
    {
        var destination = Path.Combine(Repository, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(Path.Combine(sourceRoot, relative), destination);
        if (!OperatingSystem.IsWindows() && relative.EndsWith(".sh", StringComparison.Ordinal))
            File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal ProcessResult Invoke(string entry, bool build)
    {
        if (entry == "init.bat")
        {
            // cmd consumes a command string, not an ordinary argument vector.
            var command = "\"\"" + Path.Combine(Repository, entry) + "\"" + (build ? " -Build" : "") + "\"";
            return Run(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Array.Empty<string>(),
                Repository, EnvironmentValues, "/d /s /c " + command);
        }
        var arguments = entry == "init.sh"
            ? new List<string> { Path.Combine(Repository, entry) }
            : new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(Repository, entry) };
        if (build) arguments.Add(entry == "init.sh" ? "--build" : "-Build");
        return Run(Host, arguments, Repository, EnvironmentValues);
    }

    internal string[][] Calls() => File.Exists(Log)
        ? File.ReadAllLines(Log).Select(line => JsonSerializer.Deserialize<string[]>(line)!).ToArray()
        : Array.Empty<string[]>();

    public void Dispose() => DeleteOwnedTree(Root);

    // Windows components such as WinINet create entries (INetCache\Content.IE5) with ReadOnly, System and Hidden
    // attributes inside the fixture-owned profile directories; Directory.Delete refuses those until the attributes
    // are cleared. Reparse points are removed without following them. Every failure still propagates.
    internal static void DeleteOwnedTree(string root)
    {
        if (!Directory.Exists(root)) return;
        ClearAttributes(new DirectoryInfo(root));
        Directory.Delete(root, true);
    }

    private static void ClearAttributes(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            entry.Attributes = FileAttributes.Normal;
            if (entry is DirectoryInfo child && child.LinkTarget is null) ClearAttributes(child);
        }
        directory.Attributes = FileAttributes.Directory;
    }
}
