using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;

namespace MFTLib.Index;

/// <summary>
///     Client-created, file-backed named memory-mapped sections over block files. The non-elevated
///     client creates the named section and initializes its header, then the elevated broker opens
///     the section by name and writes rows directly into it, so the cold scan and the cache save
///     are one act. Section names are unqualified and session-local.
/// </summary>
[SupportedOSPlatform("windows")]
public static class NamedBlockSection
{
    [SupportedOSPlatform("windows")]
    public static (BlockFile Block, IDisposable Lifetime) Create(BlockFileCreateOptions options, string sectionName)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(sectionName);

        var length = BlockLayout.TotalBlockBytes(options.SlotCapacity, options.NamePoolCapacity);
        var fileStream = new FileStream(options.Path, FileMode.Create, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        MemoryMappedFile? mappedFile = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            mappedFile = MemoryMappedFile.CreateFromFile(fileStream, sectionName, length,
                MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
            view = mappedFile.CreateViewAccessor(0, length, MemoryMappedFileAccess.ReadWrite);
            var block = BlockFile.BuildAndInitialize(options, length, mappedFile, view);
            return (block, mappedFile);
        }
        catch
        {
            view?.Dispose();
            mappedFile?.Dispose();
            if (mappedFile is null)
            {
                fileStream.Dispose();
            }

            BlockFile.TryDeleteFailedCreate(options.Path);
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    public static BlockFile OpenExisting(string sectionName, long expectedLength)
    {
        ArgumentException.ThrowIfNullOrEmpty(sectionName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedLength);

        var mappedFile = MemoryMappedFile.OpenExisting(sectionName, MemoryMappedFileRights.ReadWrite);
        MemoryMappedViewAccessor? view = null;
        try
        {
            view = mappedFile.CreateViewAccessor(0, expectedLength, MemoryMappedFileAccess.ReadWrite);
            return new BlockFile(mappedFile, view, expectedLength);
        }
        catch
        {
            view?.Dispose();
            mappedFile.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    public static string BuildSectionName(char driveLetter)
    {
        return "mftlib-block-" + char.ToUpperInvariant(driveLetter) + "-" + Guid.NewGuid().ToString("N");
    }
}
