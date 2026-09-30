namespace MFTLib.Tests.TestSupport;

/// <summary>Host-side steps of a scan that a test scripts on a <see cref="ScriptedBroker" />.</summary>
internal static class ScriptedScanSteps
{
    /// <summary>Answers a scan's volume query, accepts its drive channel, and reads its ArmAndScan order.</summary>
    public static async Task<(Stream Pipe, BrokerFrame Order)> AcceptScanAsync(this ScriptedBroker broker,
        NtfsVolumeInformation volume)
    {
        await broker.AnswerQueryVolumeAsync(volume);
        var pipe = await broker.AcceptChannelAsync();
        var order = await HostChannelHarness.ReadFrameAsync(pipe)
                    ?? throw new InvalidOperationException("The scan channel closed before its ArmAndScan order.");
        return (pipe, order);
    }

    /// <summary>Writes a two-record block into a section the client created, as the host's section writer would.</summary>
    public static void WriteSection(this ScriptedBroker broker, string sectionName, UsnJournalCursor cursor)
    {
        using var writer = new RecordingBlockSectionWriter(broker.Sections.Resolve);
        writer.Write(sectionName, cursor,
            [
                [new MftRecord(5, 5, new MftRecordFields(3), ".", null)],
                [new MftRecord(20, 5, new MftRecordFields(1), "file.txt", null)]
            ],
            MftBlockRowFilter.Full, default, CancellationToken.None);
    }
}
