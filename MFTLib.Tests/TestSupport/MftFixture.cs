using System.Text;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     MFTLib.Tests support for the native deterministic metadata and freed-record fixture:
///     24 hand-authored 1024-byte records with sizes, raw timestamps and attribute shapes.
/// </summary>
internal static class MftFixture
{
    internal static void Generate(string filePath)
    {
        var pathBytes = Encoding.UTF8.GetBytes(filePath + '\0');
        if (!NativeTestHooks.NativeGenerateFixtureMFTUtf8(pathBytes))
        {
            throw new InvalidOperationException($"GenerateFixtureMFT failed for {filePath}");
        }
    }
}
