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
    VolumeInfo = 14
}

public readonly record struct BrokerFrame
{
    public BrokerFrameKind Kind { get; private init; }
    public string? Drive { get; private init; }
    public UsnJournalCursor Cursor { get; private init; }
    public UsnJournalEntry[] Entries { get; private init; }
    public string? MmfName { get; private init; }
    public long RecordCount { get; private init; }
    public long ByteLength { get; private init; }
    public string? Message { get; private init; }
    public string? DrivesSpec { get; private init; }
    public IReadOnlyList<string> KeepFileNames { get; private init; }
    public BrokerScanProgress? Progress { get; private init; }
    public uint BytesPerFileRecordSegment { get; private init; }
    public long MftValidDataLength { get; private init; }

    // Cursor/JournalBatch/Error frames always carry a real (possibly empty, never
    // null) drive string: BrokerProtocol.ReadFrame decodes it via a length-prefixed
    // string, not a nullable field. These turn that protocol invariant into a clear
    // diagnostic if it is ever violated, instead of a silent null-forgiving `!`.
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

    public static BrokerFrame ScanReady(string mmfName, long recordCount, long byteLength)
    {
        return new BrokerFrame
        {
            Kind = BrokerFrameKind.ScanReady,
            Entries = Array.Empty<UsnJournalEntry>(),
            MmfName = mmfName,
            RecordCount = recordCount,
            ByteLength = byteLength,
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

    public static BrokerFrame JournalBatch(string drive, UsnJournalCursor cursor, UsnJournalEntry[] entries)
    {
        return new BrokerFrame
        {
            Kind = BrokerFrameKind.JournalBatch,
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

    public static BrokerFrame Error(string drive, string message)
    {
        return new BrokerFrame
        {
            Kind = BrokerFrameKind.Error,
            Entries = Array.Empty<UsnJournalEntry>(),
            Drive = drive,
            KeepFileNames = Array.Empty<string>(),
            Message = message
        };
    }

    // A non-fatal, per-drive degradation: unlike Error, the drive still produced a
    // usable result (the scan succeeded, or the watch is resuming from a fresh
    // position) - the message explains what was lost, not that the drive failed.
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
    // scan or allocating any shared-memory map. drivesSpec uses the same watch-token
    // shape as StartWatch ("letter:0:0" per drive, comma-joined) - the journalId/nextUsn
    // fields are unused here, but reusing the shape lets the host parse both with the
    // same ParseScanSpec helper.
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
