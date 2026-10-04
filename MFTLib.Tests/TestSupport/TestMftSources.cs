using Microsoft.VisualStudio.TestTools.UnitTesting;
using MFTLib.Index;

namespace MFTLib.Tests.TestSupport;

/// <summary>Builds <see cref="MftIndexSource" /> values for tests that exercise only one half of one.</summary>
internal static class TestMftSources
{
    /// <summary>A source whose watch is <paramref name="watchSource" /> and whose scan fails the test if reached.</summary>
    internal static MftIndexSource WatchOnly(IIndexWatchSource watchSource) => new(
        (_, _) => throw new AssertFailedException("This test watches without scanning, so the MFT producer must not run."),
        watchSource);
}
