using Microsoft.Extensions.Time.Testing;
using SampleProgram.Direct;

namespace MFTLib.Tests.TestSupport;

// The Direct twin of WatchNoticeSupport.
static class DirectNoticeSupport
{
    /// <summary>
    ///     Makes the dialog answer with an OK after a deliberate pause, so a scanner that reaches it neither shows
    ///     a real window nor treats the answer as accidental.
    /// </summary>
    internal static void AcknowledgeDeliberately(SampleHost scanner)
    {
        var clock = (FakeTimeProvider)scanner._timeProvider;
        scanner._messageBeep = _ => true;
        scanner._messageBox = (_, _, _, _) =>
        {
            clock.Advance(SampleHost.AccidentalDismissalInterval);
            return SampleHost.MessageBoxResultOk;
        };
    }
}
