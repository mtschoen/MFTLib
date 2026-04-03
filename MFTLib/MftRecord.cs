using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MFTLib.Tests")]

namespace MFTLib;

public readonly struct MftRecord
{
    readonly ulong _recordNumber;
    readonly ulong _parentRecordNumber;
    readonly ushort _flags;
    readonly ushort _nameLength;
    readonly ushort _pathLength;
    readonly char _driveLetter;

    // These are either pointers to native memory (temporary) or materialized strings
    readonly IntPtr _namePtr;
    readonly IntPtr _pathPtr;
    readonly string? _fileName;
    readonly string? _fullPath;

    public ulong RecordNumber => _recordNumber;
    public ulong ParentRecordNumber => _parentRecordNumber;
    public bool InUse => (_flags & 1) != 0;
    public bool IsDirectory => (_flags & 2) != 0;

    public unsafe string FileName
    {
        get
        {
            if (_fileName != null) return _fileName;
            if (_namePtr != IntPtr.Zero && _nameLength > 0)
                return new string((char*)_namePtr, 0, _nameLength);

            if (_pathPtr != IntPtr.Zero && _pathLength > 0)
            {
                var pathChars = (char*)_pathPtr;
                var lastSep = -1;
                for (var i = _pathLength - 1; i >= 0; i--)
                {
                    if (pathChars[i] == '\\') { lastSep = i; break; }
                }
                var start = lastSep + 1;
                return new string(pathChars, start, _pathLength - start);
            }

            return string.Empty;
        }
    }

    public unsafe string? FullPath
    {
        get
        {
            if (_fullPath != null) return _fullPath;
            if (_pathPtr == IntPtr.Zero || _pathLength == 0) return null;
            var relativePath = new string((char*)_pathPtr, 0, _pathLength);
            return _driveLetter == '\0' ? relativePath : $"{_driveLetter}:\\{relativePath}";
        }
    }

    // ReSharper disable once PreferConcreteValueOverDefault
    internal MftRecord(ulong recordNumber, ulong parentRecordNumber, ushort flags, IntPtr namePtr, ushort nameLength, IntPtr pathPtr = default, ushort pathLength = 0, char driveLetter = '\0')
    {
        _recordNumber = recordNumber;
        _parentRecordNumber = parentRecordNumber;
        _flags = flags;
        _namePtr = namePtr;
        _nameLength = nameLength;
        _pathPtr = pathPtr;
        _pathLength = pathLength;
        _driveLetter = driveLetter;
        _fileName = null;
        _fullPath = null;
    }

    /// <summary>
    /// Creates a new MftRecord where the strings are materialized into managed memory.
    /// This makes the record safe to use after the underlying native buffer is freed.
    /// </summary>
    public MftRecord Materialize()
    {
        if (_fileName != null || (_namePtr == IntPtr.Zero && _pathPtr == IntPtr.Zero))
            return this;

        return new MftRecord(_recordNumber, _parentRecordNumber, _flags, FileName, FullPath);
    }

    internal MftRecord(ulong recordNumber, ulong parentRecordNumber, ushort flags, string? fileName, string? fullPath)
    {
        _recordNumber = recordNumber;
        _parentRecordNumber = parentRecordNumber;
        _flags = flags;
        _fileName = fileName;
        _fullPath = fullPath;
        _namePtr = IntPtr.Zero;
        _nameLength = 0;
        _pathPtr = IntPtr.Zero;
        _pathLength = 0;
        _driveLetter = '\0';
    }

    public override string ToString() => FullPath ?? FileName;
}
