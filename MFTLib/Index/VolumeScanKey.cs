using System.Runtime.CompilerServices;

namespace MFTLib.Index;

readonly record struct VolumeScanKey(MftIndexSource? Source, VolumeKey Volume, uint VolumeSerial,
    string? CacheDirectory, CacheTag CacheTag)
{
    static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public bool Equals(VolumeScanKey other) => ReferenceEquals(Source, other.Source) && Volume == other.Volume &&
        VolumeSerial == other.VolumeSerial && PathComparer.Equals(CacheDirectory, other.CacheDirectory) && CacheTag == other.CacheTag;

    public override int GetHashCode() => HashCode.Combine(Source is null ? 0 : RuntimeHelpers.GetHashCode(Source),
        Volume, VolumeSerial, CacheDirectory is null ? 0 : PathComparer.GetHashCode(CacheDirectory), CacheTag);

    internal static VolumeScanKey? For(ResolvedRoot resolved, FileIndexOptions options, string cacheDirectoryPath) =>
        resolved is { Producer: ProducerKind.Mft, IsDump: false, MftUnavailable: null, Volume: { } volume }
            ? new VolumeScanKey(options.MftSource, volume, resolved.VolumeSerial,
                options.CacheMode == CacheMode.Disabled ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheDirectoryPath)),
                options.CacheTag)
            : null;
}
