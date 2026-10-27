using MFTLib.NativeInterop;

namespace MFTLib.Index;

public sealed partial record IndexedRoot
{
    /// <summary>
    ///     Builds an enumeration root from a fully qualified directory path. The key is derived from the normalized
    ///     path, and the serial identifies the containing volume when Windows reports one; zero means no serial.
    /// </summary>
    /// <exception cref="ArgumentException">The path is not a valid <see cref="RootKey" /> directory path.</exception>
    public static IndexedRoot FromDirectory(string rootDirectory)
    {
        var drive = RootKey.FromDirectory(rootDirectory, out var normalizedPath);
        return new IndexedRoot(drive, normalizedPath, ReadDirectoryVolumeSerial(normalizedPath),
            ProducerKind.Enumeration);
    }

    static uint ReadDirectoryVolumeSerial(string rootDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        try
        {
            var volumeRoot = HandleBoundFile.VolumePathOf(rootDirectory);
            var reader = Dependencies._volumeSerialReader ?? ReadVolumeSerial;
            return reader(volumeRoot) ?? 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or System.Security.SecurityException)
        {
            return 0;
        }
    }
}
