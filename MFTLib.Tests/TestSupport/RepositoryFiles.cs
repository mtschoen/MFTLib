using System.Diagnostics;

namespace MFTLib.Tests.TestSupport;

internal static class RepositoryFiles
{
    internal static string Root { get; } = FindRoot();

    internal static string Read(string path) => File.ReadAllText(Path.Combine(Root, path));

    internal static string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start Git.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var text = output.GetAwaiter().GetResult();
        var diagnostic = error.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Git {string.Join(' ', arguments)} failed: {diagnostic}");
        }

        return text;
    }

    static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var marker = Path.Combine(directory.FullName, ".git");
            if (File.Exists(marker) || Directory.Exists(marker))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Tests require a Git checkout.");
    }
}
