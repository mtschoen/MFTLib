using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Frame kinds of the broker wire. The control pipe carries the request kinds that carry a
///     request id (<see cref="OpenChannel" />, <see cref="QueryVolume" />,
///     <see cref="GrowUsnJournal" />) and their replies. A drive pipe carries one operation for
///     the drive its <see cref="OpenChannel" /> named: the client writes one request
///     (<see cref="ArmAndScan" /> or <see cref="StartWatch" />) and every frame after it flows
///     from the host.
/// </summary>
internal enum BrokerFrameKind : byte
{
    OpenChannel = 1,
    ChannelOpened = 2,
    QueryVolume = 3,
    VolumeInfo = 4,
    GrowUsnJournal = 5,
    UsnJournalSettings = 6,
    Error = 7,
    Heartbeat = 8,
    Stalled = 9,
    ArmAndScan = 10,
    Cursor = 11,
    ScanProgress = 12,
    CatchUpLost = 13,
    ScanReady = 14,
    JournalBatch = 15,
    StartWatch = 16,
    CaughtUp = 17,
    ScanCompleted = 18
}

/// <summary>
///     The journal facts a <see cref="BrokerFrameKind.CatchUpLost" /> frame carries: the fields of
///     the <see cref="JournalCheckpointLoss" /> the host proved against the live journal. The drive
///     is the channel's, so the wire carries neither the drive nor which check produced the loss.
/// </summary>
internal readonly record struct BrokerCatchUpLoss(
    JournalCheckpointLossCause Cause,
    long CheckpointUsn,
    long FirstUsn,
    long NextUsn,
    long AllocationDelta,
    long MaximumSize,
    long? BytesBehind,
    long? SizeThatWouldHaveRetained);

internal readonly record struct BrokerFrame
{
    public BrokerFrameKind Kind { get; private init; }

    /// <summary>Nonzero on every control request and reply; zero on an Error written to a drive pipe.</summary>
    public uint RequestId { get; private init; }

    public string? Drive { get; private init; }
    public string? PipeName { get; private init; }
    public string? SectionName { get; private init; }
    public BrokerScanProfile Profile { get; private init; }
    public bool IncludeFreed { get; private init; }
    public UsnJournalCursor Cursor { get; private init; }
    public UsnJournalEntry[] Entries { get; private init; }
    public long SkippedRecordCount { get; private init; }
    public string? Message { get; private init; }
    public IReadOnlyList<string> KeepFileNames { get; private init; }

    /// <summary>
    ///     A scan's progress. The drive belongs to the channel, so a frame read off the wire carries
    ///     an empty <see cref="BrokerScanProgress.DriveLetter" />.
    /// </summary>
    public BrokerScanProgress? Progress { get; private init; }

    public uint BytesPerFileRecordSegment { get; private init; }
    public long MftValidDataLength { get; private init; }

    // Journal sizing payload of the GrowUsnJournal request and UsnJournalSettings reply.
    public long JournalMaximumSize { get; private init; }
    public long JournalAllocationDelta { get; private init; }

    internal BrokerCatchUpLoss? CatchUpLoss { get; private init; }

    public string RequireDrive()
    {
        return Drive ?? throw new InvalidDataException($"{Kind} frame is missing its drive field");
    }

    public string RequirePipeName()
    {
        return PipeName ?? throw new InvalidDataException($"{Kind} frame is missing its pipe name field");
    }

    public string RequireSectionName()
    {
        return SectionName ?? throw new InvalidDataException($"{Kind} frame is missing its section name field");
    }

    public string RequireMessage()
    {
        return Message ?? throw new InvalidDataException($"{Kind} frame is missing its message field");
    }

    /// <summary>The proven loss of a <see cref="BrokerFrameKind.CatchUpLost" /> frame, for the channel's drive.</summary>
    internal JournalCheckpointLoss RequireCatchUpLoss(char driveLetter)
    {
        var loss = CatchUpLoss ?? throw new InvalidDataException($"{Kind} frame is missing its loss field");
        return new JournalCheckpointLoss(driveLetter, JournalCheckpointLossDetection.ScanCatchUp, loss.Cause,
            loss.AllocationDelta, loss.MaximumSize)
        {
            CheckpointUsn = loss.CheckpointUsn,
            FirstUsn = loss.FirstUsn,
            NextUsn = loss.NextUsn,
            BytesBehind = loss.BytesBehind,
            SizeThatWouldHaveRetained = loss.SizeThatWouldHaveRetained
        };
    }

    // Per-kind factories: the only way to build a valid frame. Each initializes Entries and
    // KeepFileNames (empty for kinds that carry none) so consumers never see a null.

    public static BrokerFrame OpenChannel(uint requestId, string drive, string pipeName)
    {
        return Empty(BrokerFrameKind.OpenChannel, requestId) with { Drive = drive, PipeName = pipeName };
    }

    public static BrokerFrame ChannelOpened(uint requestId)
    {
        return Empty(BrokerFrameKind.ChannelOpened, requestId);
    }

    public static BrokerFrame QueryVolume(uint requestId, string drive)
    {
        return Empty(BrokerFrameKind.QueryVolume, requestId) with { Drive = drive };
    }

    public static BrokerFrame VolumeInfo(uint requestId, uint bytesPerFileRecordSegment, long mftValidDataLength)
    {
        return Empty(BrokerFrameKind.VolumeInfo, requestId) with
        {
            BytesPerFileRecordSegment = bytesPerFileRecordSegment,
            MftValidDataLength = mftValidDataLength
        };
    }

    // The host refuses any requested maximum at or below the current one (grow only).
    public static BrokerFrame GrowUsnJournal(uint requestId, string drive, long maximumSize, long allocationDelta)
    {
        return Empty(BrokerFrameKind.GrowUsnJournal, requestId) with
        {
            Drive = drive,
            JournalMaximumSize = maximumSize,
            JournalAllocationDelta = allocationDelta
        };
    }

    // The post-change journal sizing, read back from the volume after FSCTL_CREATE_USN_JOURNAL.
    public static BrokerFrame UsnJournalSettings(uint requestId, long maximumSize, long allocationDelta)
    {
        return Empty(BrokerFrameKind.UsnJournalSettings, requestId) with
        {
            JournalMaximumSize = maximumSize,
            JournalAllocationDelta = allocationDelta
        };
    }

    public static BrokerFrame Error(uint requestId, string message)
    {
        return Empty(BrokerFrameKind.Error, requestId) with { Message = message };
    }

    public static BrokerFrame Heartbeat()
    {
        return Empty(BrokerFrameKind.Heartbeat, 0);
    }

    public static BrokerFrame Stalled(string message)
    {
        return Empty(BrokerFrameKind.Stalled, 0) with { Message = message };
    }

    public static BrokerFrame ArmAndScan(string sectionName, BrokerScanProfile profile,
        IReadOnlyList<string>? keepFileNames = null, bool includeFreed = false)
    {
        return Empty(BrokerFrameKind.ArmAndScan, 0) with
        {
            SectionName = sectionName,
            Profile = profile,
            IncludeFreed = includeFreed,
            KeepFileNames = keepFileNames ?? Array.Empty<string>()
        };
    }

    // Named ArmedCursor to avoid colliding with the Cursor property.
    public static BrokerFrame ArmedCursor(UsnJournalCursor cursor)
    {
        return Empty(BrokerFrameKind.Cursor, 0) with { Cursor = cursor };
    }

    public static BrokerFrame ScanProgress(BrokerScanProgress progress)
    {
        return Empty(BrokerFrameKind.ScanProgress, 0) with { Progress = progress };
    }

    internal static BrokerFrame CatchUpLost(BrokerCatchUpLoss loss)
    {
        return Empty(BrokerFrameKind.CatchUpLost, 0) with { CatchUpLoss = loss };
    }

    public static BrokerFrame ScanReady(long skippedRecordCount)
    {
        return Empty(BrokerFrameKind.ScanReady, 0) with { SkippedRecordCount = skippedRecordCount };
    }

    public static BrokerFrame JournalBatch(UsnJournalCursor cursor, UsnJournalEntry[] entries)
    {
        return Empty(BrokerFrameKind.JournalBatch, 0) with { Cursor = cursor, Entries = entries };
    }

    public static BrokerFrame StartWatch(UsnJournalCursor since)
    {
        return Empty(BrokerFrameKind.StartWatch, 0) with { Cursor = since };
    }

    // The watch has delivered its whole initial journal backlog; everything after this frame is
    // a live entry.
    public static BrokerFrame CaughtUp()
    {
        return Empty(BrokerFrameKind.CaughtUp, 0);
    }

    // The cursor a scan's catch-up advanced to from the armed one; the entries it read stay on the host.
    public static BrokerFrame ScanCompleted(UsnJournalCursor advanced)
    {
        return Empty(BrokerFrameKind.ScanCompleted, 0) with { Cursor = advanced };
    }

    static BrokerFrame Empty(BrokerFrameKind kind, uint requestId)
    {
        return new BrokerFrame
        {
            Kind = kind,
            RequestId = requestId,
            Entries = Array.Empty<UsnJournalEntry>(),
            KeepFileNames = Array.Empty<string>()
        };
    }
}
