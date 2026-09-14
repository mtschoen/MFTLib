using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Asserts that this process no longer holds a block file open. The question is the same on
///     both platforms and the only reliable answer differs: Linux reads the process's own
///     descriptor table, Windows asks the operating system for an exclusive handle, which the
///     live mapping's <see cref="FileShare.ReadWrite" /> handle refuses. A file that does not exist
///     is not held.
/// </summary>
public static class BlockFileHoldAssertions
{
    /// <summary>Asserts that this process holds no open handle to the block file.</summary>
    /// <param name="blockPath">The block file to probe.</param>
    public static void AssertNotHeld(string blockPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(blockPath);
        Assert.IsFalse(IsHeldByThisProcess(blockPath),
            $"{blockPath} is still open after everything that owns it was disposed");
    }

    static bool IsHeldByThisProcess(string blockPath)
    {
        return OperatingSystem.IsWindows()
            ? !CanOpenExclusively(blockPath)
            : AppearsInProcessDescriptorTable(blockPath);
    }

    static bool CanOpenExclusively(string blockPath)
    {
        if (!File.Exists(blockPath))
        {
            return true;
        }

        try
        {
            using var probe = new FileStream(blockPath, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    static bool AppearsInProcessDescriptorTable(string blockPath)
    {
        var target = Path.GetFullPath(blockPath);
        foreach (var descriptor in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
        {
            string? resolved;
            try
            {
                resolved = File.ResolveLinkTarget(descriptor, returnFinalTarget: false)?.FullName;
            }
            catch (IOException)
            {
                // The descriptor closed between the listing and the resolve. Nothing to report.
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (string.Equals(resolved, target, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
