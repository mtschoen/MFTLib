using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.Index.InlineContentIndexes;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="FileIndex.ReadInlineContentAsync" /> against a scripted reader: how entries map to record requests,
///     which answers the index gives without any I/O, exactly-once delivery, and how failures surface. The
///     records themselves are read in <see cref="InlineContentNativeTests" /> and
///     <see cref="InlineContentDumpIndexTests" />.
/// </summary>
[TestClass]
public sealed class FileIndexInlineContentTests
{
    sealed class Collector
    {
        readonly object _gate = new();
        public List<(int Index, InlineContentOutcome Outcome, byte[] Content)> Results { get; } = [];

        public InlineContentHandler Handler => result =>
        {
            lock (_gate)
            {
                Results.Add((result.Index, result.Outcome, result.Content.ToArray()));
            }
        };
    }

    [TestMethod]
    public async Task Read_MapsEntriesToRecordRequestsAndDecidesTheRestWithoutAnyRead()
    {
        var reader = new ScriptedInlineContentReader();
        await using var index = await OpenAsync(reader);
        var entries = new[] { Entry(index, 8), Entry(index, 6), Entry(index, 10), Entry(index, 7) };
        var collector = new Collector();

        var result = await index.ReadInlineContentAsync(entries, collector.Handler, CancellationToken.None);

        Assert.AreEqual(RootOperationOutcome.Succeeded, result.Outcome);
        Assert.IsNull(result.Failure);
        Assert.AreEqual(index.Status.Root, result.Root);
        var requests = Assert.ContainsSingle(reader.Calls);
        CollectionAssert.AreEqual(
            new[] { new InlineContentRequest(3, 7, 3), new InlineContentRequest(0, 8, 4) }, requests,
            "only the live files are read, ascending by record number, each with its own sequence number");
        Assert.AreEqual(DriveLetter, reader.Volumes.Single().Letter);
        var byPosition = collector.Results.OrderBy(entry => entry.Index).ToArray();
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, byPosition.Select(entry => entry.Index).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                InlineContentOutcome.Returned, InlineContentOutcome.NotInline, InlineContentOutcome.Superseded,
                InlineContentOutcome.Returned
            }, byPosition.Select(entry => entry.Outcome).ToArray());
        CollectionAssert.AreEqual(BitConverter.GetBytes(8L), byPosition[0].Content);
        Assert.AreEqual(0, byPosition[1].Content.Length, "a directory has no content");
        Assert.AreEqual(0, byPosition[2].Content.Length, "a deleted entry has no content");
        CollectionAssert.AreEqual(BitConverter.GetBytes(7L), byPosition[3].Content);
    }

    [TestMethod]
    public async Task Read_DuplicateEntries_AreAnsweredOncePerPosition()
    {
        var reader = new ScriptedInlineContentReader();
        await using var index = await OpenAsync(reader);
        var notes = Entry(index, 7);
        var collector = new Collector();

        var result = await index.ReadInlineContentAsync(
            [notes, Entry(index, 8), notes, notes, Entry(index, 6), Entry(index, 6)], collector.Handler,
            CancellationToken.None);

        Assert.AreEqual(RootOperationOutcome.Succeeded, result.Outcome);
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4, 5 },
            collector.Results.Select(entry => entry.Index).OrderBy(position => position).ToArray());
        Assert.AreEqual(4, Assert.ContainsSingle(reader.Calls).Length, "each duplicate is its own request");
        CollectionAssert.AreEqual(new long[] { 7, 7, 7, 8 },
            reader.Calls[0].Select(request => request.RecordNumber).ToArray());
    }

    [TestMethod]
    public async Task Read_EmptyList_SucceedsWithoutTouchingTheReader()
    {
        var reader = new ScriptedInlineContentReader();
        await using var index = await OpenAsync(reader);
        var collector = new Collector();

        var result = await index.ReadInlineContentAsync([], collector.Handler, CancellationToken.None);

        Assert.AreEqual(RootOperationOutcome.Succeeded, result.Outcome);
        Assert.IsEmpty(collector.Results);
        Assert.IsEmpty(reader.Calls);
    }

    [TestMethod]
    public async Task Read_OnlyEntriesTheIndexDecides_NeverOpensTheReader()
    {
        var reader = new ScriptedInlineContentReader();
        await using var index = await OpenAsync(reader);
        var collector = new Collector();

        var result = await index.ReadInlineContentAsync([Entry(index, 6), Entry(index, 10)], collector.Handler,
            CancellationToken.None);

        Assert.AreEqual(RootOperationOutcome.Succeeded, result.Outcome);
        Assert.HasCount(2, collector.Results);
        Assert.IsEmpty(reader.Calls, "a directory and a deleted file need no record read");
    }

    [TestMethod]
    public async Task Read_SourceThatCannotReadRecords_IsNotApplicableAndCallsNothing()
    {
        await using var index = await OpenAsync(reader: null);
        var collector = new Collector();

        var result = await index.ReadInlineContentAsync([Entry(index, 7)], collector.Handler, CancellationToken.None);

        Assert.AreEqual(RootOperationOutcome.NotApplicable, result.Outcome);
        Assert.IsNull(result.Failure);
        Assert.IsEmpty(collector.Results);
    }

    [TestMethod]
    public async Task Read_SourceThatCannotReadRecords_StillRejectsAnEntryOfAnotherIndex()
    {
        await using var index = await OpenAsync(reader: null);
        await using var other = await OpenAsync(new ScriptedInlineContentReader());

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => index.ReadInlineContentAsync([Entry(other, 7)], _ => { }, CancellationToken.None));
    }

    [TestMethod]
    public async Task Read_DirectoryWalkRoot_IsNotApplicable()
    {
        using var directory = new TempDirectory("mftlib-inline-walk");
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "a.txt"), "walked", TestContext.CancellationToken);
        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Root = RootKey.FromDirectory(directory.Path),
            Producer = ProducerSelection.WalkOnly,
            CacheMode = CacheMode.Disabled
        }, TestContext.CancellationToken);
        var entry = index.Search(new SearchQuery("a.txt"), TestContext.CancellationToken).Single();
        var collector = new Collector();

        var result = await index.ReadInlineContentAsync([entry], collector.Handler, TestContext.CancellationToken);

        Assert.AreEqual(RootOperationOutcome.NotApplicable, result.Outcome);
        await using var other = await OpenAsync(new ScriptedInlineContentReader());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            index.ReadInlineContentAsync([entry, Entry(other, 7)], collector.Handler, TestContext.CancellationToken));
        Assert.IsEmpty(collector.Results);
    }

    [TestMethod]
    public async Task Read_ArgumentsAreChecked()
    {
        await using var index = await OpenAsync(new ScriptedInlineContentReader());
        InlineContentHandler handler = _ => { };

        Assert.ThrowsExactly<ArgumentNullException>(
            () => index.ReadInlineContentAsync(null!, handler, CancellationToken.None));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => index.ReadInlineContentAsync([], null!, CancellationToken.None));
        var failure = Assert.ThrowsExactly<ArgumentException>(
            () => index.ReadInlineContentAsync([Entry(index, 7), default], handler, CancellationToken.None));
        Assert.Contains("entries[1]", failure.Message);
        Assert.AreEqual("entries", failure.ParamName);
    }

    [TestMethod]
    public async Task Read_EntryOfAnotherIndex_IsRejected()
    {
        await using var index = await OpenAsync(new ScriptedInlineContentReader());
        await using var other = await OpenAsync(new ScriptedInlineContentReader());
        var collector = new Collector();

        var failure = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => index.ReadInlineContentAsync([Entry(index, 7), Entry(other, 7)], collector.Handler,
                CancellationToken.None));

        Assert.Contains("entries[1]", failure.Message);
        Assert.IsEmpty(collector.Results, "nothing is delivered for a call that is refused");
    }

    [TestMethod]
    public async Task Read_EntryOfAnotherIndexThatWasDisposed_IsStillAnArgumentError()
    {
        await using var index = await OpenAsync(new ScriptedInlineContentReader());
        var other = await OpenAsync(new ScriptedInlineContentReader());
        var foreign = Entry(other, 7);
        await other.DisposeAsync();
        var collector = new Collector();

        var failure = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => index.ReadInlineContentAsync([Entry(index, 7), foreign], collector.Handler, CancellationToken.None));

        Assert.Contains("entries[1]", failure.Message);
        Assert.IsEmpty(collector.Results);
    }

    [TestMethod]
    public async Task Read_EntryFromABlockARescanReplaced_IsRejected()
    {
        await using var index = await OpenAsync(new ScriptedInlineContentReader());
        var stale = Entry(index, 7);
        await index.RescanAsync(CancellationToken.None);

        var failure = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => index.ReadInlineContentAsync([stale], _ => { }, CancellationToken.None));

        Assert.Contains("rescan replaced", failure.Message);
    }

    [TestMethod]
    public async Task Read_RescanWhileTheRecordsAreBeingRead_StillAnswersEveryEntryOfTheCall()
    {
        var reached = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var reader = new ScriptedInlineContentReader
        {
            Before = async token =>
            {
                reached.SetResult();
                await release.Task.WaitAsync(token);
            }
        };
        await using var index = await OpenAsync(reader);
        var collector = new Collector();
        var read = index.ReadInlineContentAsync([Entry(index, 7), Entry(index, 8), Entry(index, 6)], collector.Handler,
            CancellationToken.None);
        await reached.Task.WaitAsync(TestContext.CancellationToken);

        var rescan = await index.RescanAsync(CancellationToken.None);
        release.SetResult();
        var result = await read;

        Assert.AreEqual(RootOperationOutcome.Succeeded, rescan.Outcome);
        Assert.AreEqual(RootOperationOutcome.Succeeded, result.Outcome, "the plan was made before the rescan and holds no block");
        CollectionAssert.AreEqual(new[] { 0, 1, 2 },
            collector.Results.Select(entry => entry.Index).OrderBy(position => position).ToArray());
    }

    [TestMethod]
    public async Task Read_HandlerThatThrows_PropagatesItsOwnExceptionAndStopsDelivery()
    {
        await using var index = await OpenAsync(new ScriptedInlineContentReader());
        var entries = new[] { Entry(index, 7), Entry(index, 8), Entry(index, 9) };
        var delivered = 0;
        var thrown = new InvalidOperationException("handler failed");

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => index.ReadInlineContentAsync(entries, _ =>
            {
                delivered++;
                throw thrown;
            }, CancellationToken.None));

        Assert.AreSame(thrown, failure);
        Assert.AreEqual(1, delivered);
    }

    [TestMethod]
    public async Task Read_HandlerThatCancelsAndThrows_PropagatesItsOwnException()
    {
        await using var index = await OpenAsync(new ScriptedInlineContentReader());
        using var cancellation = new CancellationTokenSource();
        var thrown = new InvalidOperationException("handler failed after cancelling");

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => index.ReadInlineContentAsync([Entry(index, 7)], _ =>
            {
                // ReSharper disable once AccessToDisposedClosure
                cancellation.Cancel();
                throw thrown;
            }, cancellation.Token));

        Assert.AreSame(thrown, failure, "a handler exception takes precedence over the cancellation it requested");
    }

    [TestMethod]
    public async Task Read_HandlerThatThrowsOnADecidedEntry_PropagatesToo()
    {
        await using var index = await OpenAsync(new ScriptedInlineContentReader());

        var failure = await Assert.ThrowsExactlyAsync<NotSupportedException>(
            () => index.ReadInlineContentAsync([Entry(index, 6)], _ => throw new NotSupportedException(),
                CancellationToken.None));

        Assert.IsNotNull(failure);
    }

    [TestMethod]
    public async Task Read_ReaderThatFailsBeforeAnswering_IsFailedWithNoAnswerDelivered()
    {
        var failure = new IOException("The volume cannot be read.");
        var reader = new ScriptedInlineContentReader { Before = _ => Task.FromException(failure) };
        await using var index = await OpenAsync(reader);
        var collector = new Collector();

        var result = await index.ReadInlineContentAsync([Entry(index, 7), Entry(index, 6)], collector.Handler,
            CancellationToken.None);

        Assert.AreEqual(RootOperationOutcome.Failed, result.Outcome);
        Assert.AreSame(failure, result.Failure);
        Assert.AreEqual(index.Status.Root, result.Root);
        Assert.IsEmpty(collector.Results, "the directory decided without I/O waits for the reader's first answer");
    }

    [TestMethod]
    public async Task Read_CancelledBeforeTheCall_ThrowsBeforeAnyWork()
    {
        var reader = new ScriptedInlineContentReader();
        await using var index = await OpenAsync(reader);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => index.ReadInlineContentAsync([Entry(index, 7)], _ => { }, cancellation.Token));

        Assert.IsEmpty(reader.Calls);
    }

    [TestMethod]
    public async Task Read_CancelledDuringTheRead_ThrowsOperationCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        var reached = new TaskCompletionSource();
        var reader = new ScriptedInlineContentReader
        {
            Before = async token =>
            {
                reached.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        await using var index = await OpenAsync(reader);

        var read = index.ReadInlineContentAsync([Entry(index, 7)], _ => { }, cancellation.Token);
        await reached.Task.WaitAsync(TestContext.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => read);
    }

    [TestMethod]
    public async Task Read_IndexDisposedDuringTheRead_CancelsIt()
    {
        var reached = new TaskCompletionSource();
        var reader = new ScriptedInlineContentReader
        {
            Before = async token =>
            {
                reached.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        var index = await OpenAsync(reader);
        var read = index.ReadInlineContentAsync([Entry(index, 7)], _ => { }, CancellationToken.None);
        await reached.Task.WaitAsync(TestContext.CancellationToken);

        await index.DisposeAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => read);
    }

    [TestMethod]
    public async Task Read_AfterDisposal_ThrowsObjectDisposed()
    {
        var index = await OpenAsync(new ScriptedInlineContentReader());
        var entry = Entry(index, 7);
        await index.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => index.ReadInlineContentAsync([entry], _ => { }, CancellationToken.None));
    }

    [TestMethod]
    public async Task Read_HandlerRunsOffTheCallersStackAndOneAnswerAtATime()
    {
        var reader = new ScriptedInlineContentReader();
        await using var index = await OpenAsync(reader);
        var callReturned = new TaskCompletionSource();
        var inHandler = 0;
        var overlapped = false;
        var handlerWaitedForTheCaller = true;

        var read = index.ReadInlineContentAsync(
            [Entry(index, 7), Entry(index, 8), Entry(index, 9), Entry(index, 6)], _ =>
            {
                overlapped |= Interlocked.Increment(ref inHandler) > 1;
                // A handler run on the caller's stack would wait here for a call that cannot return.
                handlerWaitedForTheCaller &= callReturned.Task.Wait(TimeSpan.FromSeconds(30));
                Thread.Yield();
                Interlocked.Decrement(ref inHandler);
            }, CancellationToken.None);
        callReturned.SetResult();
        await read;

        Assert.IsFalse(overlapped, "the handler is never entered twice at once");
        Assert.IsTrue(handlerWaitedForTheCaller, "the handler does not run on the stack of the call that started the read");
    }

    public TestContext TestContext { get; set; } = null!;
}
