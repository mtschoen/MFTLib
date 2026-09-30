using System.Collections.Concurrent;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class BrokerProcessTests
{
    [DataTestMethod]
    [DataRow("dispose", "The broker process was disposed.")]
    [DataRow("eof", "The broker closed its control pipe.")]
    [DataRow("unroutable", "The broker sent CaughtUp on the control pipe.")]
    public async Task Ended_ThrowingSubscriber_DoesNotBreakTeardownOrLaterDelivery(
        string ending, string expectedReason)
    {
        await BrokerDiagnostics.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
        BrokerDiagnostics.ResetToDefaults();
        var lines = new ConcurrentQueue<string>();
        BrokerDiagnostics.ReplaceWriterForTest(new BrokerDiagnosticsWriter(lines.Enqueue));
        BrokerDiagnostics.Enable("client");
        try
        {
            await using var broker = new ScriptedBroker();
            var firstEntered = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var notifications = new List<string>();
            var throwingCalls = 0;
            var secondThrowingCalls = 0;
            var endedAtNotification = false;
            var pendingAtNotification = -1;
            var process = broker.Process;
            process.Ended += reason =>
            {
                throwingCalls++;
                endedAtNotification = process.HasEnded;
                pendingAtNotification = process.PendingRequestCountForTest;
                firstEntered.TrySetResult(reason);
                throw new InvalidOperationException("ended subscriber regression");
            };
            process.Ended += _ =>
            {
                secondThrowingCalls++;
                throw new InvalidOperationException("second ended subscriber regression");
            };
            process.Ended += notifications.Add;

            var first = broker.Process.QueryVolumeAsync('C', CancellationToken.None);
            Assert.AreEqual(BrokerFrameKind.QueryVolume, (await broker.ReadRequestAsync()).Kind);
            var second = broker.Process.QueryVolumeAsync('D', CancellationToken.None);
            Assert.AreEqual(BrokerFrameKind.QueryVolume, (await broker.ReadRequestAsync()).Kind);

            if (ending == "eof")
            {
                await broker.CloseControlAsync();
            }
            else if (ending == "unroutable")
            {
                await broker.WriteControlAsync(BrokerProtocol.WriteCaughtUp);
            }

            if (ending != "dispose")
            {
                Assert.AreEqual(expectedReason, await firstEntered.Task.WaitAsync(HangGuard));
            }

            await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard);
            await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard);

            Assert.AreEqual(expectedReason, await firstEntered.Task.WaitAsync(HangGuard));
            Assert.IsTrue(broker.Process.HasEnded);
            Assert.IsTrue(endedAtNotification);
            Assert.AreEqual(0, pendingAtNotification);
            Assert.AreEqual(1, throwingCalls);
            Assert.AreEqual(1, secondThrowingCalls);
            CollectionAssert.AreEqual(new[] { expectedReason }, notifications);
            foreach (var request in new Task[] { first, second })
            {
                var lost = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(
                    () => request.WaitAsync(HangGuard));
                Assert.IsNull(lost.DriveLetter);
                Assert.AreEqual(expectedReason, lost.Message);
            }

            var rejected = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(
                () => broker.Process.QueryVolumeAsync('E', CancellationToken.None).WaitAsync(HangGuard));
            Assert.IsNull(rejected.DriveLetter);
            Assert.AreEqual(expectedReason, rejected.Message);

            await BrokerDiagnostics.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
            var failures = lines.Where(line => line.Contains("Ended handler notification failed:",
                StringComparison.Ordinal)).ToArray();
            Assert.AreEqual(2, failures.Length);
            StringAssert.Contains(failures[0], ":control]");
            StringAssert.Contains(failures[0], "System.InvalidOperationException");
            StringAssert.Contains(failures[0], "ended subscriber regression");
            StringAssert.Contains(failures[1], ":control]");
            StringAssert.Contains(failures[1], "System.InvalidOperationException");
            StringAssert.Contains(failures[1], "second ended subscriber regression");
        }
        finally
        {
            try
            {
                await BrokerDiagnostics.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
            }
            finally
            {
                BrokerDiagnostics.ResetToDefaults();
            }
        }
    }
}
