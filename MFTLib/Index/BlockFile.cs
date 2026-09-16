using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

namespace MFTLib.Index;

/// <summary>
///     One block file mapped into memory. The pointer is acquired once and held for the
///     object's lifetime, so a property read is a pointer offset and touches only the pages it
///     actually reads. Named sections are a Windows broker concern and are not used here,
///     which is what keeps this type usable on Linux.
///     Writes through <see cref="BlockWriter" /> hold a <see cref="BlockAccessScope" /> for each
///     operation, and <see cref="Dispose" /> waits for outstanding scopes before unmapping, so a
///     write racing disposal either finishes against mapped memory or, when disposal began first,
///     fails with <see cref="ObjectDisposedException" />. Raw property reads are not part of that
///     guarantee: their check-then-use pattern protects a single owner, and readers are expected
///     to hold a snapshot borrow instead.
/// </summary>
public sealed unsafe class BlockFile : IDisposable
{
    readonly MemoryMappedFile _mappedFile;
    readonly MemoryMappedViewAccessor _view;

    /// <summary>
    ///     Guards <see cref="_activeAccessCount" />, <see cref="_disposeStarted" /> and
    ///     <see cref="_accessDrained" />. A writer admitted just before disposal begins is counted
    ///     and waited for; one that arrives just after is refused. See <see cref="TryTakeAccess" />
    ///     and <see cref="Dispose" />.
    /// </summary>
    readonly Lock _accessGate = new();
    int _activeAccessCount;
    bool _disposeStarted;

    /// <summary>
    ///     Created by a <see cref="Dispose" /> that finds writer accesses outstanding, and set when
    ///     the last of them is handed back. Null while nothing is waiting, so a block disposed with
    ///     no writer inside it allocates nothing.
    /// </summary>
    ManualResetEventSlim? _accessDrained;

    // Volatile so the disposal flag and the base pointer are read and written in program order
    // across threads: without it the guards below may observe _disposed as false while already
    // seeing a null _base, or a stale cached pointer. This orders the two against each other; it
    // does not make a raw property read safe to overlap a dispose. Readers go through snapshot
    // borrows (see FileIndex.DisposeAsync); writers are serialized through BlockAccessScope.
    volatile byte* _base;
    volatile bool _disposed;

    /// <summary>
    ///     A test seam, held per instance rather than statically so two blocks never share it.
    ///     Invoked once disposal has begun and before the view is unmapped, which is the window a
    ///     racing writer has to observe.
    /// </summary>
    internal Action? _disposeStartedForTest;

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

    /// <summary>
    ///     True when this block was created in a no-cache path and therefore owns deletion via
    ///     <see cref="FileOptions.DeleteOnClose" />, so the process is not required to delete it.
    /// </summary>
    public bool DeleteOnClose { get; }

    /// <summary>
    ///     Takes one writer access, or reports that disposal has begun and the view is on its way
    ///     out. Taken under the access gate rather than through an interlocked increment so an
    ///     access can never be admitted after the disposal flag is read but before the count moves.
    /// </summary>
    internal bool TryTakeAccess()
    {
        lock (_accessGate)
        {
            if (_disposeStarted)
            {
                return false;
            }

            _activeAccessCount++;
            return true;
        }
    }

    /// <summary>Hands one writer access back, waking a dispose waiting for the last of them.</summary>
    internal void ReturnAccess()
    {
        lock (_accessGate)
        {
            _activeAccessCount--;
            if (_activeAccessCount == 0)
            {
                _accessDrained?.Set();
            }
        }
    }

    /// <summary>
    ///     Takes a writer's claim on the view for one <see cref="BlockWriter" /> operation.
    ///     Throws once disposal has begun: the view is being torn down, so a new writer is turned
    ///     away with a catchable exception rather than joined to a region that is about to be
    ///     unmapped.
    /// </summary>
    internal BlockAccessScope TakeAccess()
    {
        return new BlockAccessScope(this);
    }

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

    public Span<ushort> SequenceNumbers
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new Span<ushort>(_base + (long)Header.SequenceRegionOffset, (int)Header.SlotCapacity);
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
        var (mappedFile, view) = OpenMapping(options.Path, FileMode.Create, length, length,
            options.DeleteOnClose ? FileOptions.DeleteOnClose : FileOptions.None);
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

    /// <summary>
    ///     Closes the view after every writer already inside the block has left it. Admission of
    ///     new writer scopes closes first; then this waits, outside the access gate because a
    ///     writer hands its access back through that gate, for the outstanding ones, bounded by
    ///     one writer operation (a row write, or <see cref="BlockWriter.Complete" />'s flush);
    ///     only then is the pointer released and the view unmapped. A call after disposal has
    ///     begun returns without waiting, matching the long-standing contract that disposal is
    ///     idempotent and owned by a single disposer.
    /// </summary>
    public void Dispose()
    {
        ManualResetEventSlim? drained;
        lock (_accessGate)
        {
            if (_disposeStarted)
            {
                return;
            }

            _disposeStarted = true;
            if (_activeAccessCount > 0)
            {
                _accessDrained ??= new ManualResetEventSlim();
            }

            drained = _accessDrained;
        }

        _disposeStartedForTest?.Invoke();

        // Invariant: blocks synchronously on the ManualResetEventSlim until in-flight writers drain, not on a Task.
        drained?.Wait();

        _disposed = true;
        if (_base is not null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _base = null;
        }

        _view.Dispose();
        _mappedFile.Dispose();
        drained?.Dispose();

        // File deletion follows the operating system contract. When created with
        // FileOptions.DeleteOnClose, the last handle closure removes the backing file.
    }

    static BlockFile? OpenMapped(string path, uint expectedVolumeSerial, long length,
        out BlockValidationResult validation)
    {
        BlockFile? block = null;
        try
        {
            var (mappedFile, view) = OpenMapping(path, FileMode.Open, mappingCapacity: 0, viewLength: length,
                fileOptions: FileOptions.None);
            block = new BlockFile(path, length, deleteOnClose: false, mappedFile, view);
            validation = BlockHeader.Validate(in block.Header, expectedVolumeSerial, length);
            if (validation == BlockValidationResult.Valid)
            {
                validation = block.ValidateNameDescriptors();
            }

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

    BlockValidationResult ValidateNameDescriptors()
    {
        var usedBytes = (ulong)Header.NamePoolUsed;
        var rows = Rows;
        for (var rowIndex = 0u; rowIndex < Header.RowCount; rowIndex++)
        {
            var descriptor = FileRow.ReadDescriptorWord(in rows[(int)rowIndex]);
            var offsetBytes = FileRow.DescriptorNameOffsetBytes(descriptor);
            var lengthBytes = (ulong)FileRow.DescriptorNameLengthUnits(descriptor) * sizeof(char);
            if ((offsetBytes & (sizeof(char) - 1)) != 0 || offsetBytes + lengthBytes > usedBytes)
            {
                return BlockValidationResult.InvalidNameDescriptor;
            }
        }

        return BlockValidationResult.Valid;
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
        header.SequenceRegionOffset = (ulong)BlockLayout.SequenceRegionOffset(options.SlotCapacity);
        header.NamePoolOffset = (ulong)BlockLayout.NamePoolOffset(options.SlotCapacity);
    }
}
