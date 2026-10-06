using System.ComponentModel;
using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The session's launch, end and disposal rules. Every launch is a task completion source the test
///     completes, so nothing here waits for time to pass. No test launches a real broker: the public
///     constructors get a launcher that declines.
/// </summary>
// The declined-prompt tests replace the BrokerLauncher process seams, which are process-global.
[TestClass]
[DoNotParallelize]
[SupportedOSPlatform("windows")]
public class BrokerSessionTests
{
    static readonly TimeSpan HangGuard = HostChannelHarness.HangGuard;

    [TestCleanup]
    public void Cleanup()
    {
        BrokerLauncher.ResetToDefaults();
    }

    [TestMethod]
    public async Task ConnectAsync_LaunchesLazilyOnceAndSharesTheProcessAcrossConcurrentCalls()
    {
        await using var handle = StartBroker();
        var brokerProcess = handle.Process;
        var launches = 0;
        await using var session = CreateSession(_ =>
        {
            Interlocked.Increment(ref launches);
            return Task.FromResult(brokerProcess);
        });
        Assert.AreEqual(0, launches);
        Assert.IsFalse(session.HasEnded);

        var callers = new List<Task<BrokerProcess>>();
        for (var caller = 0; caller < 8; caller++)
        {
            callers.Add(session.ConnectAsync(CancellationToken.None));
        }

        var processes = await Task.WhenAll(callers).WaitAsync(HangGuard);

        Assert.IsTrue(processes.All(process => ReferenceEquals(process, brokerProcess)));
        Assert.AreEqual(1, launches);
        Assert.IsFalse(processes[0].Ended.IsCompleted);
        Assert.IsFalse(session.HasEnded);
    }

    [TestMethod]
    public async Task ConnectAsync_AlreadyCancelledCallerNeverLaunches()
    {
        var launches = 0;
        await using var session = CreateSession(_ =>
        {
            Interlocked.Increment(ref launches);
            return new TaskCompletionSource<BrokerProcess>().Task;
        });
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await WatchDeduplicationTestSupport.ThrowsAsync<OperationCanceledException>(() =>
            session.ConnectAsync(cancellation.Token));

        Assert.AreEqual(0, launches);
    }

    [TestMethod]
    public async Task ConnectAsync_CancelledCallerEndsOnlyItsOwnWaitAndALaterCallerSharesTheLaunch()
    {
        await using var handle = StartBroker();
        var brokerProcess = handle.Process;
        var launch = new LaunchGate();
        await using var session = CreateSession(launch.LaunchAsync);
        using var cancellation = new CancellationTokenSource();
        var cancelled = session.ConnectAsync(cancellation.Token);
        var patient = session.ConnectAsync(CancellationToken.None);
        await launch.Entered.WaitAsync(HangGuard);

        await cancellation.CancelAsync();
        await WatchDeduplicationTestSupport.ThrowsAsync<OperationCanceledException>(() => cancelled);
        Assert.IsFalse(launch.LaunchToken.IsCancellationRequested, "one caller's token must not reach the launch");
        Assert.IsFalse(patient.IsCompleted);

        launch.Complete(brokerProcess);
        var later = await session.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard);

        Assert.AreSame(brokerProcess, await patient.WaitAsync(HangGuard));
        Assert.AreSame(brokerProcess, later);
        Assert.AreEqual(1, launch.Count);
    }

    [TestMethod]
    public async Task ConnectAsync_FailedLaunchIsNotCachedAndTheNextUseLaunchesAgain()
    {
        await using var handle = StartBroker();
        var brokerProcess = handle.Process;
        var launches = 0;
        var connecting = 0;
        var connected = 0;
        await using var session = CreateSession(_ => Interlocked.Increment(ref launches) == 1
            ? Task.FromException<BrokerProcess>(new InvalidOperationException("the UAC prompt was declined"))
            : Task.FromResult(brokerProcess));
        session.Connecting += () => Interlocked.Increment(ref connecting);
        session.Connected += () => Interlocked.Increment(ref connected);

        var failure = await WatchDeduplicationTestSupport.ThrowsAsync<InvalidOperationException>(() =>
            session.ConnectAsync(CancellationToken.None));
        Assert.AreEqual("the UAC prompt was declined", failure.Message);
        Assert.IsFalse(session.HasEnded, "a declined prompt does not end the session");
        Assert.AreEqual(1, connecting);
        Assert.AreEqual(0, connected);

        var process = await session.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard);

        Assert.AreSame(brokerProcess, process);
        Assert.AreEqual(2, launches);
        Assert.AreEqual(2, connecting);
        Assert.AreEqual(1, connected);
    }

    [TestMethod]
    public async Task ConnectAsync_ConcurrentCallersShareOneFailedLaunch()
    {
        var launch = new LaunchGate();
        await using var session = CreateSession(launch.LaunchAsync);
        var first = session.ConnectAsync(CancellationToken.None);
        var second = session.ConnectAsync(CancellationToken.None);
        await launch.Entered.WaitAsync(HangGuard);

        launch.Fail(new IOException("launch failed"));

        await WatchDeduplicationTestSupport.ThrowsAsync<IOException>(() => first);
        await WatchDeduplicationTestSupport.ThrowsAsync<IOException>(() => second);
        Assert.AreEqual(1, launch.Count);
    }

    [TestMethod]
    public async Task Events_ArriveInOrderAroundTheLauncher()
    {
        await using var handle = StartBroker();
        var brokerProcess = handle.Process;
        var order = new List<string>();
        await using var session = CreateSession(_ =>
        {
            lock (order)
            {
                order.Add("launch");
            }

            return Task.FromResult(brokerProcess);
        });
        session.Connecting += () =>
        {
            lock (order)
            {
                order.Add("connecting");
            }
        };
        session.Connected += () =>
        {
            lock (order)
            {
                order.Add("connected");
            }
        };

        await session.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard);
        await session.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard);

        CollectionAssert.AreEqual(new[] { "connecting", "launch", "connected" }, order);
    }

    [TestMethod]
    public async Task Events_AreRaisedOutsideAnyGateTheSessionHolds()
    {
        await using var handle = StartBroker();
        var brokerProcess = handle.Process;
        var launch = new LaunchGate();
        var session = CreateSession(launch.LaunchAsync);
        var connectingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConnecting = new TaskCompletionSource();
        var releaseConnected = new TaskCompletionSource();
        // Each handler parks on the launching thread until the test has probed the session, so a handler
        // running under the gate would leave the probe waiting for it.
        session.Connecting += () =>
        {
            connectingEntered.TrySetResult();
            releaseConnecting.Task.Wait(HangGuard);
        };
        session.Connected += () =>
        {
            connectedEntered.TrySetResult();
            releaseConnected.Task.Wait(HangGuard);
        };
        var connecting = session.ConnectAsync(CancellationToken.None);

        await connectingEntered.Task.WaitAsync(HangGuard);
        await ReachEveryGatedMemberAsync(session);
        releaseConnecting.SetResult();
        await launch.Entered.WaitAsync(HangGuard);
        launch.Complete(brokerProcess);
        await connectedEntered.Task.WaitAsync(HangGuard);
        await ReachEveryGatedMemberAsync(session);
        releaseConnected.SetResult();

        Assert.AreSame(brokerProcess, await connecting.WaitAsync(HangGuard));
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task ConnectAsync_FromAUserInterfaceContextLaunchesOffThatThreadAndContext()
    {
        using var userInterface = new SingleThreadSynchronizationContext();
        var launcherThread = 0;
        SynchronizationContext? launcherContext = null;
        SynchronizationContext? connectingContext = null;
        var session = CreateSession(_ =>
        {
            launcherThread = Environment.CurrentManagedThreadId;
            launcherContext = SynchronizationContext.Current;
            return Task.FromException<BrokerProcess>(new InvalidOperationException("the UAC prompt was declined"));
        });
        session.Connecting += () => connectingContext = SynchronizationContext.Current;

        var connecting = await userInterface.RunAsync(() => session.ConnectAsync(CancellationToken.None))
            .WaitAsync(HangGuard);
        await WatchDeduplicationTestSupport.ThrowsAsync<InvalidOperationException>(() => connecting)
            .WaitAsync(HangGuard);
        await session.DisposeAsync();

        Assert.AreNotEqual(userInterface.ThreadId, launcherThread, "the blocking launcher ran on the caller's thread");
        Assert.AreNotSame(userInterface, launcherContext, "the launcher ran on the caller's synchronization context");
        Assert.AreNotSame(userInterface, connectingContext, "Connecting ran on the caller's synchronization context");
    }

    [TestMethod]
    public async Task Connecting_SubscriberThatStartsDisposalStopsTheLaunchBeforeTheLauncherRuns()
    {
        var launches = 0;
        Task? disposal = null;
        var session = CreateSession(_ =>
        {
            Interlocked.Increment(ref launches);
            return Task.FromException<BrokerProcess>(new InvalidOperationException("the UAC prompt was declined"));
        });
        session.Connecting += () => disposal = session.DisposeAsync().AsTask();

        var failure = await WatchDeduplicationTestSupport.ThrowsAsync<Exception>(() =>
            session.ConnectAsync(CancellationToken.None)).WaitAsync(HangGuard);
        await disposal!.WaitAsync(HangGuard);

        Assert.IsTrue(session.HasEnded);
        Assert.AreEqual(0, launches, "the launcher ran after a Connecting subscriber disposed the session");
        Assert.IsInstanceOfType<OperationCanceledException>(failure);
    }

    [TestMethod]
    public async Task Connecting_ThrowingSubscriberFailsThatAttemptWithoutLaunchingAndTheSessionStaysUsable()
    {
        await using var handle = StartBroker();
        var brokerProcess = handle.Process;
        var launches = 0;
        await using var session = CreateSession(_ =>
        {
            Interlocked.Increment(ref launches);
            return Task.FromResult(brokerProcess);
        });
        var subscriberCalls = 0;
        session.Connecting += () =>
        {
            if (Interlocked.Increment(ref subscriberCalls) == 1)
            {
                throw new FormatException("bad subscriber");
            }
        };

        await WatchDeduplicationTestSupport.ThrowsAsync<FormatException>(() =>
            session.ConnectAsync(CancellationToken.None));
        Assert.AreEqual(0, launches);

        Assert.AreSame(brokerProcess, await session.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard));
        Assert.AreEqual(1, launches);
    }

    [TestMethod]
    public async Task Connected_ThrowingSubscriberFailsThatAttemptButTheSessionKeepsTheLaunchedProcess()
    {
        await using var handle = StartBroker();
        var brokerProcess = handle.Process;
        var launches = 0;
        var later = 0;
        var session = CreateSession(_ =>
        {
            Interlocked.Increment(ref launches);
            return Task.FromResult(brokerProcess);
        });
        session.Connected += () => throw new FormatException("bad subscriber");
        session.Connected += () => Interlocked.Increment(ref later);

        await WatchDeduplicationTestSupport.ThrowsAsync<FormatException>(() =>
            session.ConnectAsync(CancellationToken.None));

        Assert.AreEqual(1, later, "a throwing subscriber must not hide the event from the others");
        Assert.AreSame(brokerProcess, await session.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard));
        Assert.AreEqual(1, launches);
        await session.DisposeAsync();
        Assert.IsTrue(brokerProcess.Ended.IsCompleted, "the process the failed attempt launched is still owned");
    }

    [TestMethod]
    public async Task ConnectAsync_AfterTheProcessEndedRefusesNamingTheReasonAndNeverRelaunches()
    {
        await using var handle = StartBroker();
        var brokerProcess = handle.Process;
        var launches = 0;
        await using var session = CreateSession(_ =>
        {
            Interlocked.Increment(ref launches);
            return Task.FromResult(brokerProcess);
        });
        var first = await session.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard);
        Assert.IsFalse(session.HasEnded);

        handle.Crash();
        var reason = await first.Ended.WaitAsync(HangGuard);

        Assert.IsTrue(session.HasEnded, "an end that has completed is visible before the observer runs");
        var refusal = await WatchDeduplicationTestSupport.ThrowsAsync<InvalidOperationException>(() =>
            session.ConnectAsync(CancellationToken.None));
        StringAssert.Contains(refusal.Message, reason);
        Assert.AreEqual(reason, await session.Ended.WaitAsync(HangGuard));
        Assert.AreEqual(1, launches);
    }

    [TestMethod]
    public async Task ConnectAsync_ProcessEndedBeforeTheLaunchReturnedRefusesAndEndsOnce()
    {
        await using var handle = StartBroker();
        var brokerProcess = handle.Process;
        var launches = 0;
        Action crash = handle.Crash;
        await using var session = CreateSession(async cancellationToken =>
        {
            Interlocked.Increment(ref launches);
            crash();
            await brokerProcess.Ended.WaitAsync(HangGuard, cancellationToken);
            return brokerProcess;
        });

        await WatchDeduplicationTestSupport.ThrowsAsync<InvalidOperationException>(() =>
            session.ConnectAsync(CancellationToken.None));
        await WatchDeduplicationTestSupport.ThrowsAsync<InvalidOperationException>(() =>
            session.ConnectAsync(CancellationToken.None));

        var expected = await brokerProcess.Ended.WaitAsync(HangGuard);
        Assert.AreEqual(expected, await session.Ended.WaitAsync(HangGuard));
        Assert.IsTrue(session.HasEnded);
        Assert.AreEqual(1, launches);
    }

    [TestMethod]
    public async Task Ended_KeepsTheProcessReasonAfterTheEndedSessionIsDisposed()
    {
        await using var handle = StartBroker();
        var brokerProcess = handle.Process;
        var session = CreateSession(_ => Task.FromResult(brokerProcess));
        var process = await session.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard);
        handle.Crash();
        var reason = await process.Ended.WaitAsync(HangGuard);

        await session.DisposeAsync();

        Assert.IsTrue(session.HasEnded);
        Assert.AreEqual(reason, await session.Ended.WaitAsync(HangGuard));
        await WatchDeduplicationTestSupport.ThrowsAsync<ObjectDisposedException>(() =>
            session.ConnectAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task Ended_ContinuationCallingBackIntoTheSessionCompletesWhenTheProcessEndsOnItsOwn()
    {
        await using var handle = StartBroker();
        var brokerProcess = handle.Process;
        await using var session = CreateSession(_ => Task.FromResult(brokerProcess));
        await session.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard);
        var callback = CallBackIntoSessionOnEndedAsync(session);

        handle.Crash();
        var (beforeDispose, afterDispose) = await callback.WaitAsync(HangGuard);

        Assert.AreEqual(typeof(InvalidOperationException), beforeDispose.GetType());
        StringAssert.Contains(beforeDispose.Message, await brokerProcess.Ended.WaitAsync(HangGuard));
        Assert.IsInstanceOfType<ObjectDisposedException>(afterDispose);
    }

    [TestMethod]
    public async Task Ended_ContinuationCallingBackIntoTheSessionCompletesWhenTheSessionIsDisposedFirst()
    {
        await using var handle = StartBroker();
        var brokerProcess = handle.Process;
        var session = CreateSession(_ => Task.FromResult(brokerProcess));
        await session.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard);
        var callback = CallBackIntoSessionOnEndedAsync(session);

        await session.DisposeAsync();
        var (beforeDispose, afterDispose) = await callback.WaitAsync(HangGuard);

        Assert.IsInstanceOfType<ObjectDisposedException>(beforeDispose);
        Assert.IsInstanceOfType<ObjectDisposedException>(afterDispose);
    }

    // A continuation on Ended that calls back into the session: it must complete, so it was not run
    // under the session's gate by the thread that completed Ended.
    static async Task<(InvalidOperationException BeforeDispose, InvalidOperationException AfterDispose)>
        CallBackIntoSessionOnEndedAsync(BrokerSession session)
    {
        await session.Ended.ConfigureAwait(false);
        Assert.IsTrue(session.HasEnded);
        var beforeDispose = await WatchDeduplicationTestSupport.ThrowsAsync<InvalidOperationException>(() =>
            session.ConnectAsync(CancellationToken.None)).ConfigureAwait(false);
        await session.DisposeAsync().ConfigureAwait(false);
        var afterDispose = await WatchDeduplicationTestSupport.ThrowsAsync<InvalidOperationException>(() =>
            session.ConnectAsync(CancellationToken.None)).ConfigureAwait(false);
        return (beforeDispose, afterDispose);
    }

    // Another thread reaches every member that takes the session's gate; it can only return while no
    // event handler is running under that gate.
    static Task ReachEveryGatedMemberAsync(BrokerSession session) =>
        Task.Run(() =>
        {
            _ = session.ConnectAsync(CancellationToken.None);
            _ = session.HasEnded;
            _ = session.Ended;
        }).WaitAsync(HangGuard);

    [TestMethod]
    public async Task DisposeAsync_DisposesTheProcessEndsTheSessionAndIsIdempotent()
    {
        await using var handle = StartBroker();
        var brokerProcess = handle.Process;
        var session = CreateSession(_ => Task.FromResult(brokerProcess));
        var process = await session.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard);
        Assert.IsFalse(session.Ended.IsCompleted);

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.IsTrue(process.Ended.IsCompleted);
        Assert.IsTrue(session.HasEnded);
        Assert.IsTrue(session.Ended.IsCompleted, "Ended completes at disposal when the process had not ended");
        await WatchDeduplicationTestSupport.ThrowsAsync<ObjectDisposedException>(() =>
            session.ConnectAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task DisposeAsync_BeforeFirstUseNeverLaunches()
    {
        var launches = 0;
        var session = CreateSession(_ =>
        {
            Interlocked.Increment(ref launches);
            return new TaskCompletionSource<BrokerProcess>().Task;
        });

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.AreEqual(0, launches);
        Assert.IsTrue(session.HasEnded);
        Assert.IsTrue(session.Ended.IsCompleted);
        await WatchDeduplicationTestSupport.ThrowsAsync<ObjectDisposedException>(() =>
            session.ConnectAsync(CancellationToken.None));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DisposeAsync_ReclaimsAProcessThatArrivesAfterDisposalBegan(bool callerCancelled)
    {
        await using var handle = StartBroker();
        var brokerProcess = handle.Process;
        var launch = new LaunchGate();
        var session = CreateSession(launch.LaunchAsync);
        using var cancellation = new CancellationTokenSource();
        var connecting = session.ConnectAsync(cancellation.Token);
        await launch.Entered.WaitAsync(HangGuard);
        if (callerCancelled)
        {
            await cancellation.CancelAsync();
        }

        var first = session.DisposeAsync().AsTask();
        var second = session.DisposeAsync().AsTask();
        await launch.StopRequested.WaitAsync(HangGuard);
        Assert.IsFalse(first.IsCompleted, "disposal waits for the in-flight launch so no process leaks");

        // The launcher returns despite cancellation, as one that already started a process does.
        launch.Complete(brokerProcess);

        await WatchDeduplicationTestSupport.ThrowsAsync<OperationCanceledException>(() => connecting);
        await Task.WhenAll(first, second).WaitAsync(HangGuard);
        Assert.IsTrue(brokerProcess.Ended.IsCompleted);
        Assert.IsTrue(session.HasEnded);
        Assert.AreEqual(1, launch.Count);
    }

    [TestMethod]
    public async Task DisposeAsync_WithAFailingInFlightLaunchCompletesWithoutThrowing()
    {
        var launch = new LaunchGate();
        var session = CreateSession(launch.LaunchAsync);
        var connecting = session.ConnectAsync(CancellationToken.None);
        await launch.Entered.WaitAsync(HangGuard);

        var disposal = session.DisposeAsync().AsTask();
        await launch.StopRequested.WaitAsync(HangGuard);
        launch.Fail(new IOException("launch failed"));

        await disposal.WaitAsync(HangGuard);
        await WatchDeduplicationTestSupport.ThrowsAsync<IOException>(() => connecting);
    }

    [TestMethod]
    public async Task DisposeAsync_RightAfterAUseOnAUserInterfaceContextNeverRunsTheLauncher()
    {
        using var userInterface = new SingleThreadSynchronizationContext();
        var launchScheduler = new QueuedTaskScheduler();
        var launches = 0;
        var connectingRaised = 0;
        var session = new BrokerSession(_ =>
        {
            Interlocked.Increment(ref launches);
            return Task.FromException<BrokerProcess>(new InvalidOperationException("the UAC prompt was declined"));
        }, launchScheduler);
        session.Connecting += () => Interlocked.Increment(ref connectingRaised);

        // A use immediately followed by disposal, both from the user interface thread, before the launch starts.
        var (use, disposal) = await userInterface.RunAsync(() =>
            (session.ConnectAsync(CancellationToken.None), session.DisposeAsync().AsTask())).WaitAsync(HangGuard);
        Assert.IsTrue(session.HasEnded);
        launchScheduler.RunPending();
        await disposal.WaitAsync(HangGuard);
        var failure = await WatchDeduplicationTestSupport.ThrowsAsync<Exception>(() => use).WaitAsync(HangGuard);

        Assert.AreEqual(0, launches, "the launcher ran after disposal ended the session");
        Assert.AreEqual(0, connectingRaised, "Connecting was raised after disposal ended the session");
        Assert.IsInstanceOfType<OperationCanceledException>(failure);
    }

    [TestMethod]
    public async Task StatusReads_DoNotWaitForAnInFlightLaunch()
    {
        await using var handle = StartBroker();
        var brokerProcess = handle.Process;
        var launch = new LaunchGate();
        await using var session = CreateSession(launch.LaunchAsync);
        var connecting = session.ConnectAsync(CancellationToken.None);
        await launch.Entered.WaitAsync(HangGuard);

        Assert.IsFalse(session.HasEnded);
        Assert.IsFalse(session.Ended.IsCompleted);
        Assert.IsFalse(connecting.IsCompleted);

        launch.Complete(brokerProcess);
        Assert.AreSame(brokerProcess, await connecting.WaitAsync(HangGuard));
    }

    [TestMethod]
    public async Task GrowUsnJournalAsync_LaunchesOnFirstUseAndForwardsTheRequest()
    {
        var received = new List<(string Drive, long MaximumSize, long AllocationDelta)>();
        await using var handle = StartBroker((drive, maximumSize, allocationDelta) =>
        {
            received.Add((drive, maximumSize, allocationDelta));
            return new UsnJournalSettings { MaximumSize = maximumSize, AllocationDelta = allocationDelta };
        });
        var brokerProcess = handle.Process;
        var launches = 0;
        await using var session = CreateSession(_ =>
        {
            Interlocked.Increment(ref launches);
            return Task.FromResult(brokerProcess);
        });

        var settings = await session.GrowUsnJournalAsync('c', 0x08000000, 0x01000000, CancellationToken.None)
            .WaitAsync(HangGuard);

        Assert.AreEqual(0x08000000L, settings.MaximumSize);
        Assert.AreEqual(0x01000000L, settings.AllocationDelta);
        Assert.AreEqual(1, launches);
        Assert.AreEqual(1, received.Count);
        Assert.AreEqual("C", received[0].Drive);
    }

    [TestMethod]
    public async Task GrowUsnJournalAsync_AfterTheProcessEndedThrowsInvalidOperation()
    {
        await using var handle = StartBroker();
        var brokerProcess = handle.Process;
        await using var session = CreateSession(_ => Task.FromResult(brokerProcess));
        var process = await session.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard);
        handle.Crash();
        await process.Ended.WaitAsync(HangGuard);

        await WatchDeduplicationTestSupport.ThrowsAsync<InvalidOperationException>(() =>
            session.GrowUsnJournalAsync('C', 0x08000000, 0x01000000, CancellationToken.None));
    }

    [TestMethod]
    public async Task CreateIndexSource_LaunchesNothingUntilAnIndexUsesIt()
    {
        var launches = 0;
        await using var session = CreateSession(_ =>
        {
            Interlocked.Increment(ref launches);
            return new TaskCompletionSource<BrokerProcess>().Task;
        });

        var plain = session.CreateIndexSource();
        var scanned = session.CreateIndexSource(new BrokerScanOptions { Profile = BrokerScanProfile.Full });

        Assert.IsNotNull(plain);
        Assert.IsNotNull(scanned);
        Assert.AreEqual(0, launches);
    }

    [TestMethod]
    public async Task CreateIndexSource_OpensAnIndexThroughTheSessionsProcess()
    {
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "mftlib-session-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var handle = BrokerTestHarness.StartInProcess(new ScriptedBrokerVolumes
            {
                QueryJournalCursor = _ => new SyntheticJournalCursor(7, 1000),
                ScanDrive = _ =>
                [
                    [
                        new SyntheticScanRecord
                        {
                            RecordNumber = 5, ParentRecordNumber = 5, FileName = ".", IsDirectory = true
                        }
                    ]
                ]
            });
            var brokerProcess = handle.Process;
            var launches = 0;
            await using var session = CreateSession(_ =>
            {
                Interlocked.Increment(ref launches);
                return Task.FromResult(brokerProcess);
            });

            await using var index = await FileIndex.OpenAsync(new FileIndexOptions
            {
                Drives = [new IndexedDrive('X', Path.GetTempPath(), 123)],
                CacheDirectory = cacheDirectory,
                NoCache = true,
                MftSource = session.CreateIndexSource()
            }, CancellationToken.None).WaitAsync(HangGuard);

            Assert.AreEqual(1, launches);
            Assert.AreEqual(DriveState.Ready, index.Drives.Single().State);
        }
        finally
        {
            if (Directory.Exists(cacheDirectory))
            {
                Directory.Delete(cacheDirectory, true);
            }
        }
    }

    [TestMethod]
    public async Task DefaultConstructor_RelaunchesAfterTheUacPromptIsDeclinedAndNeverCachesTheDecline()
    {
        var prompts = 0;
        BrokerLauncher._getProcessPathFunc = () => @"C:\app\MyApp.exe";
        BrokerLauncher._startProcess = _ =>
        {
            Interlocked.Increment(ref prompts);
            throw new Win32Exception(1223);
        };
        await using var session = new BrokerSession();

        var first = await WatchDeduplicationTestSupport.ThrowsAsync<InvalidOperationException>(() =>
            session.GrowUsnJournalAsync('C', 2, 1, CancellationToken.None));
        await WatchDeduplicationTestSupport.ThrowsAsync<InvalidOperationException>(() =>
            session.GrowUsnJournalAsync('C', 2, 1, CancellationToken.None));

        StringAssert.Contains(first.Message, "UAC prompt was declined");
        Assert.AreEqual(2, prompts);
        Assert.IsFalse(session.HasEnded);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LauncherConstructor_DeclinedLaunchRaisesConnectingAgainOnEveryAttempt(bool withTimeout)
    {
        var commandLines = new List<string>();
        var connecting = 0;
        await using var session = withTimeout
            ? new BrokerSession(Decline, TimeSpan.FromSeconds(5))
            : new BrokerSession(Decline);
        session.Connecting += () => Interlocked.Increment(ref connecting);

        await WatchDeduplicationTestSupport.ThrowsAsync<InvalidOperationException>(() =>
            session.GrowUsnJournalAsync('C', 2, 1, CancellationToken.None));
        await WatchDeduplicationTestSupport.ThrowsAsync<InvalidOperationException>(() =>
            session.GrowUsnJournalAsync('C', 2, 1, CancellationToken.None));

        Assert.AreEqual(2, commandLines.Count);
        StringAssert.StartsWith(commandLines[0], "--broker --pipe ");
        Assert.AreEqual(2, connecting);
        return;

        bool Decline(string brokerArguments)
        {
            commandLines.Add(brokerArguments);
            return false;
        }
    }

    [TestMethod]
    public async Task LauncherConstructor_PassesTheConnectTimeoutToTheLaunch()
    {
        await using var session = new BrokerSession(_ => true, TimeSpan.FromSeconds(-5));

        await WatchDeduplicationTestSupport.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            session.GrowUsnJournalAsync('C', 2, 1, CancellationToken.None));
    }

    [TestMethod]
    public void Constructors_RejectNullLaunchers()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new BrokerSession((Func<string, bool>)null!));
        Assert.ThrowsException<ArgumentNullException>(() =>
            BrokerTestHarness.CreateSession(null!));
    }

    static InProcessBrokerHandle StartBroker(Func<string, long, long, UsnJournalSettings>? grow = null) =>
        BrokerTestHarness.StartInProcess(new ScriptedBrokerVolumes
        {
            QueryJournalCursor = _ => throw new NotSupportedException("no scan in this test"),
            GrowUsnJournal = grow
        });

    static BrokerSession CreateSession(Func<CancellationToken, Task<BrokerProcess>> launchAsync) =>
        new(launchAsync);

    /// <summary>A launcher the test holds open, finishes or fails, and whose token it can inspect.</summary>
    sealed class LaunchGate
    {
        readonly TaskCompletionSource<BrokerProcess> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _count;
        CancellationToken _token;

        public int Count => Volatile.Read(ref _count);

        public Task Entered => _entered.Task;

        public Task StopRequested => _stopRequested.Task;

        public CancellationToken LaunchToken => _token;

        public Task<BrokerProcess> LaunchAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            _token = cancellationToken;
            cancellationToken.Register(() => _stopRequested.TrySetResult());
            _entered.TrySetResult();
            return _result.Task;
        }

        public void Complete(BrokerProcess process) => _result.TrySetResult(process);

        public void Fail(Exception exception) => _result.TrySetException(exception);
    }
}
