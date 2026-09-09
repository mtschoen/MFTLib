namespace MFTLib.Index;

public sealed partial class FileIndex
{
    void CleanupRetiredSiblings(char driveLetter, uint volumeSerial)
    {
        var pattern = CacheDirectory.BlockFileName(driveLetter, volumeSerial) + ".retired-*";
        try
        {
            foreach (var path in Directory.EnumerateFiles(CacheDirectoryPath, pattern))
            {
                TryDeleteBestEffort(path);
            }
        }
        catch (IOException)
        {
            // Best effort: an inaccessible cache directory is reported by the warm-start
            // attempt that follows, not here.
        }
        catch (UnauthorizedAccessException)
        {
            // Same reasoning as the IOException case above.
        }
    }

    static void CleanupStaleNoCacheBlocks(char driveLetter, uint volumeSerial)
    {
        var pattern = $"mftlib-nocache-*-{CacheDirectory.BlockFileName(driveLetter, volumeSerial)}";
        try
        {
            foreach (var path in Directory.EnumerateFiles(Path.GetTempPath(), pattern))
            {
                TryDeleteBestEffort(path);
            }
        }
        catch (IOException)
        {
            // Guards Directory.EnumerateFiles itself (for example the temp directory is
            // briefly inaccessible), not the deletes it drives: a leftover another running
            // instance still has mapped is not a concern here, because BlockFile opens with
            // FileShare.Delete, so unlinking it succeeds and that instance keeps reading its
            // own mapping undisturbed. A leftover that genuinely cannot be deleted is retried
            // on the next open.
        }
        catch (UnauthorizedAccessException)
        {
            // Same reasoning as the IOException case above.
        }
    }

    static void TryDeleteBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Whatever still needs this file surfaces its own error; a leftover here is either
            // rejected again next time or, for a leftover still in use, simply left alone.
        }
        catch (UnauthorizedAccessException)
        {
            // Same reasoning as the IOException case above.
        }
    }
}
