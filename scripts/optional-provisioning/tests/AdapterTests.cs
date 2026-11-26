using System.Text.Json;

internal static class AdapterTests
{
    internal const string Completion = "Project-scope provisioned settings re-applied";

    internal static void Check(ProcessResult result, ProcessFixture fixture, int installed, int provisionerExit)
    {
        TestProgram.Equal(0, result.ExitCode, "init exit");
        TestProgram.Contains("fixture dotnet restore output", result.Output);
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
        TestProgram.Equal(false, (result.Output + result.Error).Contains("launcher exited", StringComparison.Ordinal),
            "helper statuses 0, 10 and 20 never raise the launcher warning");
        TestProgram.Equal(installed != 0 && provisionerExit == 0 ? 1 : 0,
            result.Output.Split('\n').Count(line => line.Trim() == Completion), "completed summary");
    }

    // Worktree-only mode (review worktrees) restores and builds in place and never configures git,
    // installs agent files or invokes the optional provisioning helper or provisioners.
    internal static void AssertWorktreeOnly(ProcessFixture fixture, string[][] calls)
    {
        TestProgram.Equal(false, calls.Any(call => call[0] == "git"), "worktree-only never calls git");
        TestProgram.Equal(false, calls.Any(call => call[0] == "aislop" && call.ElementAtOrDefault(1) == "hook"),
            "worktree-only installs no agent files");
        TestProgram.Equal(false, calls.Any(call => call[0] is "schoen-lab-onboard" or "schoen-lab" or "onboard"),
            "worktree-only invokes no provisioner");
        TestProgram.Equal(false, calls.Any(call => call.Any(argument =>
            argument.EndsWith("OptionalProvisioning.csproj", StringComparison.Ordinal))),
            "worktree-only never touches the optional-provisioning helper");
        TestProgram.Equal(false, Directory.Exists(Path.Combine(fixture.Repository, ".claude")), "no agent files written");
    }

    private static void CheckWorktreeOnly(string root, string entry)
    {
        foreach (var build in new[] { false, true })
        {
            using var fixture = new ProcessFixture(root, 3, 0);
            var result = fixture.Invoke(entry, build, worktreeOnly: true);
            TestProgram.Equal(0, result.ExitCode, $"worktree-only exit (build: {build})");
            var calls = fixture.Calls();
            AssertWorktreeOnly(fixture, calls);
            TestProgram.Equal(true, calls.Any(call => call[0] == "dotnet" && call.ElementAtOrDefault(1) == "restore"), "worktree-only restores");
            TestProgram.Equal(build, calls.Any(call => call[0] == "dotnet" && call.ElementAtOrDefault(1) == "build"), "build only on request");
            TestProgram.Equal(build, calls.Any(call => call[0] == (OperatingSystem.IsWindows() ? "MSBuild" : "cmake")), "native build only on request");
        }
        using (var failing = new ProcessFixture(root, 3, 0))
        {
            // With -Build the prerequisite probe passes first, so this reaches the restore step.
            failing.EnvironmentValues["MFTLIB_INIT_REQUIRED_FAILURE"] = "1";
            var result = failing.Invoke(entry, true, worktreeOnly: true);
            TestProgram.Equal(true, result.ExitCode != 0, "worktree-only restore failure is non-zero");
            TestProgram.Equal(false, failing.Calls().Any(call => call[0] == (OperatingSystem.IsWindows() ? "MSBuild" : "cmake")),
                "worktree-only restore failure stops before the native build");
        }
        using var missing = new ProcessFixture(root, 3, 0);
        File.Delete(Path.Combine(missing.Commands, "dotnet"));
        var missingResult = missing.Invoke(entry, true, worktreeOnly: true);
        TestProgram.Equal(true, missingResult.ExitCode != 0, "worktree-only fails fast without dotnet");
        TestProgram.Contains("dotnet SDK is missing", missingResult.Error);
        TestProgram.Equal(0, missing.Calls().Length, "worktree-only runs nothing after a missing prerequisite");
    }

    private static void CheckManagedSmokeInvocation(ProcessFixture fixture, string[][] calls)
    {
        var smoke = calls.Where(call => call[0] == "dotnet" && call.ElementAtOrDefault(1) == "test").ToArray();
        var expected = new[]
        {
            "dotnet", "test", Path.Combine(fixture.Repository, "MFTLib.Tests", "MFTLib.Tests.csproj"),
            "-c", "Release", "-p:MFTLibNativeLinuxFlavor=Release",
            smoke.Single().Single(argument => argument.StartsWith("-p:MFTLibSmokeArtifactsDirectory=", StringComparison.Ordinal)),
            "--no-restore", "--filter", "TestCategory=Smoke"
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

    private static void CheckDotnetBackgroundNetworkIsolation(string root)
    {
        var sharedHome = Path.Combine(root, "shared-home");
        var shared = ProcessFixture.CreateDotnetEnvironment(sharedHome);
        TestProgram.Equal(sharedHome, shared["DOTNET_CLI_HOME"], "shared build uses its own SDK home");
        TestProgram.Equal("1", shared["MSBUILDDISABLENODEREUSE"], "shared build disables reusable MSBuild nodes");
        TestProgram.Equal("0", shared["DOTNET_CLI_USE_MSBUILD_SERVER"], "shared build disables the MSBuild server");
        TestProgram.Equal("false", shared["UseSharedCompilation"], "shared build disables the compiler server");
        foreach (var realDependencies in new[] { false, true })
        {
            using var fixture = new ProcessFixture(root, 0, 0, realDependencies);
            TestProgram.Equal("true", fixture.EnvironmentValues.GetValueOrDefault("DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"),
                $"fixture sets DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE (real dependencies: {realDependencies})");
            foreach (var name in new[] { "DOTNET_NOLOGO", "DOTNET_CLI_TELEMETRY_OPTOUT" })
                TestProgram.Equal("1", fixture.EnvironmentValues.GetValueOrDefault(name),
                    $"fixture sets {name} (real dependencies: {realDependencies})");
        }
        Console.WriteLine("SDK background network isolation passed for both fixture modes.");
    }

    // init.bat only chooses a PowerShell and forwards its arguments and exit code to init.ps1, so it gets three
    // launches that together prove each of those and the Windows PowerShell optional-failure path; the policy matrix and provisioner discovery are asserted in
    // process (TestProgram.Policy and LauncherTests).
    private static void CheckBatchEntry(string root)
    {
        // No pwsh on PATH: the shim falls back to Windows PowerShell without inventing -Build.
        using (var fallback = new ProcessFixture(root, 3, 23))
        {
            File.Delete(Path.Combine(fallback.Commands, "pwsh.exe"));
            fallback.EnvironmentValues["MFTLIB_INIT_REAL_SHELL"] = Path.Combine(Environment.SystemDirectory,
                "WindowsPowerShell/v1.0/powershell.exe");
            var restoreOnly = fallback.Invoke("init.bat", false, worktreeOnly: true);
            TestProgram.Equal(0, restoreOnly.ExitCode, "init.bat -WorktreeOnly exits successfully");
            var calls = fallback.Calls();
            var expected = new[] { "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                Path.Combine(fallback.Repository, "init.ps1"), "-WorktreeOnly" };
            TestProgram.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(calls.Single(call => call[0] == "powershell")),
                "init.bat falls back to Windows PowerShell with exact no-build argv");
            TestProgram.Equal(false, calls.Any(call => call[0] == "pwsh"), "init.bat does not run pwsh when absent");
            AssertWorktreeOnly(fallback, calls);
            TestProgram.Equal(true, calls.Any(call => call.SequenceEqual(new[] { "dotnet", "restore", Path.Combine(fallback.Repository, "MFTLib.sln") })),
                "init.bat -WorktreeOnly restores the solution");
            TestProgram.Equal(false, calls.Any(call => call[0] == "dotnet" && call.ElementAtOrDefault(1) == "build"),
                "init.bat does not add -Build");
            TestProgram.Equal(false, calls.Any(call => call[0] == "MSBuild"), "no-build init.bat skips the native build");
        }
        // Same Windows PowerShell engine through a full init: the optional provisioner failure (helper status 20)
        // must stay a diagnostic and not become the init exit code.
        using (var provisioned = new ProcessFixture(root, 3, 23))
        {
            File.Delete(Path.Combine(provisioned.Commands, "pwsh.exe"));
            provisioned.EnvironmentValues["MFTLIB_INIT_REAL_SHELL"] = Path.Combine(Environment.SystemDirectory,
                "WindowsPowerShell/v1.0/powershell.exe");
            Check(provisioned.Invoke("init.bat", false), provisioned, 3, 23);
        }
        // pwsh present: both flags arrive and the shell double's exact sentinel status propagates without a full init.
        using var failing = new ProcessFixture(root, 3, 0);
        var shell = Path.Combine(failing.Commands, "fixture-shell.exe");
        failing.InstallDouble(shell);
        failing.EnvironmentValues["MFTLIB_INIT_REAL_SHELL"] = shell;
        var result = failing.Invoke("init.bat", true, worktreeOnly: true);
        TestProgram.Equal(37, result.ExitCode, "init.bat propagates the exact failing exit code");
        var failingCalls = failing.Calls();
        var preferred = new[] { "pwsh", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(failing.Repository, "init.ps1"), "-Build", "-WorktreeOnly" };
        TestProgram.Equal(JsonSerializer.Serialize(preferred), JsonSerializer.Serialize(failingCalls.Single(call => call[0] == "pwsh")),
            "init.bat prefers pwsh and forwards both flags exactly");
    }

    private static void CheckProvisioningOutcomes(string root, string entry)
    {
        // Installed provisioners, exit 0: the one run through the real `dotnet run` from the fixture copy, which
        // verifies the copied helper file list, the entry point's delegation and the completion summary.
        using (var real = new ProcessFixture(root, 3, 0))
        {
            real.EnvironmentValues["MFTLIB_INIT_REAL_RUN"] = "1";
            Check(real.Invoke(entry, false), real, 3, 0);
        }
        using (var absent = new ProcessFixture(root, 0, 0))
            Check(absent.Invoke(entry, false), absent, 0, 0);
        using (var failed = new ProcessFixture(root, 3, 23))
        {
            Check(failed.Invoke(entry, true), failed, 3, 23);
            var calls = failed.Calls();
            TestProgram.Equal(true, calls.Any(call => call[0] == "dotnet" && call.ElementAtOrDefault(1) == "build"), "build forwarding");
            TestProgram.Equal(true, calls.Any(call => call[0] == (OperatingSystem.IsWindows() ? "MSBuild" : "cmake")), "native build forwarding");
            if (!OperatingSystem.IsWindows()) CheckManagedSmokeInvocation(failed, calls);
        }
        // Windows init.ps1 branch logic (launcher, restore and build failure) is tested in process by
        // scripts/init.tests.ps1; Linux has no in-process tests, so init.sh keeps these launches.
        if (OperatingSystem.IsWindows()) return;
        using (var launcher = new ProcessFixture(root, 3, 0))
        {
            launcher.EnvironmentValues["MFTLIB_INIT_LAUNCHER_FAILURE"] = "1";
            var result = launcher.Invoke(entry, false);
            TestProgram.Equal(0, result.ExitCode, "launcher failure remains optional");
            TestProgram.Contains("fixture launcher failure", result.Output + result.Error);
            TestProgram.Contains("launcher exited with 31; continuing.", result.Output + result.Error);
            TestProgram.Equal(false, result.Output.Contains(Completion), "no false launcher completion");
        }
        using (var restore = new ProcessFixture(root, 3, 0))
        {
            restore.EnvironmentValues["MFTLIB_INIT_REQUIRED_FAILURE"] = "1";
            var result = restore.Invoke(entry, false);
            TestProgram.Equal(true, result.ExitCode != 0, "required restore still fails init");
            TestProgram.Equal(false, restore.Calls().Any(call => call[0].StartsWith("schoen-lab", StringComparison.Ordinal)), "restore failure stops provisioning");
        }
        using (var build = new ProcessFixture(root, 3, 23))
        {
            build.EnvironmentValues["MFTLIB_INIT_BUILD_FAILURE"] = "1";
            var result = build.Invoke(entry, true);
            TestProgram.Equal(true, result.ExitCode != 0, "required build still fails init");
            TestProgram.Equal(true, build.Calls().Any(call => call[0] == "schoen-lab-onboard"), "optional failure preceded required build failure");
            TestProgram.Equal(false, build.Calls().Any(call => call[0] == "dotnet" && call.ElementAtOrDefault(1) == "test"),
                "managed Smoke tier does not run after a failed native build");
        }
    }

    internal static int Run(string root)
    {
        TestProgram.Policy();
        LauncherTests.Run();
        CheckDotnetUserPathIsolation(root);
        CheckDotnetBackgroundNetworkIsolation(root);
        CheckRestrictiveDisposal(root);

        ProcessFixture.BuildSharedHelper(root);
        var entry = OperatingSystem.IsWindows() ? "init.ps1" : "init.sh";
        CheckProvisioningOutcomes(root, entry);
        // Windows worktree-only paths are tested in process (scripts/init.tests.ps1) and through init.bat in CheckBatchEntry.
        if (!OperatingSystem.IsWindows()) CheckWorktreeOnly(root, entry);
        if (OperatingSystem.IsWindows())
        {
            CheckBatchEntry(root);
            using var errorPreferenceFixture = new ProcessFixture(root, 3, 23);
            var script = """
                $PSNativeCommandUseErrorActionPreference = $true
                $env:MFTLIB_INIT_REQUIRED_FAILURE = '1'
                try {
                    & './init.ps1' | ForEach-Object { "failure pipeline: $_" }
                    throw 'Expected restore to terminate init'
                } catch [System.Management.Automation.NativeCommandExitException] {
                    if ($_.Exception.ExitCode -ne 17) { throw }
                }
                Remove-Item Env:MFTLIB_INIT_REQUIRED_FAILURE
                & './init.ps1' | ForEach-Object { "success pipeline: $_" }
                exit $LASTEXITCODE
                """;
            var result = ProcessFixture.Run(ProcessFixture.Host, new[] { "-NoProfile", "-Command", script },
                errorPreferenceFixture.Repository, errorPreferenceFixture.EnvironmentValues);
            Check(result, errorPreferenceFixture, 3, 23);
            TestProgram.Contains("failure pipeline: fixture dotnet restore output", result.Output);
            var restore = result.Output.IndexOf("success pipeline: fixture dotnet restore output", StringComparison.Ordinal);
            TestProgram.Equal(true, restore >= 0 && restore < result.Output.IndexOf("Done.", StringComparison.Ordinal),
                "restore stdout reaches the caller's success stream before the summary");
        }
        Console.WriteLine("Init adapter regression tests passed.");
        return 0;
    }
}
