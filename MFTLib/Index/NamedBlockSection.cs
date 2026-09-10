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
    /// <summary>Creates an initialized block and the lifetime of its published section name.</summary>
    /// <remarks>
    ///     The returned Lifetime is not an independent resource: it is the very
    ///     <see cref="MemoryMappedFile" /> instance already passed to
    ///     <see cref="BlockFile.BuildAndInitialize" /> and stored by the returned
    ///     <see cref="BlockFile" />, which is an owner-confirmed design rather than an oversight.
    ///     Disposing the Lifetime early only unpublishes the named section, preventing further
    ///     opens by name; it does not tear down the mapping, because the Block's own view
    ///     accessor holds its own reference and Windows keeps a memory-mapped section alive while
    ///     any view of it remains mapped. The Block also disposes this same
    ///     <see cref="MemoryMappedFile" /> in its own Dispose, which is safe because a second
    ///     dispose of an already-disposed safe handle is a no-op.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public static (BlockFile Block, IDisposable Lifetime) Create(BlockFileCreateOptions options, string sectionName)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(sectionName);

        var length = BlockLayout.TotalBlockBytes(options.SlotCapacity, options.NamePoolCapacity);
        var fileStream = new FileStream(options.Path, FileMode.Create, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096,
            options: options.DeleteOnClose ? FileOptions.DeleteOnClose : FileOptions.None);
        MemoryMappedFile? mappedFile = null;
        MemoryMappedViewAccessor? view = null;
        BlockFile? block = null;
        var ownershipTransferred = false;
        try
        {
            mappedFile = MemoryMappedFile.CreateFromFile(fileStream, sectionName, length,
                MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
            view = mappedFile.CreateViewAccessor(0, length, MemoryMappedFileAccess.ReadWrite);
            block = BlockFile.BuildAndInitialize(options, length, mappedFile, view);
            var result = (block, mappedFile);
            block = null;
            view = null;
            mappedFile = null;
            ownershipTransferred = true;
            return result;
        }
        finally
        {
            block?.Dispose();
            view?.Dispose();
            mappedFile?.Dispose();
            if (!ownershipTransferred)
            {
                if (mappedFile is null)
                {
                    fileStream.Dispose();
                }

                BlockFile.TryDeleteFailedCreate(options.Path);
            }
        }
    }

    /// <summary>Opens a section using the capacities in its client-initialized header to determine its length.</summary>
    [SupportedOSPlatform("windows")]
    public static BlockFile OpenExisting(string sectionName)
    {
        ArgumentException.ThrowIfNullOrEmpty(sectionName);
        long expectedLength;
        using (var probe = MemoryMappedFile.OpenExisting(sectionName, MemoryMappedFileRights.Read))
        using (var headerView = probe.CreateViewAccessor(0, BlockLayout.HeaderRegionBytes, MemoryMappedFileAccess.Read))
        {
            headerView.Read(0, out BlockHeader header);
            expectedLength = BlockLayout.TotalBlockBytes(header.SlotCapacity, header.NamePoolCapacity);
        }

        return OpenExisting(sectionName, expectedLength);
    }

    [SupportedOSPlatform("windows")]
    public static BlockFile OpenExisting(string sectionName, long expectedLength)
    {
        ArgumentException.ThrowIfNullOrEmpty(sectionName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedLength);

        MemoryMappedFile? mappedFile = MemoryMappedFile.OpenExisting(sectionName, MemoryMappedFileRights.ReadWrite);
        MemoryMappedViewAccessor? view = null;
        try
        {
            view = mappedFile.CreateViewAccessor(0, expectedLength, MemoryMappedFileAccess.ReadWrite);
            var block = new BlockFile(mappedFile, view, expectedLength);
            mappedFile = null;
            view = null;
            return block;
        }
        finally
        {
            view?.Dispose();
            mappedFile?.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    public static string BuildSectionName(char driveLetter)
    {
        return "mftlib-block-" + char.ToUpperInvariant(driveLetter) + "-" + Guid.NewGuid().ToString("N");
    }
}
