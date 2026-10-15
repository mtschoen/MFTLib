using System.ComponentModel;

internal static class ProvisioningDispatcher
{
    internal static int Run(Func<string, string?> find,
        Func<string, IReadOnlyList<string>, int> launch,
        TextWriter output, TextWriter diagnostics)
    {
        try
        {
            foreach (var candidate in new[] { "schoen-lab-onboard", "schoen-lab" })
            {
                var path = find(candidate);
                if (path is null) continue;
                diagnostics.WriteLine("Re-applying provisioned project settings...");
                var status = launch(path, new[] { "apply", "auto-memory" });
                if (status != 0)
                {
                    diagnostics.WriteLine($"{candidate} apply auto-memory exited with {status}; continuing.");
                    return 20;
                }
                output.WriteLine("Project-scope provisioned settings re-applied");
                return 0;
            }
            return 10;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException
            or UnauthorizedAccessException)
        {
            diagnostics.WriteLine($"Optional provisioning failed: {exception.Message}; continuing.");
            return 20;
        }
    }
}
