using System.Text.Json;

internal static class AdapterTests
{
    internal const string Completion = "Project-scope provisioned settings re-applied";

    private static void CheckBatchTransport(string root)
    {
        foreach (var extension in new[] { ".cmd", ".bat" })
        foreach (var directory in new[] { "commands with spaces", "literal%MFTLIB_REVIEW_EXPANSION%", "literal!caret^and&" })
        {
            using var batch = new ProcessFixture(root, 3, 0);
            File.Delete(Path.Combine(batch.Commands, "schoen-lab-onboard.exe"));
            var commandDirectory = Path.Combine(batch.Root, directory);
            batch.InstallDouble(Path.Combine(commandDirectory, "fixture-preferred.exe"));
            File.WriteAllText(Path.Combine(commandDirectory, "schoen-lab-onboard" + extension),
                "@echo off\r\n\"%~dp0fixture-preferred.exe\" %*\r\nexit /b %ERRORLEVEL%\r\n");
            batch.EnvironmentValues["PATH"] = commandDirectory + Path.PathSeparator + batch.EnvironmentValues["PATH"];
            batch.EnvironmentValues["MFTLIB_REVIEW_EXPANSION"] = "expanded\" & echo INJECTED & rem ";
            var result = batch.Invoke("init.ps1", false);
            TestProgram.Equal(false, (result.Output + result.Error).Contains("INJECTED", StringComparison.Ordinal),
                $"no injected command for {directory}{extension}");
            // FakeCommand maps fixture-preferred to the preferred provisioner for its log.
            Check(result, batch, 3, 0);
            Console.WriteLine($"Batch transport passed: {directory}{extension}");
        }
    }

    internal static void Check(ProcessResult result, ProcessFixture fixture, int installed, int provisionerExit)
    {
        TestProgram.Equal(0, result.ExitCode, "init exit");
        var calls = fixture.Calls();
        var provisioning = calls.Where(call => call[0] is "schoen-lab-onboard" or "schoen-lab").ToArray();
        TestProgram.Equal(installed == 0 ? 0 : 1, provisioning.Length, "selected provisioner invocation");
        TestProgram.Equal(false, calls.Any(call => call[0] == "onboard"), "poison executable untouched");
        TestProgram.Equal(1, calls.Count(call => call[0] == "dotnet" && call.ElementAtOrDefault(1) == "run"),
            "entry point delegates to shared implementation");
        if (installed != 0)
        {
            var expected = new[] { (installed & 1) != 0 ? "schoen-lab-onboard" : "schoen-lab", "apply", "auto-memory" };
            TestProgram.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(provisioning.Single()), "argv");
            TestProgram.Contains("fixture provisioner stdout", result.Error);
            TestProgram.Contains("fixture provisioner stderr", result.Error);
            if (provisionerExit != 0) TestProgram.Contains("exited with 23; continuing.", result.Error);
        }
        else
        {
            TestProgram.Equal(false, (result.Output + result.Error).Contains("Re-applying provisioned"), "silent absence");
        }
        TestProgram.Equal(installed != 0 && provisionerExit == 0 ? 1 : 0,
            result.Output.Split('\n').Count(line => line.Trim() == Completion), "completed summary");
    }

    private static void CheckManagedSmokeInvocation(ProcessFixture fixture, string[][] calls)
    {
        var smoke = calls.Where(call => call[0] == "dotnet" && call.ElementAtOrDefault(1) == "test").ToArray();
        var expected = new[]
        {
            "dotnet", "test", Path.Combine(fixture.Repository, "MFTLib.Tests", "MFTLib.Tests.csproj"),
            "-c", "Release", "-p:MFTLibNativeLinuxFlavor=Release",
            smoke.Single().Single(argument => argument.StartsWith("-p:MFTLibSmokeArtifactsDirectory=", StringComparison.Ordinal)), "--filter", "TestCategory=Smoke"
        };
        TestProgram.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(smoke.Single()), "managed Smoke argv");
        var nativeBuild = Array.FindIndex(calls, call => call[0] == "cmake" && call.ElementAtOrDefault(1) == "--build");
        TestProgram.Equal(true, nativeBuild >= 0 && nativeBuild < Array.IndexOf(calls, smoke.Single()),
            "managed Smoke tier runs after the native build");
    }

    private static void CheckRestrictiveDisposal(string root)
    {
        var fixture = new ProcessFixture(root, 3, 0);
        var cache = Path.Combine(fixture.Root, "home", "AppData", "Local", "Microsoft", "Windows", "INetCache", "Content.IE5");
        var nested = Path.Combine(cache, "nested");
        Directory.CreateDirectory(nested);
        var files = new[] { Path.Combine(cache, "index.dat"), Path.Combine(nested, "entry.tmp") };
        foreach (var file in files) File.WriteAllText(file, "cache");
        if (OperatingSystem.IsWindows())
        {
            foreach (var file in files)
                File.SetAttributes(file, FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
            File.SetAttributes(nested, FileAttributes.Directory | FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
            File.SetAttributes(cache, FileAttributes.Directory | FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
        }
        else foreach (var file in files) File.SetAttributes(file, FileAttributes.ReadOnly);
        fixture.Dispose();
        TestProgram.Equal(false, Directory.Exists(fixture.Root), "fixture root removed despite restrictive attributes");
        Console.WriteLine("Restrictive attribute disposal passed.");
    }

    private static void CheckDotnetUserPathIsolation(string root)
    {
        foreach (var realDependencies in new[] { false, true })
        {
            using var fixture = new ProcessFixture(root, 0, 0, realDependencies);
            TestProgram.Equal("false", fixture.EnvironmentValues.GetValueOrDefault("DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"),
                $"fixture dotnet stays off the user PATH (real dependencies: {realDependencies})");
        }
    }

    internal static int Run(string root)
    {
        TestProgram.Policy();
        CheckDotnetUserPathIsolation(root);
        CheckRestrictiveDisposal(root);

        var entries = OperatingSystem.IsWindows() ? new[] { "init.ps1", "init.bat" } : new[] { "init.sh" };
        foreach (var entry in entries)
        foreach (var installed in new[] { 0, 1, 2, 3 })
        foreach (var provisionerExit in new[] { 0, 23 })
        {
            using var fixture = new ProcessFixture(root, installed, provisionerExit);
            Check(fixture.Invoke(entry, false), fixture, installed, provisionerExit);
        }
        foreach (var entry in entries)
        {
            using var fixture = new ProcessFixture(root, 3, 23);
            Check(fixture.Invoke(entry, true), fixture, 3, 23);
            var calls = fixture.Calls();
            TestProgram.Equal(true, calls.Any(call => call[0] == "dotnet" && call.ElementAtOrDefault(1) == "build"), "build forwarding");
            TestProgram.Equal(true, calls.Any(call => call[0] == (OperatingSystem.IsWindows() ? "MSBuild" : "cmake")), "native build forwarding");
            if (!OperatingSystem.IsWindows()) CheckManagedSmokeInvocation(fixture, calls);
        }
        foreach (var entry in entries)
        {
            using var fixture = new ProcessFixture(root, 3, 0);
            fixture.EnvironmentValues["MFTLIB_INIT_LAUNCHER_FAILURE"] = "1";
            var result = fixture.Invoke(entry, false);
            TestProgram.Equal(0, result.ExitCode, "launcher failure remains optional");
            TestProgram.Contains("fixture launcher failure", result.Output + result.Error);
            TestProgram.Equal(false, result.Output.Contains(Completion), "no false launcher completion");
        }
        foreach (var entry in entries)
        {
            using var fixture = new ProcessFixture(root, 3, 0);
            fixture.EnvironmentValues["MFTLIB_INIT_REQUIRED_FAILURE"] = "1";
            var result = fixture.Invoke(entry, false);
            TestProgram.Equal(true, result.ExitCode != 0, "required restore still fails init");
            TestProgram.Equal(false, fixture.Calls().Any(call => call[0].StartsWith("schoen-lab", StringComparison.Ordinal)), "restore failure stops provisioning");
        }
        foreach (var entry in entries)
        {
            using var fixture = new ProcessFixture(root, 3, 23);
            fixture.EnvironmentValues["MFTLIB_INIT_BUILD_FAILURE"] = "1";
            var result = fixture.Invoke(entry, true);
            TestProgram.Equal(true, result.ExitCode != 0, "required build still fails init");
            TestProgram.Equal(true, fixture.Calls().Any(call => call[0] == "schoen-lab-onboard"), "optional failure preceded required build failure");
            TestProgram.Equal(false, fixture.Calls().Any(call => call[0] == "dotnet" && call.ElementAtOrDefault(1) == "test"),
                "managed Smoke tier does not run after a failed native build");
        }
        if (OperatingSystem.IsWindows())
        {
            using var fixture = new ProcessFixture(root, 3, 23);
            File.Delete(Path.Combine(fixture.Commands, "pwsh.exe"));
            fixture.EnvironmentValues["MFTLIB_INIT_REAL_SHELL"] = Path.Combine(Environment.SystemDirectory,
                "WindowsPowerShell/v1.0/powershell.exe");
            Check(fixture.Invoke("init.bat", false), fixture, 3, 23);

            CheckBatchTransport(root);

            using var errorPreferenceFixture = new ProcessFixture(root, 3, 23);
            var script = "$PSNativeCommandUseErrorActionPreference=$true; & './init.ps1'; exit $LASTEXITCODE";
            Check(ProcessFixture.Run(ProcessFixture.Host, new[] { "-NoProfile", "-Command", script },
                errorPreferenceFixture.Repository, errorPreferenceFixture.EnvironmentValues), errorPreferenceFixture, 3, 23);
        }
        else
        {
            using var fixture = new ProcessFixture(root, 3, 0);
            File.SetUnixFileMode(Path.Combine(fixture.Commands, "schoen-lab-onboard"), UnixFileMode.UserRead);
            Check(fixture.Invoke("init.sh", false), fixture, 2, 0);
        }
        Console.WriteLine("Init adapter regression tests passed.");
        return 0;
    }
}
