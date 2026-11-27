using MFTLib.Index;

namespace MFTLib;

/// <summary>Re-reads the surviving name and parent of each file a watch batch reports a hard-link change for.</summary>
sealed partial class JournalBrokerHost
{
    /// <summary>
    ///     One answer per distinct (record number, sequence number) whose entries carry
    ///     <see cref="UsnReason.HardLinkChange" />, ascending by record, read through the same native record reader as
    ///     an inline content read. The journal record alone does not say which of a file's links survives, so the
    ///     index applies these answers rather than the record's own name and parent. The read runs while the batch's
    ///     Processing step is published and republishes it per answer, so a long read keeps the watch channel's
    ///     progress clock moving. A read that throws leaves every unanswered file Unreadable: its message is logged,
    ///     never classified.
    /// </summary>
    FileNameResolution[] ResolveLinkChanges(VolumeChannel channel, UsnJournalEntry[] entries,
        CancellationToken cancellationToken)
    {
        var files = entries.Where(entry => (entry.Reason & UsnReason.HardLinkChange) != 0)
            .Select(entry => (entry.RecordNumber, entry.SequenceNumber))
            .Distinct()
            .OrderBy(file => file.RecordNumber)
            .ThenBy(file => file.SequenceNumber)
            .ToArray();
        if (files.Length == 0)
        {
            return [];
        }

        var answers = new FileNameResolution?[files.Length];
        var requests = files.Select((file, position) =>
            new InlineContentRequest(position, checked((long)file.RecordNumber), file.SequenceNumber)).ToArray();
        try
        {
            _volumeAccess.ReadFileNames(channel.Volume, requests, (position, outcome, content) =>
            {
                var (recordNumber, sequenceNumber) = files[position];
                answers[position] = FileNameResolution.FromLookup(recordNumber, sequenceNumber, outcome, content);
                channel.Pipe.Processing(JournalBatchStep);
            }, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            BrokerDiagnostics.Log(channel.Tag,
                $"File name lookup for {files.Length} hard-link changes failed; they stay unresolved: {exception.Message}");
        }

        return
        [
            .. answers.Select((answer, position) =>
                answer ?? FileNameResolution.Unreadable(files[position].RecordNumber, files[position].SequenceNumber))
        ];
    }
}
