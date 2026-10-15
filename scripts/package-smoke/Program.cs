using System.Diagnostics;
using System.Runtime.InteropServices;
using MFTLib;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;

// Loads a synthetic MFT dump through the packaged library and checks the native library came from the package.
const string NotesPath = "dump:/D/documents/Notes.txt";

if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) ||
    RuntimeInformation.ProcessArchitecture != Architecture.X64)
{
    return Fail("The package smoke requires a Windows or Linux x64 process.");
}

var nativeLibraryName = OperatingSystem.IsWindows() ? "MFTLibNative.dll" : "libMFTLibNative.so";
var runtimeIdentifier = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
var nativeDirectory = Path.Combine(AppContext.BaseDirectory, "runtimes", runtimeIdentifier, "native");
var expectedNativePath = Path.GetFullPath(Path.Combine(nativeDirectory, nativeLibraryName));
if (!File.Exists(expectedNativePath))
{
    return Fail($"The package native library is not in the output at {nativeDirectory}.");
}

if (File.Exists(Path.Combine(AppContext.BaseDirectory, nativeLibraryName)))
{
    return Fail("The native library was copied flat next to the application, so the smoke test would not prove package resolution.");
}

var directory = Path.Combine(Path.GetTempPath(), "mftlib-package-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var dumpPath = MftDumpFixture.WriteFile(directory, MftDumpFixture.Standard());
    await using var index = await FileIndex.OpenAsync(new FileIndexOptions
    {
        MftSource = MftIndexSources.FromMftDumpFile(dumpPath, 'D')
    }, CancellationToken.None);

    var notes = index.Find(NotesPath);
    if (notes is null || notes.Value.Size != 11)
    {
        return Fail($"{NotesPath} was not indexed with its 11 bytes.");
    }

    if (OperatingSystem.IsWindows())
    {
        using var process = Process.GetCurrentProcess();
        var nativeModule = process.Modules.Cast<ProcessModule>().SingleOrDefault(module =>
            string.Equals(module.ModuleName, nativeLibraryName, StringComparison.OrdinalIgnoreCase));
        if (nativeModule is null || !string.Equals(
                Path.GetFullPath(nativeModule.FileName), expectedNativePath, StringComparison.OrdinalIgnoreCase))
        {
            return Fail($"Native module resolved to '{nativeModule?.FileName ?? "<not loaded>"}', expected '{expectedNativePath}'.");
        }
    }

    Console.WriteLine($"Package smoke passed: {NotesPath} indexed from the package native library in {nativeDirectory}.");
    return 0;
}
finally
{
    Directory.Delete(directory, true);
}

static int Fail(string message)
{
    Console.Error.WriteLine("Package smoke failed: " + message);
    return 1;
}
