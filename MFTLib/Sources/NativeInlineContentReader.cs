using System.Runtime.InteropServices;
using MFTLib.Index;
using MFTLib.NativeInterop;

namespace MFTLib;

/// <summary>
///     A native reader of specific MFT records, opened on a volume handle or an opened dump. It borrows what it
///     was opened on, so the owner disposes it first. One thread uses it at a time.
/// </summary>
sealed unsafe class NativeInlineContentReader : IDisposable
{
    /// <summary>The bytes of content one native call can place; at least the native minimum.</summary>
    internal const int ContentBufferBytes = 4 * 1024 * 1024;

    /// <summary>The requests handed to the native reader at once, which bounds how far it coalesces.</summary>
    internal const int BatchRequests = 65536;

    /// <summary>Answers delivered between two looks at the cancellation token.</summary>
    const int CancellationCheckInterval = 4096;

    readonly MftInlineReaderHandle _handle;

    NativeInlineContentReader(MftInlineReaderHandle handle, MftInlineReaderInfo info)
    {
        _handle = handle;
        TotalRecords = info.TotalRecords;
    }

    /// <summary>How many records the MFT holds; a record at or past this number is superseded.</summary>
    internal ulong TotalRecords { get; }

    /// <summary>The reads of MFT bytes the native reader has issued so far, summed over this reader's native calls.</summary>
    internal long NativeReadCalls { get; private set; }

    /// <summary>The MFT bytes those reads covered, which exceeds the bytes of the records asked for by the coalescing gaps.</summary>
    internal long NativeBytesRead { get; private set; }

    /// <summary>Opens a reader on an NTFS volume or image handle, which must stay open until this is disposed.</summary>
    /// <exception cref="IOException">The handle is not a readable NTFS volume; the message says why.</exception>
    internal static NativeInlineContentReader Open(SafeHandle volumeHandle)
    {
        MFTLibNative.EnsureCompatibleNativeAbi();
        return Finish(MFTLibNative.OpenMftInlineReader(volumeHandle, out var info), info);
    }

    /// <summary>Opens a reader on an opened dump, which must stay open until this is disposed.</summary>
    /// <exception cref="IOException">The native open failed; the message says why.</exception>
    internal static NativeInlineContentReader Open(MftDumpInputHandle dumpInput)
    {
        MFTLibNative.EnsureCompatibleNativeAbi();
        return Finish(MFTLibNative.OpenMftDumpInlineReader(dumpInput, out var info), info);
    }

    static NativeInlineContentReader Finish(MftInlineReaderHandle handle, MftInlineReaderInfo info)
    {
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException(info.ErrorMessage);
        }

        return new NativeInlineContentReader(handle, info);
    }

    /// <summary>
    ///     Answers every request exactly once through <paramref name="sink" />, serially on the calling thread.
    ///     The requests go to the native reader in batches of <see cref="BatchRequests" /> sharing one content
    ///     buffer. A native call makes at most one group read (plus a single-record reread of each record a short or
    ///     failed group read did not deliver), so a batch is answered across one call per group (and more when content
    ///     fills the buffer), and the answers of each completed call reach the sink before the next group read starts.
    /// </summary>
    /// <param name="requests">The requests, ascending by record number.</param>
    /// <param name="sink">Receives each answer; an exception it throws propagates and stops the read.</param>
    /// <param name="cancellationToken">Stops the read between native reads and, through the control block, inside one.</param>
    /// <exception cref="OperationCanceledException">The token stopped the read.</exception>
    /// <exception cref="InvalidOperationException">The native read failed or answered inconsistently.</exception>
    internal void Read(ReadOnlySpan<InlineContentRequest> requests, InlineContentSink sink,
        CancellationToken cancellationToken) => Read(requests, MftInlineKind.Content, sink, cancellationToken);

    /// <summary>
    ///     Answers every request exactly once, as <see cref="Read(ReadOnlySpan{InlineContentRequest}, InlineContentSink, CancellationToken)" />
    ///     does, with the name and parent a scan gives each file instead of its content: a returned answer is the
    ///     8-byte parent file reference followed by the name's UTF-16 units.
    /// </summary>
    internal void ReadFileNames(ReadOnlySpan<InlineContentRequest> requests, InlineContentSink sink,
        CancellationToken cancellationToken) => Read(requests, MftInlineKind.FileName, sink, cancellationToken);

    void Read(ReadOnlySpan<InlineContentRequest> requests, MftInlineKind kind, InlineContentSink sink,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        var control = (IntPtr)NativeMemory.AllocZeroed((nuint)sizeof(MftParseControl));
        try
        {
            using var registration = cancellationToken.Register(() => RequestCancel(control));
            var state = new ReadState(Math.Min(requests.Length, BatchRequests), kind, control, sink, cancellationToken);
            for (var start = 0; start < requests.Length; start += BatchRequests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadBatch(requests.Slice(start, Math.Min(BatchRequests, requests.Length - start)), state);
            }
        }
        finally
        {
            NativeMemory.Free((void*)control);
        }
    }

    /// <summary>What one <see cref="Read" /> reuses across its batches and native calls.</summary>
    sealed class ReadState(int batchLength, MftInlineKind kind, IntPtr control, InlineContentSink sink,
        CancellationToken cancellationToken)
    {
        internal MftInlineKind Kind { get; } = kind;

        internal byte[] Buffer { get; } = GC.AllocateUninitializedArray<byte>(ContentBufferBytes, pinned: true);

        internal MftInlineRequest[] Batch { get; } =
            GC.AllocateUninitializedArray<MftInlineRequest>(batchLength, pinned: true);

        internal IntPtr Control { get; } = control;

        internal InlineContentSink Sink { get; } = sink;

        internal CancellationToken CancellationToken { get; } = cancellationToken;
    }

    void ReadBatch(ReadOnlySpan<InlineContentRequest> slice, ReadState state)
    {
        var filled = 0;
        foreach (var request in slice)
        {
            state.Batch[filled++] = new MftInlineRequest
            {
                RecordNumber = checked((ulong)request.RecordNumber),
                ExpectedSequenceNumber = request.SequenceNumber,
                Kind = state.Kind
            };
        }

        fixed (MftInlineRequest* requestAddress = state.Batch)
        fixed (byte* bufferAddress = state.Buffer)
        {
            var call = new MftInlineReadCall
            {
                Requests = (IntPtr)requestAddress,
                RequestCount = (ulong)slice.Length,
                Buffer = (IntPtr)bufferAddress,
                BufferLength = (ulong)state.Buffer.Length,
                Control = state.Control
            };
            var answered = 0;
            while (answered < slice.Length)
            {
                call.StartIndex = (ulong)answered;
                MFTLibNative.Hooks._readMftInlineContent(_handle, ref call, out var result);
                NativeReadCalls += (long)result.ReadCalls;
                NativeBytesRead += (long)result.BytesRead;
                answered = Deliver(slice, state, answered, result);
            }
        }
    }

    // Hands the answers a native call produced to the sink and returns the first position still unanswered.
    static int Deliver(ReadOnlySpan<InlineContentRequest> slice, ReadState state, int answered,
        MftInlineReadResult result)
    {
        if (result.Failed != 0)
        {
            throw new InvalidOperationException(result.ErrorMessage);
        }

        if (result.Cancelled != 0)
        {
            throw new OperationCanceledException(state.CancellationToken);
        }

        if (result.NextRequest <= (ulong)answered || result.NextRequest > (ulong)slice.Length ||
            result.BytesUsed > (ulong)state.Buffer.Length)
        {
            throw new InvalidOperationException("The native inline content read made no consistent progress.");
        }

        var next = (int)result.NextRequest;
        for (var index = answered; index < next; index++)
        {
            if ((index - answered) % CancellationCheckInterval == 0)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
            }

            var answer = state.Batch[index];
            var outcome = Outcome(answer.Outcome);
            state.Sink(slice[index].Position, outcome, ContentOf(answer, outcome, result.BytesUsed, state.Buffer));
        }

        return next;
    }

    static InlineContentOutcome Outcome(uint native)
    {
        var outcome = (InlineContentOutcome)native;
        return Enum.IsDefined(outcome)
            ? outcome
            : throw new InvalidOperationException($"The native inline content read returned outcome {native}.");
    }

    static ReadOnlySpan<byte> ContentOf(MftInlineRequest answer, InlineContentOutcome outcome, ulong bytesUsed,
        byte[] buffer)
    {
        if (outcome != InlineContentOutcome.Returned)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        if (answer.ContentOffset > bytesUsed || answer.ContentLength > bytesUsed - answer.ContentOffset)
        {
            throw new InvalidOperationException("The native inline content lies outside its buffer.");
        }

        return buffer.AsSpan((int)answer.ContentOffset, (int)answer.ContentLength);
    }

    static void RequestCancel(IntPtr control)
    {
        Volatile.Write(ref ((MftParseControl*)control)->CancelRequested, 1);
    }

    public void Dispose() => _handle.Dispose();
}
