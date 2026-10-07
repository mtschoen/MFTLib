using Microsoft.Extensions.Time.Testing;
using SampleProgram.Watch;

namespace MFTLib.Tests;

// The Watch twin of the Direct acknowledgement in SampleHostElevationNoticeTests; the Watch tests are Windows only.
internal static class WatchNoticeSupport
{
    /// <summary>Makes the dialog answer with an OK after a deliberate pause, so a Watch run never shows a real window.</summary>
    internal static void AcknowledgeDeliberately(SampleHost host)
    {
        var clock = new FakeTimeProvider();
        host._timeProvider = clock;
        host._getEnvironmentVariable = _ => null;
        host._messageBeep = _ => true;
        host._messageBox = (_, _, _, _) =>
        {
            clock.Advance(SampleHost.AccidentalDismissalInterval);
            return SampleHost.MessageBoxResultOk;
        };
    }
}
