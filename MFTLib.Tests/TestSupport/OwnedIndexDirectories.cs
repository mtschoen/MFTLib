namespace MFTLib.Tests.TestSupport;

/// <summary>Owns the isolated tree and cache paths used by an Index test fixture.</summary>
internal sealed class OwnedIndexDirectories
{
    public string TreeRoot { get; } = Path.Combine(Path.GetTempPath(), $"mftlib-tree-{Guid.NewGuid():N}");

    public string CacheDirectory { get; } = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");

    public void Dispose()
    {
        foreach (var directory in new[] { TreeRoot, CacheDirectory })
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // A just-unmapped block file can stay locked briefly on Windows.
            }
        }
    }
}
