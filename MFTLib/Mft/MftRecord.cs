using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MFTLib.Tests")]

namespace MFTLib;

readonly struct NativeStrings(IntPtr namePtr, ushort nameLength, IntPtr pathPtr, ushort pathLength)
{
    public readonly IntPtr NamePtr = namePtr;
    public readonly ushort NameLength = nameLength;
    public readonly IntPtr PathPtr = pathPtr;
    public readonly ushort PathLength = pathLength;
}

internal readonly struct MftRecordFields(
    ushort flags, FileAttributes fileAttributes = 0, long size = 0, long modifiedFileTime = 0)
{
    public readonly ushort Flags = flags;
    public readonly FileAttributes FileAttributes = fileAttributes;
    public readonly long Size = size;
    public readonly long ModifiedFileTime = modifiedFileTime;
}

/// <summary>
///     Every column a test needs to mint a materialized record. Grouped into one value so
///     the factory stays inside the parameter limit as the row gains columns.
/// </summary>
internal sealed record MftRecordTestValues
{
    public required ulong RecordNumber { get; init; }
    public required ulong ParentRecordNumber { get; init; }
    public required ushort Flags { get; init; }
    public required string FileName { get; init; }
    public string? FullPath { get; init; }
    public FileAttributes FileAttributes { get; init; }
    public long Size { get; init; }
    public long ModifiedFileTime { get; init; }
}

public readonly struct MftRecord
{
    readonly ushort _flags;
    readonly ushort _nameLength;
    readonly ushort _pathLength;
    readonly char _driveLetter;
    readonly bool _materialized;
    readonly long _size;
    readonly long _modifiedFileTime;

    const ushort SizeUnknownFlag = 0x8000;

    // DateTime.MaxValue as a FILETIME. Anything past it makes FromFileTimeUtc throw.
    static readonly long MaximumFileTime = DateTime.MaxValue.ToFileTimeUtc();

    // These are either pointers to native memory (temporary) or materialized strings
    readonly IntPtr _namePtr;
    readonly IntPtr _pathPtr;
    readonly string? _fileName;
    readonly string? _fullPath;

    /// <summary>
    ///     MFT segment index (48-bit, sequence number stripped). Stable across USN journal reads.
    /// </summary>
    public ulong RecordNumber { get; }

    /// <summary>
    ///     Parent directory's MFT segment index (48-bit, sequence number stripped).
    ///     The NTFS root directory is segment 5 (its parent is also 5).
    /// </summary>
    public ulong ParentRecordNumber { get; }

    public bool InUse => (_flags & 1) != 0;
    public bool IsDirectory => (_flags & 2) != 0;
    public FileAttributes FileAttributes { get; }

    /// <summary>
    ///     Size in bytes of the unnamed data stream. Zero for a directory, and zero when
    ///     <see cref="SizeKnown" /> is false, which means the record's data attribute lives
    ///     in an extension record this parser does not follow.
    /// </summary>
    public long Size => _size;

    public bool SizeKnown => (_flags & SizeUnknownFlag) == 0;

    /// <summary>
    ///     Last modification time from <c>$STANDARD_INFORMATION</c>. A value the runtime
    ///     cannot represent reads as <see cref="DateTime.MinValue" /> rather than throwing,
    ///     so one corrupt record never fails a whole scan.
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

    public unsafe string FileName
    {
        get
        {
            if (_materialized)
            {
                return _fileName ?? string.Empty;
            }

            if (_fileName != null)
            {
                return _fileName;
            }

            if (_namePtr != IntPtr.Zero && _nameLength > 0)
            {
                return new string((char*)_namePtr, 0, _nameLength);
            }

            if (_pathPtr != IntPtr.Zero && _pathLength > 0)
            {
                var pathChars = (char*)_pathPtr;
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

    public unsafe string? FullPath
    {
        get
        {
            if (_materialized)
            {
                return _fullPath;
            }

            if (_fullPath != null)
            {
                return _fullPath;
            }

            if (_pathPtr == IntPtr.Zero)
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

        var fields = new MftRecordFields(_flags, FileAttributes, _size, _modifiedFileTime);
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
        var fields = new MftRecordFields(values.Flags, values.FileAttributes, values.Size, values.ModifiedFileTime);
        return new MftRecord(values.RecordNumber, values.ParentRecordNumber, fields, values.FileName, values.FullPath);
    }

    public override string ToString()
    {
        return FullPath ?? FileName;
    }
}
