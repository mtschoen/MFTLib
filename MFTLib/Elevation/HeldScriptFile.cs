using System.Runtime.Versioning;
using System.Text;
using MFTLib.NativeInterop;
using Microsoft.Win32.SafeHandles;

namespace MFTLib;

/// <summary>
///     A temporary PowerShell script held for a child process to run. The script is written, reopened read-only with
///     write and delete sharing refused, checked to be the written file holding the written bytes, and named to the
///     child by <see cref="LaunchPath" />: on Windows the file's final path, which no junction, symbolic link or
///     rename can redirect while the file is held. On Windows the file is only ever deleted through its handle, never
///     its pathname.
/// </summary>
sealed class HeldScriptFile : IDisposable
{
    readonly FileStream _held;
    readonly SafeFileHandle? _anchor;
    readonly string _createdPath;
    bool _disposed;

    HeldScriptFile(FileStream held, SafeFileHandle? anchor, string createdPath, string launchPath)
    {
        _held = held;
        _anchor = anchor;
        _createdPath = createdPath;
        LaunchPath = launchPath;
    }

    /// <summary>The path the child is given; on Windows it resolves to the held file and nothing else.</summary>
    internal string LaunchPath { get; }

    /// <summary>
    ///     Writes <paramref name="script" /> as UTF-8 with a byte order mark (Windows PowerShell 5.1 reads it as UTF-8
    ///     only then) into a new uniquely named file in <paramref name="directory" /> and holds it. Throws
    ///     <see cref="IOException" /> when the held file is not the one written or cannot be pinned. Test support
    ///     only: <paramref name="written" /> runs after the writer closes and before the locked reopen, and
    ///     <paramref name="takeAnchor" /> replaces the attributes-only reopen of the writer's handle.
    /// </summary>
    internal static HeldScriptFile Create(string directory, string script, Action<string>? written = null,
        Func<SafeFileHandle, SafeFileHandle>? takeAnchor = null)
    {
        var utf8WithByteOrderMark = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var expected = utf8WithByteOrderMark.GetPreamble().Concat(utf8WithByteOrderMark.GetBytes(script)).ToArray();
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".ps1");
        SafeFileHandle? anchor = null;
        FileStream? held = null;
        var created = false;
        try
        {
            using (var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                // An attributes-only handle on the written file itself: what the held file is checked against and
                // what the delete goes through, so neither ever resolves the pathname again.
                anchor = OperatingSystem.IsWindows() ? TakeAnchor(writer.SafeFileHandle, takeAnchor) : null;
                writer.Write(expected);
                writer.Flush(flushToDisk: true);
            }

            written?.Invoke(path);
            held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            RequireBytes(held, expected);
            // The anchor exists exactly when on Windows, so the pathname is the launch path only elsewhere.
            var launchPath = OperatingSystem.IsWindows() && anchor is not null ? PinnedLaunchPath(held, anchor) : path;
            return new HeldScriptFile(held, anchor, path, launchPath);
        }
        catch
        {
            held?.Dispose();
            if (created)
            {
                Delete(anchor, path);
            }

            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    static SafeFileHandle TakeAnchor(SafeFileHandle writer, Func<SafeFileHandle, SafeFileHandle>? takeAnchor) =>
        takeAnchor is null ? HandleBoundFile.ReopenForAttributes(writer) : takeAnchor(writer);

    static void RequireBytes(FileStream held, byte[] expected)
    {
        var actual = new byte[expected.Length + 1];
        var length = held.ReadAtLeast(actual, actual.Length, throwOnEndOfStream: false);
        if (!actual.AsSpan(0, length).SequenceEqual(expected))
        {
            throw new IOException("The PowerShell script file changed before it could be locked.");
        }
    }

    // The final path names every component by its real name with every junction and symbolic link already resolved,
    // so each component is a real directory on the held file's ancestor chain; NTFS refuses to rename any of them
    // while the file is open, and a non-empty directory cannot become a junction. That holds only for a local NTFS
    // volume reached at its drive root, so a network path, another file system or a folder mount point is refused.
    // The path must also mean the same to PowerShell as to Win32: Win32 normalization must leave it unchanged (a
    // trailing dot or space would name a different directory), and it must carry no PowerShell wildcard or escape
    // character, because powershell.exe -File resolves 'dir[ab]' to a sibling 'dira'. Last, opening it the way
    // Win32 does must land on the written file.
    [SupportedOSPlatform("windows")]
    static string PinnedLaunchPath(FileStream held, SafeFileHandle anchor)
    {
        // The reopen resolved the pathname again, so it must land on the very file the writer created.
        var writtenIdentity = HandleBoundFile.IdentityOf(anchor);
        if (HandleBoundFile.IdentityOf(held.SafeFileHandle) != writtenIdentity)
        {
            throw new IOException("The PowerShell script path was redirected before the script could be locked.");
        }

        var fileSystem = HandleBoundFile.FileSystemNameOf(held.SafeFileHandle);
        if (fileSystem != "NTFS")
        {
            throw new IOException($"The PowerShell script is on {fileSystem}, not NTFS, so its path cannot be pinned.");
        }

        var launchPath = ToLocalDrivePath(HandleBoundFile.FinalPathOf(held.SafeFileHandle));
        RequireSameMeaningToPowerShell(launchPath);
        if (!IsDriveRoot(HandleBoundFile.VolumePathOf(launchPath)))
        {
            throw new IOException($"The PowerShell script path '{launchPath}' passes through a folder mount point.");
        }

        using var check = HandleBoundFile.OpenAttributesOnly(launchPath);
        if (HandleBoundFile.IdentityOf(check) != writtenIdentity)
        {
            throw new IOException($"The PowerShell script path '{launchPath}' does not open the held script.");
        }

        return launchPath;
    }

    /// <summary>
    ///     Throws <see cref="IOException" /> when Win32 normalization would change <paramref name="launchPath" /> or
    ///     when it holds a character PowerShell's path resolution treats as a wildcard or escape.
    /// </summary>
    internal static void RequireSameMeaningToPowerShell(string launchPath)
    {
        if (launchPath.IndexOfAny(['[', ']', '`', '*', '?']) >= 0)
        {
            throw new IOException(
                $"The PowerShell script path '{launchPath}' holds a character PowerShell reads as a wildcard.");
        }

        var components = launchPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (Path.GetFullPath(launchPath) != launchPath
            || components.Skip(1).Any(component => component.EndsWith('.') || component.EndsWith(' ')))
        {
            throw new IOException($"The PowerShell script path '{launchPath}' does not survive Win32 normalization.");
        }
    }

    // Windows PowerShell 5.1 refuses a -File path in the \\?\ form, so the final path goes back to its drive form.
    // Anything but a local drive path (a \\?\UNC\ network path, a volume GUID path) is refused: a server, not the
    // local kernel, would decide what the child reads.
    internal static string ToLocalDrivePath(string finalPath)
    {
        const string localPrefix = @"\\?\";
        var drivePath = finalPath.StartsWith(localPrefix, StringComparison.Ordinal) ? finalPath[localPrefix.Length..] : "";
        if (drivePath.Length < 3 || !char.IsAsciiLetter(drivePath[0]) || drivePath[1] != ':' || drivePath[2] != '\\')
        {
            throw new IOException($"The PowerShell script path '{finalPath}' is not on a local drive.");
        }

        return drivePath;
    }

    internal static bool IsDriveRoot(string volumePath) =>
        volumePath.Length == 3 && char.IsAsciiLetter(volumePath[0]) && volumePath[1] == ':' && volumePath[2] == '\\';

    /// <summary>
    ///     Releases the hold and deletes the file; a second call does nothing. On Windows the delete goes through a
    ///     reopen of the held file's own handle, so a pathname redirected after the child ran cannot steer it at another
    ///     file. A failed delete is ignored: a leftover temporary script must not change the outcome of the run.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _held.Dispose();
        Delete(_anchor, _createdPath);
    }

    // On Windows the delete goes only through the anchor; without one (it could not be taken) the file is left
    // behind, because a pathname resolved after the writer closed may no longer name it. Elsewhere it goes by path.
    static void Delete(SafeFileHandle? anchor, string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            DeleteQuietly(path);
            return;
        }

        if (anchor is null)
        {
            return;
        }

        HandleBoundFile.TryDeleteByHandle(anchor);
        anchor.Dispose();
    }

    static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary script must not change the outcome of the run.
        }
    }
}
