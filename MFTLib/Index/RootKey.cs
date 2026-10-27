using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

/// <summary>
///     Identifies an indexed root as either an NTFS volume or a normalized directory path.
///     The default value is invalid.
/// </summary>
[SuppressMessage("Design", "CA1036",
    Justification = "The owner-specified public surface exposes ordering through IComparable<RootKey>.CompareTo only.")]
public readonly record struct RootKey : IComparable<RootKey>
{
    enum Kind : byte { Volume = 1, Directory = 2 }

    internal const int MaximumTextUnits = 1983;

    readonly Kind _kind;
    readonly VolumeKey _volume;
    readonly string? _directory;

    RootKey(VolumeKey volume)
    {
        _kind = Kind.Volume;
        _volume = volume;
        _directory = null;
    }

    RootKey(string directory)
    {
        _kind = Kind.Directory;
        _volume = default;
        _directory = directory;
    }

    /// <summary>Whether this key identifies a volume rather than a directory.</summary>
    public bool IsVolume => _kind == Kind.Volume;

    /// <summary>Gets the volume when this is a volume key; otherwise returns false and the default volume.</summary>
    public bool TryGetVolume(out VolumeKey volume)
    {
        volume = _kind == Kind.Volume ? _volume : default;
        return _kind == Kind.Volume;
    }

    /// <summary>Creates a key from a fully qualified directory path, detecting a Windows volume root.</summary>
    /// <exception cref="ArgumentException">
    ///     The path is blank, relative, not well-formed UTF-16, or longer than 1,983 UTF-16 code units after normalization.
    /// </exception>
    public static RootKey FromDirectory(string path) => FromDirectory(path, out _);

    internal static RootKey FromDirectory(string path, out string normalizedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The root directory path must be fully qualified.", nameof(path));
        }

        normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!WellFormedText.IsWellFormed(normalizedPath))
        {
            throw new ArgumentException("The root directory path must be well-formed UTF-16.", nameof(path));
        }
        if (normalizedPath.Length > MaximumTextUnits)
        {
            throw new ArgumentException(
                $"The normalized root directory path cannot exceed {MaximumTextUnits} UTF-16 code units.", nameof(path));
        }

        return OperatingSystem.IsWindows() && VolumeKey.TryParse(normalizedPath, out var volume)
            ? new RootKey(volume)
            : new RootKey(normalizedPath);
    }

    /// <summary>Parses a drive spec as a volume or any other valid full path as a normalized directory.</summary>
    public static bool TryParse(string? text, out RootKey result)
    {
        if (VolumeKey.TryParse(text, out var volume))
        {
            result = new RootKey(volume);
            return true;
        }

        if (text is null)
        {
            result = default;
            return false;
        }

        try
        {
            result = FromDirectory(text);
            return true;
        }
        catch (ArgumentException)
        {
            result = default;
            return false;
        }
    }

    /// <summary>Widens a volume key to an index root key without validation or conversion of its letter.</summary>
    public static implicit operator RootKey(VolumeKey volume) => new(volume);

    /// <summary>Returns the volume letter or normalized directory path. Rejects the default key.</summary>
    public override string ToString() => _kind switch
    {
        Kind.Volume => _volume.ToString(),
        Kind.Directory => _directory ?? throw new ArgumentException("The root key is invalid."),
        _ => throw new ArgumentException("The default root key is invalid.")
    };

    /// <summary>Orders volumes by letter before directories by ordinal path text.</summary>
    public int CompareTo(RootKey other)
    {
        Validated();
        other.Validated();
        if (_kind != other._kind)
        {
            return _kind == Kind.Volume ? -1 : 1;
        }

        return _kind == Kind.Volume
            ? _volume.Letter.CompareTo(other._volume.Letter)
            : string.CompareOrdinal(_directory, other._directory);
    }

    /// <summary>Compares volume letters or directory paths under the platform's directory case policy.</summary>
    public bool Equals(RootKey other)
    {
        if (_kind != other._kind)
        {
            return false;
        }

        return _kind switch
        {
            Kind.Volume => _volume.Equals(other._volume),
            Kind.Directory => string.Equals(_directory, other._directory, DriveKind.DirectoryWalk.Comparison),
            _ => true
        };
    }

    /// <summary>Returns a hash consistent with the volume or platform directory comparison.</summary>
    public override int GetHashCode() => _kind switch
    {
        Kind.Volume => _volume.GetHashCode(),
        Kind.Directory when _directory is not null => (DriveKind.DirectoryWalk.IsCaseSensitive
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase).GetHashCode(_directory),
        _ => 0
    };

    internal RootKey Validated()
    {
        switch (_kind)
        {
            case Kind.Volume:
                _volume.Validated();
                break;
            case Kind.Directory:
                if (_directory is null || _directory.Length > MaximumTextUnits)
                {
                    throw new ArgumentException("The root key is invalid.");
                }
                break;
            default:
                throw new ArgumentException("The default root key is invalid.");
        }

        return this;
    }
}
