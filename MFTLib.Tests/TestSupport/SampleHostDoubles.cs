using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

/// <summary>Collects each written line, then hands it to <paramref name="onLine" />; safe for overlapping writers.</summary>
sealed class LineCollectingWriter(List<string> lines, Action<string>? onLine = null) : TextWriter
{
    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

    public override void WriteLine(string? value)
    {
        var line = value ?? string.Empty;
        lock (lines)
        {
            lines.Add(line);
        }

        onLine?.Invoke(line);
    }
}

/// <summary>A key reader that fails the test when read: the code under test must not wait for a key.</summary>
sealed class FailingReader(string message) : TextReader
{
    public override int Read() => throw new AssertFailedException(message);
}

/// <summary>
///     A fake clock that jumps ahead by a fixed amount once a timer is created and <paramref name="advanceAfter" />
///     (when given) has completed, so a delay ends without waiting on real time.
/// </summary>
sealed class AutoAdvanceClock(TimeSpan advanceBy, Task? advanceAfter = null) : FakeTimeProvider
{
    public TimeSpan TotalAdvanced { get; private set; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        if (advanceAfter is null)
        {
            AdvanceOnce();
        }
        else
        {
            advanceAfter.ContinueWith(_ => AdvanceOnce(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        return timer;
    }

    void AdvanceOnce()
    {
        TotalAdvanced += advanceBy;
        Advance(advanceBy);
    }
}
