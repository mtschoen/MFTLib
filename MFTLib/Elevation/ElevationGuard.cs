using System.Diagnostics;

namespace MFTLib;

/// <summary>
///     Thrown instead of starting a process that would raise a UAC prompt, in a process that opted into
///     elevation isolation.
/// </summary>
internal sealed class ElevationForbiddenException : InvalidOperationException
{
    public ElevationForbiddenException()
    {
    }

    public ElevationForbiddenException(string message) : base(message)
    {
    }

    public ElevationForbiddenException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
///     The real process start behind the two <c>runas</c> launch sites. A test process that opts in refuses it,
///     so an unstubbed elevation fails loudly instead of putting a UAC prompt on the desktop. A test that installs
///     its own start delegate never reaches this class.
/// </summary>
internal static class ElevationGuard
{
    static int _forbidden;

    internal static void Forbid()
    {
        Interlocked.Exchange(ref _forbidden, 1);
    }

    internal static Process? Start(ProcessStartInfo startInfo)
    {
        return StartUnlessForbidden(Volatile.Read(ref _forbidden) == 1, Process.Start, startInfo);
    }

    internal static Process? StartUnlessForbidden(
        bool forbidden, Func<ProcessStartInfo, Process?> start, ProcessStartInfo startInfo)
    {
        if (!forbidden)
        {
            return start(startInfo);
        }

        var arguments = startInfo.ArgumentList.Count > 0
            ? string.Join(' ', startInfo.ArgumentList)
            : startInfo.Arguments;
        throw new ElevationForbiddenException(
            $"Elevation is forbidden in this test process; refusing to start '{startInfo.FileName}' with verb "
            + $"'{startInfo.Verb}' and arguments '{arguments}'. Stub the start delegate in the test.");
    }
}
