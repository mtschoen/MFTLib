namespace MFTLib;

readonly struct NativeStrings(IntPtr namePtr, ushort nameLength, IntPtr pathPtr, ushort pathLength)
{
    public readonly IntPtr NamePtr = namePtr;
    public readonly ushort NameLength = nameLength;
    public readonly IntPtr PathPtr = pathPtr;
    public readonly ushort PathLength = pathLength;
}

internal readonly struct MftRecordFields(
    ushort flags, FileAttributes fileAttributes = 0, long size = 0, long modifiedFileTime = 0,
    ushort sequenceNumber = 0)
{
    public readonly ushort Flags = flags;
    public readonly FileAttributes FileAttributes = fileAttributes;
    public readonly long Size = size;
    public readonly long ModifiedFileTime = modifiedFileTime;
    public readonly ushort SequenceNumber = sequenceNumber;
}

/// <summary>
///     Every column a test needs to mint a materialized record. Grouped into one value so
///     the factory stays inside the parameter limit as the row gains columns.
/// </summary>
internal sealed record MftRecordTestValues
{
    public required ulong RecordNumber { get; init; }
    public required ulong ParentRecordNumber { get; init; }
    public bool InUse { get; init; } = true;
    public bool IsDirectory { get; init; }
    public bool SizeKnown { get; init; } = true;
    public required string FileName { get; init; }
    public string? FullPath { get; init; }
    public FileAttributes FileAttributes { get; init; }
    public long Size { get; init; }
    public long ModifiedFileTime { get; init; }
    public ushort SequenceNumber { get; init; }
}

/// <summary>
///     One parsed MFT file record. A record read straight from an <see cref="MftResult" /> borrows
///     its name and path strings from native memory and is valid only until that result is
///     disposed; call <see cref="Materialize" /> to keep one longer. Records from the batch and
///     array APIs are already materialized.
/// </summary>
internal readonly struct MftRecord
{
    readonly ushort _flags;
    readonly ushort _nameLength;
    readonly ushort _pathLength;
    readonly char _driveLetter;
    readonly bool _materialized;
    readonly long _size;
    readonly long _modifiedFileTime;
    readonly ushort _sequenceNumber;

    const ushort InUseFlag = 1;
    const ushort DirectoryFlag = 2;
    const ushort SizeUnknownFlag = 0x8000;
    const ushort PathUnresolvedFlag = 0x4000;

    // DateTime.MaxValue as a FILETIME. Anything past it makes FromFileTimeUtc throw.
    static readonly long MaximumFileTime = DateTime.MaxValue.ToFileTimeUtc();

    // These are either pointers to native memory (temporary) or materialized strings
    readonly IntPtr _namePtr;
    readonly IntPtr _pathPtr;
    readonly string? _fileName;
    readonly string? _fullPath;

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
    public ushort SequenceNumber => _sequenceNumber;

    /// <summary>
    ///     Whether the record is allocated. Freed base records returned with
    ///     <see cref="MatchFlags.IncludeFreed" /> have this value set to false.
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
    public long Size => _size;

    /// <summary>
    ///     False when <see cref="Size" /> is a placeholder zero because the base record carried
    ///     no usable data size. A true <see cref="Size" /> of zero reads true here.
    /// </summary>
    public bool SizeKnown => (_flags & SizeUnknownFlag) == 0;

    /// <summary>
    ///     Last modification time from <c>$STANDARD_INFORMATION</c>. Zero and negative raw
    ///     FILETIME values are treated as no timestamp and read as <see cref="DateTime.MinValue" />.
    ///     Values beyond <see cref="DateTime.MaxValue" /> also read as MinValue to avoid overflow.
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
    ///     The file name without its directory. When the record carried no name of its own it is
    ///     taken from the last segment of the path; the root directory (segment 5) reads as
    ///     <c>.</c>, and a record with neither a name nor a path reads as an empty string.
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

            if (_pathPtr != IntPtr.Zero && _pathLength > 0)
            {
                var pathChars = (char*)_pathPtr;
                if ((_flags & PathUnresolvedFlag) != 0)
                {
                    return new string(pathChars, 0, _pathLength);
                }

                var lastSep = -1;
                for (var i = _pathLength - 1; i >= 0; i--)
                {
                    if (pathChars[i] == '\\')
                    {
                        lastSep = i;
                        break;
                    }
                }

                var start = lastSep + 1;
                return new string(pathChars, start, _pathLength - start);
            }

            if (RecordNumber == 5)
            {
                return ".";
            }

            return string.Empty;
        }
    }

    /// <summary>
    ///     The resolved path, or null when paths were not requested or could not be resolved.
    ///     A freed record requires a trusted sequence match at every parent, including the root;
    ///     a freed parent's sequence may also be one higher than its reference, with ushort wraparound.
    /// </summary>
    public unsafe string? FullPath
    {
        get
        {
            if (_materialized)
            {
                return _fullPath;
            }

            if (_pathPtr == IntPtr.Zero || (_flags & PathUnresolvedFlag) != 0)
            {
                return null;
            }

            if (_pathLength == 0)
            {
                if (RecordNumber == 5 && (_flags & 1) != 0)
                {
                    return _driveLetter == '\0' ? @"\" : $"{_driveLetter}:\\";
                }

                return null;
            }

            var relativePath = new string((char*)_pathPtr, 0, _pathLength);
            return _driveLetter == '\0' ? relativePath : $"{_driveLetter}:\\{relativePath}";
        }
    }

    internal MftRecord(ulong recordNumber, ulong parentRecordNumber, MftRecordFields fields,
        NativeStrings strings, char driveLetter = '\0')
    {
        RecordNumber = recordNumber;
        ParentRecordNumber = parentRecordNumber;
        _flags = fields.Flags;
        FileAttributes = fields.FileAttributes;
        _size = fields.Size;
        _modifiedFileTime = fields.ModifiedFileTime;
        _sequenceNumber = fields.SequenceNumber;
        _namePtr = strings.NamePtr;
        _nameLength = strings.NameLength;
        _pathPtr = strings.PathPtr;
        _pathLength = strings.PathLength;
        _driveLetter = driveLetter;
        _fileName = null;
        _fullPath = null;
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

        var fields = new MftRecordFields(_flags, FileAttributes, _size, _modifiedFileTime, _sequenceNumber);
        return new MftRecord(RecordNumber, ParentRecordNumber, fields, FileName, FullPath);
    }

    internal MftRecord(ulong recordNumber, ulong parentRecordNumber, MftRecordFields fields, string? fileName,
        string? fullPath)
    {
        RecordNumber = recordNumber;
        ParentRecordNumber = parentRecordNumber;
        _flags = fields.Flags;
        FileAttributes = fields.FileAttributes;
        _size = fields.Size;
        _modifiedFileTime = fields.ModifiedFileTime;
        _sequenceNumber = fields.SequenceNumber;
        _fileName = fileName;
        _fullPath = fullPath;
        _namePtr = IntPtr.Zero;
        _nameLength = 0;
        _pathPtr = IntPtr.Zero;
        _pathLength = 0;
        _driveLetter = '\0';
        _materialized = true;
    }

    internal static MftRecord CreateForTest(MftRecordTestValues values)
    {
        var flags = (ushort)((values.InUse ? InUseFlag : 0)
                             | (values.IsDirectory ? DirectoryFlag : 0)
                             | (values.SizeKnown ? 0 : SizeUnknownFlag));
        var fields = new MftRecordFields(flags, values.FileAttributes, values.Size, values.ModifiedFileTime,
            values.SequenceNumber);
        return new MftRecord(values.RecordNumber, values.ParentRecordNumber, fields, values.FileName, values.FullPath);
    }

    /// <summary>Formats the record as its path when one was resolved, otherwise as its file name.</summary>
    /// <returns>The <see cref="FullPath" /> or, when that is null, the <see cref="FileName" />.</returns>
    public override string ToString()
    {
        return FullPath ?? FileName;
    }
}
