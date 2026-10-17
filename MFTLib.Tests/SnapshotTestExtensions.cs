using MFTLib.Index;

namespace MFTLib.Tests;

/// <summary>Deterministic teardown for a snapshot of drive blocks a test built itself.</summary>
static class SnapshotTestExtensions
{
    internal static async Task ReleaseNowAsync(this IReadOnlyList<DriveBlock> snapshot)
    {
        foreach (var driveBlock in snapshot)
        {
            await driveBlock.ReleaseState.ReleaseAsync();
        }
    }
}
