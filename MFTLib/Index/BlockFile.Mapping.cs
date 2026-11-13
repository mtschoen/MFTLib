using System.IO.MemoryMappedFiles;

namespace MFTLib.Index;

sealed partial class BlockFile
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

    /// <summary>
    ///     Unmaps the view. Disposing a writable <see cref="MemoryMappedViewAccessor" /> first calls
    ///     <c>FlushViewOfFile</c> over the whole view, a synchronous write of every dirty page. A
    ///     delete-on-close block is never read back from disk (every mapping of its file shares the
    ///     page cache), so its view handle is disposed alone: that only unmaps, and the lazy writer
    ///     owns the dirty pages. Every other block keeps the accessor's flushing disposal.
    /// </summary>
    void ReleaseView()
    {
        Hooks._viewReleased?.Invoke(!DeletesOnClose);
        if (DeletesOnClose)
        {
            _view.SafeMemoryMappedViewHandle.Dispose();
            return;
        }

        _view.Dispose();
    }

    internal static void TryDeleteFailedCreate(string path, Action<string>? diagnostics = null,
        string reason = "block creation or header initialization failed")
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort, and only ever a cleanup: the caller's own exception is the one worth
            // surfacing, and a header-less file left behind is rejected by the next Open anyway.
            return;
        }
        catch (UnauthorizedAccessException)
        {
            // Same reasoning as the IOException case above.
            return;
        }

        DiagnosticsReport.Send(diagnostics, $"Deleted block file '{path}': {reason}.");
    }
}
