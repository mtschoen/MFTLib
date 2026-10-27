using MFTLib.Index;

namespace MFTLib.Tests;

/// <summary>Deterministic teardown for a root block or snapshot of root blocks a test built itself.</summary>
static class SnapshotTestExtensions
{
    internal static Task ReleaseNowAsync(this RootBlock rootBlock) =>
        rootBlock.ReleaseState.DrainAndReleaseAsync().AsTask();

    internal static async Task ReleaseNowAsync(this IReadOnlyList<RootBlock> snapshot)
    {
        foreach (var rootBlock in snapshot)
        {
            await rootBlock.ReleaseState.DrainAndReleaseAsync();
        }
    }
}
