using System.ComponentModel;
using System.Text.Json;

internal static class TestProgram
{
    internal static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
    }

    internal static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Missing '{expected}' in '{actual}'");
    }

    internal static int Policy()
    {
        foreach (var installed in new[] { 0, 1, 2, 3 })
        foreach (var exitCode in new[] { 0, 23 })
        {
            var probes = new List<string>();
            var calls = new List<string[]>();
            using var output = new StringWriter();
            using var diagnostics = new StringWriter();
            string? Find(string name)
            {
                probes.Add(name);
                return name switch
                {
                    "schoen-lab-onboard" when (installed & 1) != 0 => "/tools with spaces/preferred",
                    "schoen-lab" when (installed & 2) != 0 => "/tools with spaces/fallback",
                    _ => null
                };
            }
            int Launch(string path, IReadOnlyList<string> arguments)
            {
                calls.Add(new[] { path }.Concat(arguments).ToArray());
                return exitCode;
            }
            var status = ProvisioningDispatcher.Run(Find, Launch, output, diagnostics);
            Equal(installed == 0 ? 10 : exitCode == 0 ? 0 : 20, status, "status");
            Equal(false, probes.Contains("onboard"), "unqualified probe");
            Equal((installed & 1) != 0 ? 1 : 2, probes.Count, "preference short circuit");
            Equal(installed == 0 ? 0 : 1, calls.Count, "one selected command");
            if (installed != 0)
            {
                var selected = (installed & 1) != 0 ? "preferred" : "fallback";
                Equal(JsonSerializer.Serialize(new[] { "/tools with spaces/" + selected, "apply", "auto-memory" }),
                    JsonSerializer.Serialize(calls.Single()), "exact argument vector");
                Contains("Re-applying provisioned project settings...", diagnostics.ToString());
                if (exitCode != 0) Contains("exited with 23; continuing.", diagnostics.ToString());
            }
            else Equal("", diagnostics.ToString(), "absent is silent");
            Equal(installed != 0 && exitCode == 0
                ? "Project-scope provisioned settings re-applied" + Environment.NewLine : "",
                output.ToString(), "completion only for success");
        }
        using var failureOutput = new StringWriter();
        using var failureDiagnostics = new StringWriter();
        Equal(20, ProvisioningDispatcher.Run(_ => "/broken executable",
            (_, _) => throw new Win32Exception("fixture cannot start"),
            failureOutput, failureDiagnostics), "launch exception");
        Equal("", failureOutput.ToString(), "failed launch has no completion");
        Contains("fixture cannot start", failureDiagnostics.ToString());
        Console.WriteLine("Optional provisioning policy tests passed.");
        return 0;
    }

    public static int Main(string[] arguments)
    {
        if (Environment.GetEnvironmentVariable("MFTLIB_INIT_FIXTURE") == "1")
            return ProcessFixture.FakeCommand(arguments);
        if (arguments.SequenceEqual(new[] { "--policy" })) return Policy();
        if (arguments.Length != 2) throw new ArgumentException("Expected mode and repository root");
        return arguments[0] switch
        {
            "--adapters" => AdapterTests.Run(Path.GetFullPath(arguments[1])),
            "--acceptance" => AcceptanceTests.Run(Path.GetFullPath(arguments[1])),
            _ => throw new ArgumentException("Unknown regression mode")
        };
    }
}
