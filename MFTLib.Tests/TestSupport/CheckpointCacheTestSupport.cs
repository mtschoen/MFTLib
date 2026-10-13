using MFTLib.Index;

namespace MFTLib.Tests.TestSupport;

internal static class CheckpointCacheTestSupport
{
    public const ulong CachedJournalIdentifier = 0xABCD;
    public const long CachedNextUsn = 1_000_000;
    public static readonly DateTime FixedMoment = new(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);

    public static Task<MftBlockProduceResult> ProduceMftShapedBlock(
        MftBlockProduceRequest request, CancellationToken cancellationToken) =>
        SeededBlocks.Produce(request, CachedJournalIdentifier, CachedNextUsn, FixedMoment);

    public static JournalWindow HealthyWindow =>
        new(CachedJournalIdentifier, 0, CachedNextUsn, 64, 128L * 1024 * 1024);

    public static JournalWindow TrimmedWindow =>
        new(CachedJournalIdentifier, CachedNextUsn + 500, CachedNextUsn + 4_000, 64, 128L * 1024 * 1024);

    public static IDisposable OverrideJournals(IReadOnlyDictionary<DriveKey, JournalWindow> byDrive) =>
        JournalCheckpointCheck.OverrideJournalForTest(
            drive => byDrive.TryGetValue(drive, out var window) ? window : null);
}
