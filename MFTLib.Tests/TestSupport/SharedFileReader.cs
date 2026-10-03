namespace MFTLib.Tests.TestSupport;

internal static class SharedFileReader
{
    public static async Task<byte[]> ReadAllBytesAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes);
        return bytes;
    }
}
