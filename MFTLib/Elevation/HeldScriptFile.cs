using System.Text;
using MFTLib.NativeInterop;
using Microsoft.Win32.SafeHandles;

namespace MFTLib;

/// <summary>
///     A temporary PowerShell script held for a child process to run. The script is written, reopened read-only with
///     write and delete sharing refused, checked to be the written file holding the written bytes, and named to the
///     child by <see cref="LaunchPath" />: on Windows the file's final path, which no junction, symbolic link or
///     rename can redirect while the file is held. Disposing deletes the file through its handle, not its pathname.
/// </summary>
sealed class HeldScriptFile : IDisposable
{
    readonly FileStream _held;
    readonly SafeFileHandle? _anchor;
    readonly string _createdPath;

    HeldScriptFile(FileStream held, SafeFileHandle? anchor, string createdPath,
        string launchPath)
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
    ///     only then) into a new uniquely named file in <paramref name="directory" /> and holds it.
    ///     <paramref name="written" /> runs after the writer closes and before the locked reopen; test support only.
    ///     Throws <see cref="IOException" /> when the held file is not the one written or cannot be pinned.
    /// </summary>
    internal static HeldScriptFile Create(string directory, string script, Action<string>? written = null)
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
                anchor = OperatingSystem.IsWindows() ? HandleBoundFile.ReopenForAttributes(writer.SafeFileHandle) : null;
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
    // while the file is open, and a non-empty directory cannot become a junction. The path is used only when Win32
    // normalization leaves it unchanged (a trailing dot or space would otherwise name a different directory) and
    // when opening it the way the child will lands on the held file.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static string PinnedLaunchPath(FileStream held, SafeFileHandle anchor)
    {
        // The reopen resolved the pathname again, so it must land on the very file the writer created.
        var writtenIdentity = HandleBoundFile.IdentityOf(anchor);
        if (HandleBoundFile.IdentityOf(held.SafeFileHandle) != writtenIdentity)
        {
            throw new IOException("The PowerShell script path was redirected before the script could be locked.");
        }

        var launchPath = ToWin32Path(HandleBoundFile.FinalPathOf(held.SafeFileHandle));
        var components = launchPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (Path.GetFullPath(launchPath) != launchPath
            || components.Skip(1).Any(component => component.EndsWith('.') || component.EndsWith(' ')))
        {
            throw new IOException($"The PowerShell script path '{launchPath}' does not survive Win32 normalization.");
        }

        using var check = HandleBoundFile.OpenAttributesOnly(launchPath);
        if (HandleBoundFile.IdentityOf(check) != writtenIdentity)
        {
            throw new IOException($"The PowerShell script path '{launchPath}' does not open the held script.");
        }

        return launchPath;
    }

    // Windows PowerShell 5.1 refuses a -File path in the \\?\ form, so the final path goes back to its drive or UNC
    // form; PinnedLaunchPath then checks that form still names the same file.
    internal static string ToWin32Path(string finalPath)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string localPrefix = @"\\?\";
        if (finalPath.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + finalPath[uncPrefix.Length..];
        }

        return finalPath.StartsWith(localPrefix, StringComparison.Ordinal) ? finalPath[localPrefix.Length..] : finalPath;
    }

    /// <summary>
    ///     Releases the hold and deletes the file. On Windows the delete goes through a reopen of the held file's own
    ///     handle, so a pathname redirected after the child ran cannot steer it at another file. A failed delete is
    ///     ignored: a leftover temporary script must not change the outcome of the run.
    /// </summary>
    public void Dispose()
    {
        _held.Dispose();
        Delete(_anchor, _createdPath);
    }

    // The anchor exists only on Windows; elsewhere, or when the anchor could not be taken, the delete goes by pathname.
    static void Delete(SafeFileHandle? anchor, string path)
    {
        if (anchor is null)
        {
            DeleteQuietly(path);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            HandleBoundFile.TryDeleteByHandle(anchor);
        }

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
