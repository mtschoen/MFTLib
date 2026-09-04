using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

namespace MFTLib.Index;

/// <summary>
///     One block file mapped into memory. The pointer is acquired once and held for the
///     object's lifetime, so a property read is a pointer offset and touches only the pages it
///     actually reads. Named sections are a Windows broker concern and are not used here,
///     which is what keeps this type usable on Linux.
/// </summary>
public sealed unsafe class BlockFile : IDisposable
{
    readonly MemoryMappedFile _mappedFile;
    readonly MemoryMappedViewAccessor _view;

    // Volatile so the disposal flag and the base pointer are read and written in program order
    // across threads: without it the guards below may observe _disposed as false while already
    // seeing a null _base, or a stale cached pointer. This orders the two against each other; it
    // does not make a query safe to overlap a dispose. See FileIndex.DisposeAsync.
    volatile byte* _base;
    volatile bool _disposed;

    BlockFile(string path, long length, bool deleteOnClose, MemoryMappedFile mappedFile,
        MemoryMappedViewAccessor view)
    {
        Path = path;
        Length = length;
        DeleteOnClose = deleteOnClose;
        _mappedFile = mappedFile;
        _view = view;
        byte* pointer = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _base = pointer;
    }

    public string Path { get; }

    public long Length { get; }

    public bool DeleteOnClose { get; }

    public ref BlockHeader Header
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ref Unsafe.AsRef<BlockHeader>(_base);
        }
    }

    /// <summary>The whole row region, <c>SlotCapacity</c> rows long, not only the used rows.</summary>
    public Span<FileRow> Rows
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new Span<FileRow>(_base + BlockLayout.RowRegionOffset, (int)Header.SlotCapacity);
        }
    }

    /// <summary>The whole name pool as UTF-16 code units. Row name offsets are in bytes.</summary>
    public Span<char> NamePoolCharacters
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new Span<char>(_base + (long)Header.NamePoolOffset,
                (int)(Header.NamePoolCapacity / sizeof(char)));
        }
    }

    public static BlockFile Create(BlockFileCreateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var length = BlockLayout.TotalBlockBytes(options.SlotCapacity, options.NamePoolCapacity);
        var (mappedFile, view) = OpenMapping(options.Path, FileMode.Create, length, length);
        return BuildAndInitialize(options, length, mappedFile, view);
    }

    /// <summary>
    ///     Takes ownership of a freshly built mapping and turns it into an initialized block. If
    ///     either step throws, everything this attempt created is torn down before the exception
    ///     propagates: the view and the mapping are disposed, and the file is deleted, because
    ///     <see cref="FileMode.Create" /> has already truncated whatever was at that path and what
    ///     remains is a block with no valid header that the next <see cref="Open" /> could only
    ///     reject. Deleting matters most in cache mode, where nothing else would ever remove it:
    ///     it would sit at the canonical path and cost every later open a needless rejection.
    ///     Internal rather than private so a regression test can provoke the failure directly and
    ///     verify that neither a handle nor a file survives it.
    /// </summary>
    internal static BlockFile BuildAndInitialize(BlockFileCreateOptions options, long length,
        MemoryMappedFile mappedFile, MemoryMappedViewAccessor view)
    {
        BlockFile? block = null;
        try
        {
            block = new BlockFile(options.Path, length, options.DeleteOnClose, mappedFile, view);
            block.InitializeHeader(options);
            return block;
        }
        catch
        {
            if (block is null)
            {
                // The constructor never returned, so nothing else owns these two yet.
                view.Dispose();
                mappedFile.Dispose();
            }
            else
            {
                block.Dispose();
            }

            TryDeleteFailedCreate(options.Path);
            throw;
        }
    }

    /// <summary>
    ///     Builds a block over an already-built mapping and view without touching the header,
    ///     used by an opener (such as an elevated broker) that writes rows into a section created
    ///     by another process.
    /// </summary>
    internal BlockFile(MemoryMappedFile mappedFile, MemoryMappedViewAccessor view, long length)
        : this(string.Empty, length, deleteOnClose: false, mappedFile, view)
    {
    }

    /// <summary>
    ///     Maps an existing block and validates it. A rejected block returns null with the reason
    ///     in <paramref name="validation" />, and the caller discards the file and rescans. A
    ///     missing or unreadable file reports <see cref="BlockValidationResult.WrongMagic" />
    ///     rather than throwing, because "there is no usable block here" is one outcome with one
    ///     response.
    /// </summary>
    public static BlockFile? Open(string path, uint expectedVolumeSerial, out BlockValidationResult validation)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        long length;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < BlockLayout.HeaderRegionBytes)
            {
                validation = BlockValidationResult.WrongMagic;
                return null;
            }

            length = info.Length;
        }
        catch (IOException)
        {
            validation = BlockValidationResult.WrongMagic;
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // A block the process may not read is "no usable block here" exactly like a missing
            // one: the caller discards it and cold-scans. See this method's summary.
            validation = BlockValidationResult.WrongMagic;
            return null;
        }

        return OpenMapped(path, expectedVolumeSerial, length, out validation);
    }

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _view.Flush();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_base is not null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _base = null;
        }

        _view.Dispose();
        _mappedFile.Dispose();

        if (!DeleteOnClose)
        {
            return;
        }

        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
            // A no-cache block that survives one process exit is a leftover in the temp
            // directory, not a correctness problem. Rethrowing would turn a benign sharing
            // violation on shutdown into a failed dispose.
        }
    }

    static BlockFile? OpenMapped(string path, uint expectedVolumeSerial, long length,
        out BlockValidationResult validation)
    {
        BlockFile? block = null;
        try
        {
            var (mappedFile, view) = OpenMapping(path, FileMode.Open, mappingCapacity: 0, length);
            block = new BlockFile(path, length, deleteOnClose: false, mappedFile, view);
            validation = BlockHeader.Validate(in block.Header, expectedVolumeSerial, length);
            if (validation == BlockValidationResult.Valid)
            {
                return block;
            }

            block.Dispose();
            return null;
        }
        catch (IOException)
        {
            block?.Dispose();
            validation = BlockValidationResult.WrongMagic;
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // The FileStream inside OpenMapping throws this for a block file the process may not
            // open for read and write: a permission-denied cache file, or one another user owns.
            // Same outcome as a corrupt block, so the caller discards it and cold-scans.
            block?.Dispose();
            validation = BlockValidationResult.WrongMagic;
            return null;
        }
    }

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
        string path, FileMode fileMode, long mappingCapacity, long viewLength)
    {
        var fileStream = new FileStream(path, fileMode, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
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

    internal static void TryDeleteFailedCreate(string path)
    {
        try
        {
            File.Delete(path);
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

    void InitializeHeader(BlockFileCreateOptions options)
    {
        ref var header = ref Header;
        header = default;
        header.Magic = BlockLayout.Magic;
        header.FormatVersion = BlockLayout.FormatVersion;
        header.ProducerKind = options.ProducerKind;
        header.Flags = BlockFlags.None;
        header.VolumeSerial = options.VolumeSerial;
        header.RootRow = options.RootRow;
        header.SlotCapacity = options.SlotCapacity;
        header.NamePoolCapacity = options.NamePoolCapacity;
        header.RowRegionOffset = BlockLayout.RowRegionOffset;
        header.NamePoolOffset = (ulong)BlockLayout.NamePoolOffset(options.SlotCapacity);
    }
}
