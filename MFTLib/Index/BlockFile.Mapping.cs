using System.IO.MemoryMappedFiles;

namespace MFTLib.Index;

public sealed partial class BlockFile
{
    /// <summary>
    ///     Opens the backing file and builds the memory mapping and view together, so a failure at
    ///     any step disposes whichever of the three was already constructed before rethrowing.
    ///     <see cref="MemoryMappedFile.CreateFromFile(FileStream, string, long, MemoryMappedFileAccess, HandleInheritability, bool)" />
    ///     does not dispose the stream it was given if construction fails, regardless of
    ///     <c>leaveOpen</c>, so without this the caller's <see cref="FileStream" /> would leak a
    ///     write-locked handle on the block file whenever the mapping itself could not be built.
    ///     Internal rather than private so a regression test can provoke the failure directly and
    ///     verify the file becomes unlocked afterward.
    /// </summary>
    internal static (MemoryMappedFile MappedFile, MemoryMappedViewAccessor View) OpenMapping(
        string path, FileMode fileMode, long mappingCapacity, long viewLength, FileOptions fileOptions)
    {
        var fileStream = new FileStream(path, fileMode, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096, options: fileOptions);
        MemoryMappedFile? mappedFile = null;
        try
        {
            mappedFile = MemoryMappedFile.CreateFromFile(fileStream, mapName: null, mappingCapacity,
                MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
            var view = mappedFile.CreateViewAccessor(0, viewLength, MemoryMappedFileAccess.ReadWrite);
            return (mappedFile, view);
        }
        catch
        {
            mappedFile?.Dispose();
            if (mappedFile is null)
            {
                fileStream.Dispose();
            }

            throw;
        }
    }

    internal static void TryDeleteFailedCreate(string path, Action<string>? diagnostics = null,
        string reason = "block creation or header initialization failed")
    {
        try
        {
            File.Delete(path);
            diagnostics?.Invoke($"Deleted block file '{path}': {reason}.");
        }
        catch (IOException)
        {
            // Best effort, and only ever a cleanup: the caller's own exception is the one worth
            // surfacing, and a header-less file left behind is rejected by the next Open anyway.
        }
        catch (UnauthorizedAccessException)
        {
            // Same reasoning as the IOException case above.
        }
    }
}
