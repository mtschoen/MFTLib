using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     MFTLib issue 252, host side: an EndWatch stops only the generation it names, and a StartWatch
///     for a new generation stops every drive of the old one first, so a client whose stop gave up on
///     the acknowledgement, or never got its EndWatch onto the wire, can start the next watch on the
///     same connection.
/// </summary>
public partial class JournalBrokerHostTests
{
    [TestMethod]
    public async Task EndWatch_ForAnOlderGeneration_IsAcknowledgedWithoutStoppingTheLiveGeneration()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var invocations = new List<WatchInvocation>();
        var host = CreateRecordingWatchHost(invocations);
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cancellationSource.Token;
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, token);

        await WriteBrokerRequestAsync(clientSide, writer => BrokerProtocol.WriteStartWatch(writer, 2, "C:7:100:1"), token);
        var watchC = (await WaitForInvocationCountAsync(invocations, 1, token)).Single();

        await WriteBrokerRequestAsync(clientSide, writer => BrokerProtocol.WriteEndWatch(writer, 1), token);
        var staleAcknowledgement = await ReadAcknowledgementAsync(clientSide, token);
        Assert.AreEqual(BrokerFrameKind.EndWatchAck, staleAcknowledgement.Kind);
        Assert.AreEqual(1U, staleAcknowledgement.WatchGeneration);
        Assert.IsFalse(watchC.Stopped.Task.IsCompleted, "An EndWatch for an older generation stopped the live one.");

        var acknowledgement = await EndWatchGenerationAndReadAcknowledgementAsync(clientSide, 2, token);
        Assert.AreEqual(2U, acknowledgement.WatchGeneration);
        Assert.IsTrue(watchC.Stopped.Task.IsCompleted, "The live generation's own EndWatch did not stop it.");

        await ShutdownAsync(clientSide, token);
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_ForANewGeneration_StopsEveryDriveOfTheOldGenerationFirst()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var invocations = new List<WatchInvocation>();
        var host = CreateRecordingWatchHost(invocations);
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cancellationSource.Token;
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, token);

        await WriteBrokerRequestAsync(clientSide,
            writer => BrokerProtocol.WriteStartWatch(writer, 1, "C:7:100:1,D:7:200:2"), token);
        var oldGeneration = await WaitForInvocationCountAsync(invocations, 2, token);

        // The new generation names neither old drive, and still no old drive survives it.
        await WriteBrokerRequestAsync(clientSide, writer => BrokerProtocol.WriteStartWatch(writer, 2, "E:7:300:3"), token);
        var all = await WaitForInvocationCountAsync(invocations, 3, token);
        Assert.AreEqual("E", all[2].Drive);
        await Task.WhenAll(oldGeneration.Select(invocation => invocation.Stopped.Task)).WaitAsync(token);

        var acknowledgement = await EndWatchGenerationAndReadAcknowledgementAsync(clientSide, 2, token);
        Assert.AreEqual(2U, acknowledgement.WatchGeneration);
        Assert.IsTrue(all[2].Stopped.Task.IsCompleted);

        await ShutdownAsync(clientSide, token);
        await serveTask;
    }

    static async Task<BrokerFrame> EndWatchGenerationAndReadAcknowledgementAsync(
        Stream stream, uint watchGeneration, CancellationToken cancellationToken)
    {
        await WriteBrokerRequestAsync(stream, writer => BrokerProtocol.WriteEndWatch(writer, watchGeneration),
            cancellationToken);
        return await ReadAcknowledgementAsync(stream, cancellationToken);
    }

    // Skips the live frames (a drive's CaughtUp marker) the watch writes ahead of the reply.
    static async Task<BrokerFrame> ReadAcknowledgementAsync(Stream stream, CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await ReadOneFrameAsync(stream).WaitAsync(cancellationToken);
            if (frame.Kind == BrokerFrameKind.EndWatchAck)
            {
                return frame;
            }
        }
    }
}
