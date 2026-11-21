using System.Runtime.ExceptionServices;

namespace MFTLib.Index;

/// <summary>
///     Hands answers to the consumer's <see cref="InlineContentHandler" /> and remembers the exception it throws, so a
///     fault of the handler can be told from a fault of the volume, and propagates unchanged.
/// </summary>
sealed class InlineContentDelivery(InlineContentHandler handler)
{
    /// <summary>How many answers are delivered between checks of the cancellation token.</summary>
    const int CancellationCheckInterval = 4096;

    ExceptionDispatchInfo? _handlerFailure;

    /// <summary>Delivers the answers the index decided without I/O.</summary>
    /// <exception cref="OperationCanceledException">The token was cancelled between answers.</exception>
    internal void DeliverDecided((int Position, InlineContentOutcome Outcome)[] decided,
        CancellationToken cancellationToken)
    {
        var delivered = 0;
        foreach (var (position, outcome) in decided)
        {
            if (delivered++ % CancellationCheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            Deliver(position, outcome, ReadOnlySpan<byte>.Empty);
        }
    }

    /// <summary>
    ///     The sink a reader answers into, which first delivers <paramref name="decided" /> when the reader hands over
    ///     its first answer: a reader opens and checks its source before answering, so a source refused before the first
    ///     answer delivers nothing (a broker reader's later chunk can still be refused after earlier answers), while a
    ///     failed record read is itself an answer (<see cref="InlineContentOutcome.Unreadable" />).
    /// </summary>
    internal InlineContentSink SinkAfterDecided((int Position, InlineContentOutcome Outcome)[] decided,
        CancellationToken cancellationToken)
    {
        var pending = decided;
        return (position, outcome, content) =>
        {
            if (pending is { } waiting)
            {
                pending = null;
                DeliverDecided(waiting, cancellationToken);
            }

            Deliver(position, outcome, content);
        };
    }

    /// <summary>Throws the handler's own exception, with its original stack, when the handler threw one.</summary>
    internal void RethrowHandlerFailure() => _handlerFailure?.Throw();

    void Deliver(int position, InlineContentOutcome outcome, ReadOnlySpan<byte> content)
    {
        try
        {
            handler(new InlineContentResult(position, outcome, content));
        }
        catch (Exception failure)
        {
            _handlerFailure = ExceptionDispatchInfo.Capture(failure);
            throw;
        }
    }
}
