using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;

namespace MFTLib.Index;

/// <summary>
///     Client-created, file-backed named memory-mapped sections over block files. The non-elevated
///     client creates the named section and initializes its header, then the elevated broker opens
///     the section by name and writes rows directly into it, so the cold scan and the cache save
///     are one act. Section names are unqualified and session-local.
/// </summary>
static class NamedBlockSection
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

        var length = BlockLayout.TotalBlockBytes(options.SlotCapacity, options.SlotCapacity, options.NamePoolCapacity);
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

                BlockFile.TryDeleteFailedCreate(options.Path, options.Diagnostics,
                    "block creation or section initialization failed");
            }
        }
    }

    /// <summary>
    ///     Opens a client-created section for writing. The whole section is mapped, so the view's
    ///     length is the section's own size rather than anything its header claims. One private copy
    ///     of the header is then validated against that length, and the block is built from the
    ///     copy alone: a process that rewrites the shared header afterwards cannot move the regions
    ///     the block writes into.
    /// </summary>
    /// <exception cref="InvalidDataException">The section's header does not describe a block that exactly fills it.</exception>
    [SupportedOSPlatform("windows")]
    public static BlockFile OpenExisting(string sectionName)
    {
        ArgumentException.ThrowIfNullOrEmpty(sectionName);

        var mappedFile = MemoryMappedFile.OpenExisting(sectionName, MemoryMappedFileRights.ReadWrite);
        MemoryMappedViewAccessor? view = null;
        try
        {
            view = mappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            var length = view.Capacity;
            if (length < BlockLayout.HeaderRegionBytes)
            {
                throw RejectedSection(sectionName, BlockValidationResult.WrongMagic);
            }

            view.Read(0, out BlockHeader layout);
            if (BlockHeader.ValidateSection(in layout, length) is { } validation)
            {
                throw RejectedSection(sectionName, validation);
            }

            var block = new BlockFile(mappedFile, view, length, layout);
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

    static InvalidDataException RejectedSection(string sectionName, BlockValidationResult validation)
    {
        return new InvalidDataException($"Block section '{sectionName}' was rejected: {validation}.");
    }

    public static string BuildSectionName(VolumeKey volume)
    {
        return BlockSectionNaming.Prefix + volume + "-" + Guid.NewGuid().ToString("N");
    }
}
