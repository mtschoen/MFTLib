using MFTLib.Index;

namespace MFTLib.Tests;

/// <summary>Deterministic teardown for a drive block or snapshot of drive blocks a test built itself.</summary>
static class SnapshotTestExtensions
{
    internal static Task ReleaseNowAsync(this DriveBlock driveBlock) =>
        driveBlock.ReleaseState.DrainAndReleaseAsync().AsTask();

    internal static async Task ReleaseNowAsync(this IReadOnlyList<DriveBlock> snapshot)
    {
        foreach (var driveBlock in snapshot)
        {
            await driveBlock.ReleaseState.DrainAndReleaseAsync();
        }
    }
}
