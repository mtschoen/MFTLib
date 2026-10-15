internal static class AcceptanceTests
{
    internal static int Run(string sourceRoot)
    {
        using var fixture = new ProcessFixture(sourceRoot, 3, 0, realDependencies: true);
        var git = ProcessFixture.FindHost("git" + ProcessFixture.ExecutableSuffix);
        var clone = ProcessFixture.Run(git, new[] { "clone", "--local", "--no-hardlinks", sourceRoot, fixture.Repository }, sourceRoot);
        TestProgram.Equal(0, clone.ExitCode, clone.Output + clone.Error);
        var clean = ProcessFixture.Run(git, new[] { "clean", "-ffxd" }, fixture.Repository);
        TestProgram.Equal(0, clean.ExitCode, clean.Output + clean.Error);
        var head = ProcessFixture.Run(git, new[] { "rev-parse", "HEAD" }, fixture.Repository);
        TestProgram.Equal(0, head.ExitCode, head.Error);
        Console.WriteLine($"Acceptance HEAD: {head.Output.Trim()} platform={Environment.OSVersion.Platform}");
        var entries = OperatingSystem.IsWindows() ? new[] { "init.ps1", "init.bat" } : new[] { "init.sh" };
        foreach (var entry in entries)
        foreach (var build in new[] { false, true })
        foreach (var provisionerExit in new[] { 0, 23 })
        {
            File.WriteAllText(fixture.Log, "");
            fixture.EnvironmentValues["MFTLIB_INIT_PROVISIONER_EXIT"] = provisionerExit.ToString();
            var result = fixture.Invoke(entry, build);
            Console.Write(result.Output);
            Console.Error.Write(result.Error);
            TestProgram.Equal(0, result.ExitCode, $"{entry} build={build} exit={provisionerExit}");
            var selected = fixture.Calls().Where(call => call[0] is "schoen-lab-onboard" or "schoen-lab").ToArray();
            TestProgram.Equal(1, selected.Length, "real-init doubled provisioning");
            TestProgram.Equal("schoen-lab-onboard|apply|auto-memory", string.Join('|', selected.Single()), "real-init argv");
            TestProgram.Equal(false, fixture.Calls().Any(call => call[0] == "onboard"), "poison untouched");
            TestProgram.Equal(provisionerExit == 0 ? 1 : 0,
                result.Output.Split('\n').Count(line => line.Trim() == AdapterTests.Completion), "real-init completion");
            var status = ProcessFixture.Run(git, new[] { "status", "--porcelain" }, fixture.Repository);
            TestProgram.Equal(0, status.ExitCode, status.Error);
            TestProgram.Equal("", status.Output, "owned checkout remains clean");
            if (build)
            {
                TestProgram.Equal(true, File.Exists(Path.Combine(fixture.Repository,
                    "MFTLib.Tests/bin/x64/Release/net10.0/MFTLib.Tests.dll")), "managed build artifact");
                TestProgram.Equal(true, File.Exists(Path.Combine(fixture.Repository, OperatingSystem.IsWindows()
                    ? "x64/Release/MFTLibNative.dll" : "build/linux/libMFTLibNative.so")), "native build artifact");
            }
            Console.WriteLine($"Acceptance passed: {entry} build={build} optionalExit={provisionerExit} " +
                $"initExit={result.ExitCode} trackedState=clean nativeOutput={(build ? "present" : "not-required")} " +
                $"managedOutput={(build ? "present" : "not-required")}");
        }
        Console.WriteLine("Disposable checkout init acceptance passed.");
        return 0;
    }
}
