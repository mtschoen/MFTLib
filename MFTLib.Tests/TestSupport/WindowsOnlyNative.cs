using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Guards for the two native behaviours only the Windows build has. Each returns false on Windows
///     and otherwise marks the test inconclusive and returns true, so the test returns early.
/// </summary>
internal static class WindowsOnlyNative
{
    /// <summary>
    ///     The volume export, ParseMFTRecordsWithProgress, parses a volume handle (or an image file
    ///     handle standing in for one) only on Windows; elsewhere it is a stub that returns an error.
    /// </summary>
    internal static bool SkipWithoutVolumeParse() =>
        Skip("ParseMFTRecordsWithProgress parses a volume handle only on Windows.");

    /// <summary>The SetFailPlatformRead hook is consulted only by the Windows platform read.</summary>
    internal static bool SkipWithoutPlatformReadHook() =>
        Skip("SetFailPlatformRead is consulted only by the Windows platform read.");

    static bool Skip(string reason)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        Assert.Inconclusive(reason);
        return true;
    }
}
