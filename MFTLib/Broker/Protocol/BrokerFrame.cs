namespace MFTLib;

public enum BrokerFrameKind : byte
{
    ArmAndScan = 1,
    StartWatch = 2,
    Shutdown = 3,
    ScanReady = 4,
    Cursor = 5,
    JournalBatch = 6,
    Error = 7,
    Heartbeat = 8,
    EndWatch = 9,
    EndWatchAck = 10,
    ScanProgress = 11,
    Warning = 12,
    QueryVolumes = 13,
    VolumeInfo = 14,
    DisarmDrive = 15
}

public readonly record struct BrokerFrame
{
    // Scan and volume-query frames belong to no live arm; clients never issue zero.
    public const uint NoArmEpoch = 0;
    public BrokerFrameKind Kind { get; private init; }
    // Live batches and errors are delivered only while this is the drive's current epoch.
    public uint ArmEpoch { get; private init; }
    public string? Drive { get; private init; }
    public UsnJournalCursor Cursor { get; private init; }
    public UsnJournalEntry[] Entries { get; private init; }
    public string? MmfName { get; private init; }
    public long RecordCount { get; private init; }
    public long RowCount { get; private init; }
    public long NamePoolUsedBytes { get; private init; }
    public long SkippedRecordCount { get; private init; }
    public string? Message { get; private init; }
    public string? DrivesSpec { get; private init; }
    public IReadOnlyList<string> KeepFileNames { get; private init; }
    public BrokerScanProgress? Progress { get; private init; }
    public uint BytesPerFileRecordSegment { get; private init; }
    public long MftValidDataLength { get; private init; }

    // Drive-carrying frames always carry a real (possibly empty, never null) drive
    // string: BrokerProtocol.ReadFrame decodes it via a length-prefixed string, not a
    // nullable field. These turn that protocol invariant into a clear diagnostic if it
    // is ever violated, instead of a silent null-forgiving `!`.
    public string RequireDrive()
    {
        return Drive ?? throw new InvalidDataException($"{Kind} frame is missing its drive field");
    }

    public string RequireMmfName()
    {
        return MmfName ?? throw new InvalidDataException($"{Kind} frame is missing its MMF name field");
    }

    public string RequireMessage()
    {
        return Message ?? throw new InvalidDataException($"{Kind} frame is missing its message field");
    }

    // Per-kind factories: the only way to build a valid frame. Each initializes
    // Entries (empty for non-batch kinds) so consumers never see a null Entries.
    // The Cursor-kind factory is named ArmedCursor to avoid colliding with the
    // Cursor property.
    public static BrokerFrame ArmAndScan(string drivesSpec, IReadOnlyList<string>? keepFileNames = null)
    {
        return new BrokerFrame
        {
            Kind = BrokerFrameKind.ArmAndScan,
            Entries = Array.Empty<UsnJournalEntry>(),
            DrivesSpec = drivesSpec,
            KeepFileNames = keepFileNames ?? Array.Empty<string>()
        };
    }

    public static BrokerFrame StartWatch(string drivesSpec)
    {
        return new BrokerFrame
        {
            Kind = BrokerFrameKind.StartWatch,
            Entries = Array.Empty<UsnJournalEntry>(),
            DrivesSpec = drivesSpec,
            KeepFileNames = Array.Empty<string>()
        };
    }

    // Retires one drive from the live watch generation and leaves every other drive
    // running. EndWatch stays the generation-wide stop and keeps its acknowledgement;
    // this one needs none, because the host reads request frames in order and the client
    // completes the drive's channel itself before writing this.
    public static BrokerFrame DisarmDrive(string drive)
    {
        return new BrokerFrame
        {
            Kind = BrokerFrameKind.DisarmDrive,
            Entries = Array.Empty<UsnJournalEntry>(),
            Drive = drive,
            KeepFileNames = Array.Empty<string>()
        };
    }

    public static BrokerFrame Shutdown()
    {
        return Empty(BrokerFrameKind.Shutdown);
    }

    public static BrokerFrame Heartbeat()
    {
        return Empty(BrokerFrameKind.Heartbeat);
    }

    public static BrokerFrame EndWatch()
    {
        return Empty(BrokerFrameKind.EndWatch);
    }

    public static BrokerFrame EndWatchAck()
    {
        return Empty(BrokerFrameKind.EndWatchAck);
    }

    public static BrokerFrame ScanReady(string mmfName, long rowCount, long namePoolUsedBytes, long skippedRecordCount)
    {
        return new BrokerFrame
        {
            Kind = BrokerFrameKind.ScanReady,
            Entries = Array.Empty<UsnJournalEntry>(),
            MmfName = mmfName,
            RowCount = rowCount,
            NamePoolUsedBytes = namePoolUsedBytes,
            SkippedRecordCount = skippedRecordCount,
            KeepFileNames = Array.Empty<string>()
        };
    }

    public static BrokerFrame ArmedCursor(string drive, UsnJournalCursor cursor)
    {
        return new BrokerFrame
        {
            Kind = BrokerFrameKind.Cursor,
            Entries = Array.Empty<UsnJournalEntry>(),
            Drive = drive,
            Cursor = cursor,
            KeepFileNames = Array.Empty<string>()
        };
    }

    public static BrokerFrame JournalBatch(string drive, uint armEpoch, UsnJournalCursor cursor, UsnJournalEntry[] entries)
    {
        return new BrokerFrame
        {
            Kind = BrokerFrameKind.JournalBatch,
            ArmEpoch = armEpoch,
            Entries = entries,
            Drive = drive,
            Cursor = cursor,
            KeepFileNames = Array.Empty<string>()
        };
    }

    public static BrokerFrame ScanProgress(BrokerScanProgress progress)
    {
        return new BrokerFrame
        {
            Kind = BrokerFrameKind.ScanProgress,
            Entries = Array.Empty<UsnJournalEntry>(),
            Drive = progress.DriveLetter,
            Progress = progress,
            KeepFileNames = Array.Empty<string>()
        };
    }

    public static BrokerFrame Error(string drive, uint armEpoch, string message)
    {
        return new BrokerFrame
        {
            Kind = BrokerFrameKind.Error,
            ArmEpoch = armEpoch,
            Entries = Array.Empty<UsnJournalEntry>(),
            Drive = drive,
            KeepFileNames = Array.Empty<string>(),
            Message = message
        };
    }

    // A non-fatal, per-drive scan degradation: unlike Error, the drive still produced
    // a usable scan result. The message explains what was lost, not that the drive
    // failed.
    public static BrokerFrame Warning(string drive, string message)
    {
        return new BrokerFrame
        {
            Kind = BrokerFrameKind.Warning,
            Entries = Array.Empty<UsnJournalEntry>(),
            Drive = drive,
            KeepFileNames = Array.Empty<string>(),
            Message = message
        };
    }

    // A request for volume information on each drive in drivesSpec, without arming a
    // scan or allocating any shared-memory map. drivesSpec uses three-field
    // arm-and-scan tokens ("letter:0:0", comma-joined), with unused journal fields
    // and no section or profile, so the host reads them through ParseScanSpec.
    public static BrokerFrame QueryVolumes(string drivesSpec)
    {
        return new BrokerFrame
        {
            Kind = BrokerFrameKind.QueryVolumes,
            Entries = Array.Empty<UsnJournalEntry>(),
            DrivesSpec = drivesSpec,
            KeepFileNames = Array.Empty<string>()
        };
    }

    // One drive's answer to a QueryVolumes request. mftRecordCount is the pre-computed
    // NtfsVolumeInformation.MftRecordCount value; bytesPerFileRecordSegment and
    // mftValidDataLength are the two raw fields it was derived from, carried alongside so
    // a client can reconstruct NtfsVolumeInformation.MftRecordCount independently instead
    // of trusting the transmitted count outright.
    public static BrokerFrame VolumeInfo(
        string drive, long mftRecordCount, uint bytesPerFileRecordSegment, long mftValidDataLength)
    {
        return new BrokerFrame
        {
            Kind = BrokerFrameKind.VolumeInfo,
            Entries = Array.Empty<UsnJournalEntry>(),
            Drive = drive,
            RecordCount = mftRecordCount,
            BytesPerFileRecordSegment = bytesPerFileRecordSegment,
            MftValidDataLength = mftValidDataLength,
            KeepFileNames = Array.Empty<string>()
        };
    }

    static BrokerFrame Empty(BrokerFrameKind kind)
    {
        return new BrokerFrame
        {
            Kind = kind,
            Entries = Array.Empty<UsnJournalEntry>(),
            KeepFileNames = Array.Empty<string>()
        };
    }
}
