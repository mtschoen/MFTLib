using MFTLib;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;

// Loads a synthetic MFT dump through the packaged library and checks the native library came from the package.
const string NotesPath = "dump:/D/documents/Notes.txt";
const string NativeLibraryName = "libMFTLibNative.so";

var nativeDirectory = Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native");
if (!File.Exists(Path.Combine(nativeDirectory, NativeLibraryName)))
{
    return Fail($"The package native library is not in the output at {nativeDirectory}.");
}

if (File.Exists(Path.Combine(AppContext.BaseDirectory, NativeLibraryName)))
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
        Drives = [new IndexedDrive('D', "dump:/D", 0)],
        NoCache = true,
        MftSource = MftIndexSources.FromMftDumpFile(dumpPath, 'D')
    }, CancellationToken.None);

    var notes = index.Find(NotesPath);
    if (notes is null || notes.Value.Size != 11)
    {
        return Fail($"{NotesPath} was not indexed with its 11 bytes.");
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
