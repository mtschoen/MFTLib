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

public readonly struct MftRecord
{
    readonly ushort _flags;
    readonly ushort _nameLength;
    readonly ushort _pathLength;
    readonly char _driveLetter;
    readonly bool _materialized;

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

    internal MftRecord(ulong recordNumber, ulong parentRecordNumber, ushort flags, FileAttributes fileAttributes,
        NativeStrings strings, char driveLetter = '\0')
    {
        RecordNumber = recordNumber;
        ParentRecordNumber = parentRecordNumber;
        _flags = flags;
        FileAttributes = fileAttributes;
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

        return new MftRecord(RecordNumber, ParentRecordNumber, _flags, FileName, FullPath, FileAttributes);
    }

    internal MftRecord(ulong recordNumber, ulong parentRecordNumber, ushort flags, string? fileName, string? fullPath,
        FileAttributes fileAttributes = 0)
    {
        RecordNumber = recordNumber;
        ParentRecordNumber = parentRecordNumber;
        _flags = flags;
        FileAttributes = fileAttributes;
        _fileName = fileName;
        _fullPath = fullPath;
        _namePtr = IntPtr.Zero;
        _nameLength = 0;
        _pathPtr = IntPtr.Zero;
        _pathLength = 0;
        _driveLetter = '\0';
        _materialized = true;
    }

    public override string ToString()
    {
        return FullPath ?? FileName;
    }
}
