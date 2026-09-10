using System.Threading.Channels;
using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     The per-drive half of the watch source: adding or replacing one drive on a stream that is
///     already running, and taking one off it. Both share the same three phases, and the order
///     inside them is what keeps them from hanging: claim every piece of per-drive state under the
///     lock, call the client, then await the reader the client just told to finish.
/// </summary>
public sealed partial class BrokerIndexWatchSource
{
    public async Task ArmDriveAsync(IndexWatchTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var driveLetter = char.ToUpperInvariant(target.DriveLetter);
        JournalBrokerClient client;
        Task? previousReader;
        lock (_streamLock)
        {
            client = ClaimedStreamLocked("arm").Client;

            // Both halves of the replacement are claimed before anything is awaited. The stop
            // flag is what tells this drive's previous reader that its channel was completed on
            // purpose, so it returns quietly rather than reading a normal completion as the whole
            // generation ending and completing the merged channel for every other drive too.
            _stopRequestedDrives.Add(driveLetter);
            _drivesAwaitingReader.Add(driveLetter);
            _armGenerationsByDrive[driveLetter] = _armGenerationsByDrive.GetValueOrDefault(driveLetter) + 1;
            _readersByDrive.Remove(driveLetter, out previousReader);
        }

        // Call the client first, await the previous reader second, never the other way round.
        // SendStartWatchAsync is what completes that reader's channel, through the client's
        // ArmDriveLocked, so awaiting it first would await a task nothing has told to finish.
        // CancellationToken.None for the same reason StartWatching uses it: cancelling this write
        // can leave the acknowledgement to terminate the next watch.
        try
        {
            await client.SendStartWatchAsync(BuildCursors([target]), CancellationToken.None)
                .ConfigureAwait(false);
            await AwaitRetiredReaderAsync(previousReader, cancellationToken).ConfigureAwait(false);

            lock (_streamLock)
            {
                _stopRequestedDrives.Remove(driveLetter);
                StartDriveReaderLocked(ClaimedStreamLocked("arm"), target, driveLetter);

                // Cleared under the same lock that puts the reader back, so no other drive's
                // fault can see this one both absent from the map and not awaited.
                _drivesAwaitingReader.Remove(driveLetter);
            }
        }
        catch (Exception armFailure)
        {
            // An arm that failed leaves the drive stopped and with no reader coming, so holding
            // the stream open for it would outlast the only thing that was ever going to restore
            // it.
            RetireDriveWithoutReader(driveLetter, armFailure);
            throw;
        }
    }

    public async Task DisarmDriveAsync(char driveLetter, CancellationToken cancellationToken)
    {
        var normalizedLetter = char.ToUpperInvariant(driveLetter);
        JournalBrokerClient client;
        Task? previousReader;
        lock (_streamLock)
        {
            client = ClaimedStreamLocked("disarm").Client;

            // The stop flag stays set after this call: the drive is not armed until an arm clears it.
            _stopRequestedDrives.Add(normalizedLetter);

            // So does the marker, and that is the point of it. It spans the caller's whole
            // disarm-to-rearm window, which for a rescan is a full MFT scan, and that window is
            // exactly what WasLastReader must not read as the reader map emptying for good.
            // ArmDriveAsync clears it in its own finally, whether the re-arm starts a fresh reader
            // or fails outright, so only a disarm that itself throws has to clear it below.
            _drivesAwaitingReader.Add(normalizedLetter);
            _armGenerationsByDrive[normalizedLetter] =
                _armGenerationsByDrive.GetValueOrDefault(normalizedLetter) + 1;
            _readersByDrive.Remove(normalizedLetter, out previousReader);
        }

        try
        {
            // Same order and the same reason as ArmDriveAsync: SendDisarmDriveAsync completes this
            // drive's channel before the frame reaches the wire, so it is what ends the reader.
            await client.SendDisarmDriveAsync(
                JournalBrokerClient.NormalizeDriveLetter(normalizedLetter.ToString()),
                CancellationToken.None).ConfigureAwait(false);
            await AwaitRetiredReaderAsync(previousReader, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception disarmFailure)
        {
            // Only a failed disarm clears the marker here, because a failed disarm has no re-arm
            // coming that would clear it. Unlike the stop flag, which only ever mis-describes its
            // own drive, a marker left behind gates every other drive's fault-path exit, so it
            // would stop the merged channel from ever completing again for anyone. A throw after
            // the client completed this drive's channel but before the frame reached the broker
            // needs no other repair: the retired reader ends on that completion and returns
            // quietly because its stop flag is still set, the broker's later frames for this drive
            // carry a superseded arm epoch and are dropped by the client's demux, and the next
            // ArmDriveAsync finds no reader to retire and simply starts a fresh one.
            RetireDriveWithoutReader(normalizedLetter, disarmFailure);
            throw;
        }
    }

    /// <summary>
    ///     Clears the awaiting-reader marker of a drive whose arm or disarm failed, and ends the
    ///     merged stream when doing so leaves it with no reader and none on the way. The failure
    ///     is published first, under the arm generation current at the moment it happened, so the
    ///     end names its reason instead of arriving as a bare completion. Without this the caller
    ///     is the only one told: the stream would stay open with nothing alive that could ever
    ///     write to it again, and a consumer's enumeration would wait on it until its own token
    ///     was cancelled.
    /// </summary>
    void RetireDriveWithoutReader(char driveLetter, Exception failure)
    {
        ChannelWriter<TaggedItem>? writer;
        int armGeneration;
        lock (_streamLock)
        {
            _drivesAwaitingReader.Remove(driveLetter);
            if (_stream is not { } stream || _readersByDrive.Count > 0 || _drivesAwaitingReader.Count > 0)
            {
                return;
            }

            writer = stream.Writer;
            armGeneration = _armGenerationsByDrive.GetValueOrDefault(driveLetter);
        }

        writer.TryWrite(new TaggedItem(new DriveWatchFailure(driveLetter, failure), driveLetter, armGeneration));
        writer.TryComplete();
    }

    static Task AwaitRetiredReaderAsync(Task? previousReader, CancellationToken cancellationToken)
    {
        // A failure or cancellation while retiring the old reader leaves that drive stopped and
        // surfaces to RescanAsync, which is already where a failed re-arm is contracted to appear.
        return previousReader is null ? Task.CompletedTask : previousReader.WaitAsync(cancellationToken);
    }
}
