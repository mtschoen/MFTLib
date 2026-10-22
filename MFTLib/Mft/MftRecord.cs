namespace MFTLib;

internal readonly struct MftRecordFields(
    ushort flags, FileAttributes fileAttributes = 0, long size = 0, long modifiedFileTime = 0,
    ushort sequenceNumber = 0, ushort parentSequenceNumber = 0)
{
    public readonly ushort Flags = flags;
    public readonly FileAttributes FileAttributes = fileAttributes;
    public readonly long Size = size;
    public readonly long ModifiedFileTime = modifiedFileTime;
    public readonly ushort SequenceNumber = sequenceNumber;
    public readonly ushort ParentSequenceNumber = parentSequenceNumber;
}

/// <summary>
///     One parsed MFT file record. A record read straight from an <see cref="MftResult" /> borrows
///     its name from native memory and is valid only until that result is disposed; call
///     <see cref="Materialize" /> to keep one longer. Records from the batch and array APIs are
///     already materialized.
/// </summary>
internal readonly struct MftRecord
{
    readonly ushort _flags;
    readonly ushort _nameLength;
    readonly bool _materialized;
    readonly long _modifiedFileTime;

    const ushort InUseFlag = 1;
    const ushort DirectoryFlag = 2;
    const ushort SizeUnknownFlag = 0x8000;

    // DateTime.MaxValue as a FILETIME. Anything past it makes FromFileTimeUtc throw.
    static readonly long MaximumFileTime = DateTime.MaxValue.ToFileTimeUtc();

    // Either a pointer to native memory (temporary) or a materialized string
    readonly IntPtr _namePtr;
    readonly string? _fileName;

    /// <summary>
    ///     MFT segment index (the lower 48 bits of the file reference number). Stable across USN
    ///     journal reads. The sequence number is carried separately in <see cref="SequenceNumber" />.
    /// </summary>
    public ulong RecordNumber { get; }

    /// <summary>
    ///     Parent directory's MFT segment index (the lower 48 bits of the file reference number).
    ///     The NTFS root directory is segment 5 (its parent is also 5).
    /// </summary>
    public ulong ParentRecordNumber { get; }

    /// <summary>
    ///     The NTFS sequence number of the file reference. NTFS reuses a segment index after a
    ///     file is deleted, so pair it with <see cref="RecordNumber" /> to identify one file.
    /// </summary>
    public ushort SequenceNumber { get; }

    /// <summary>
    ///     The sequence number the record's name stores in its parent reference. A parent is the
    ///     directory this name was created in only while its own sequence number still equals this one.
    /// </summary>
    internal ushort ParentSequenceNumber { get; }

    /// <summary>
    ///     Whether the record is allocated. Freed base records, returned only when a volume scan asks
    ///     for them, have this value set to false.
    /// </summary>
    public bool InUse => (_flags & InUseFlag) != 0;
    /// <summary>Whether the record header marks a directory.</summary>
    public bool IsDirectory => (_flags & DirectoryFlag) != 0;

    /// <summary>The Win32 file attributes the record carries; the directory bit also appears in <see cref="IsDirectory" />.</summary>
    public FileAttributes FileAttributes { get; }

    /// <summary>
    ///     Size in bytes of the unnamed data stream. Zero for a directory, and zero when
    ///     <see cref="SizeKnown" /> is false because no usable data size was found in the base
    ///     record, including data in an extension record or a negative non-resident size.
    /// </summary>
    public long Size { get; }

    /// <summary>
    ///     False when <see cref="Size" /> is a placeholder zero because the base record carried
    ///     no usable data size. A true <see cref="Size" /> of zero reads true here.
    /// </summary>
    public bool SizeKnown => (_flags & SizeUnknownFlag) == 0;

    /// <summary>
    ///     Last modification time from <c>$STANDARD_INFORMATION</c> when present, otherwise
    ///     from the selected <c>$FILE_NAME</c> attribute's <c>ModificationTime</c>.
    ///     Zero and negative raw FILETIME values are treated as no timestamp and read as
    ///     <see cref="DateTime.MinValue" />. Values beyond <see cref="DateTime.MaxValue" />
    ///     also read as MinValue to avoid overflow.
    /// </summary>
    public DateTime ModifiedUtc
    {
        get
        {
            if (_modifiedFileTime <= 0 || _modifiedFileTime > MaximumFileTime)
            {
                return DateTime.MinValue;
            }

            return DateTime.FromFileTimeUtc(_modifiedFileTime);
        }
    }

    /// <summary>
    ///     The record's name. The root directory (segment 5) reads as <c>.</c> when it carried no
    ///     name of its own, and any other record without a name reads as an empty string.
    /// </summary>
    public unsafe string FileName
    {
        get
        {
            if (_materialized)
            {
                return _fileName ?? string.Empty;
            }

            if (_namePtr != IntPtr.Zero && _nameLength > 0)
            {
                return new string((char*)_namePtr, 0, _nameLength);
            }

            return RecordNumber == 5 ? "." : string.Empty;
        }
    }

    internal MftRecord(ulong recordNumber, ulong parentRecordNumber, MftRecordFields fields, IntPtr namePtr,
        ushort nameLength)
    {
        RecordNumber = recordNumber;
        ParentRecordNumber = parentRecordNumber;
        _flags = fields.Flags;
        FileAttributes = fields.FileAttributes;
        Size = fields.Size;
        _modifiedFileTime = fields.ModifiedFileTime;
        SequenceNumber = fields.SequenceNumber;
        ParentSequenceNumber = fields.ParentSequenceNumber;
        _namePtr = namePtr;
        _nameLength = nameLength;
        _fileName = null;
        _materialized = false;
    }

    /// <summary>
    ///     Creates a new MftRecord where the strings are materialized into managed memory.
    ///     This makes the record safe to use after the underlying native buffer is freed.
    /// </summary>
    public MftRecord Materialize()
    {
        if (_materialized)
        {
            return this;
        }

        var fields = new MftRecordFields(_flags, FileAttributes, Size, _modifiedFileTime, SequenceNumber,
            ParentSequenceNumber);
        return new MftRecord(RecordNumber, ParentRecordNumber, fields, FileName);
    }

    internal MftRecord(ulong recordNumber, ulong parentRecordNumber, MftRecordFields fields, string? fileName)
    {
        RecordNumber = recordNumber;
        ParentRecordNumber = parentRecordNumber;
        _flags = fields.Flags;
        FileAttributes = fields.FileAttributes;
        Size = fields.Size;
        _modifiedFileTime = fields.ModifiedFileTime;
        SequenceNumber = fields.SequenceNumber;
        ParentSequenceNumber = fields.ParentSequenceNumber;
        _fileName = fileName;
        _namePtr = IntPtr.Zero;
        _nameLength = 0;
        _materialized = true;
    }
}
