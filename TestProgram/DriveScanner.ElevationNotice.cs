using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace TestProgram;

// The heads-up dialog shown before this run raises a Windows UAC prompt: the elevated relaunch, or the broker
// launch of an attended, unelevated scan-drive run. The prompt that follows is expected and a stray key press
// cannot dismiss it unseen. It lives here, not in MFTLib: a consumer's runtime flow must never gain a dialog.
// The two native seams are the bare user32 imports, so every flag, text and decision below runs in tests and
// the only untested lines are the import declarations.
partial class DriveScanner
{
    internal const string ElevationNoticeTitle = "TestProgram: administrator approval needed next";

    // The one sentence that differs between the two UAC prompts this dialog precedes.
    const string SelfElevationReason = "TestProgram relaunches itself elevated.";
    const string BrokerLaunchReason = "TestProgram launches its broker elevated.";
    internal const uint MessageBoxOkCancel = 0x00000001;
    internal const uint MessageBoxIconInformation = 0x00000040;
    internal const uint MessageBoxSystemModal = 0x00001000;
    internal const uint MessageBoxSetForeground = 0x00010000;
    internal const uint MessageBoxTopMost = 0x00040000;
    internal const uint MessageBeepIconExclamation = 0x00000030;
    internal const int MessageBoxResultOk = 1;
    internal const int MessageBoxResultCancel = 2;

    /// <summary>
    ///     A dismissal sooner than this after the dialog appeared is treated as an accidental key press and the
    ///     dialog is shown again.
    /// </summary>
    internal static readonly TimeSpan AccidentalDismissalInterval = TimeSpan.FromMilliseconds(750);

    /// <summary>
    ///     How long the dialog waits for an answer. Nobody at the desk must not hang a run forever, so an
    ///     unanswered dialog counts as Cancel: no UAC prompt, the manual-elevation fallback, exit code 1.
    /// </summary>
    internal static readonly TimeSpan ElevationNoticeTimeout = TimeSpan.FromMinutes(5);

    internal Func<IntPtr, string, string, uint, int> _messageBox = MessageBoxNative;
    internal Func<uint, bool> _messageBeep = MessageBeepNative;
    internal TimeProvider _timeProvider = TimeProvider.System;
    internal TaskScheduler _dialogScheduler = TaskScheduler.Default;

    /// <summary>
    ///     Everything the dialog thread observed, captured on that thread: the button and the clock immediately
    ///     before the message box call and immediately after it returned. The caller decides from this record only,
    ///     never from its own clock reads or from which task finished first.
    /// </summary>
    readonly record struct ElevationNoticeOutcome(int Button, DateTimeOffset ShownAt, DateTimeOffset AnsweredAt);

    /// <summary>
    ///     Shows the heads-up dialog until the person answers it deliberately. Returns true for OK, false for
    ///     Cancel, for a dialog that went unanswered too long, or for a dialog the system could not show.
    /// </summary>
    bool ConfirmElevation(string[] arguments, string reason)
    {
        var previousDismissalTooFast = false;
        while (true)
        {
            _messageBeep(MessageBeepIconExclamation);
            var outcome = ShowElevationNotice(BuildElevationNotice(arguments, reason, previousDismissalTooFast));
            if (outcome is not { } answered || answered.AnsweredAt - answered.ShownAt >= ElevationNoticeTimeout)
            {
                _writeLine("The heads-up dialog was not answered in time; treating it as Cancel.");
                return false;
            }

            var result = answered.Button;
            var onScreen = answered.AnsweredAt - answered.ShownAt;

            // A result that is neither button means the dialog failed, so asking again would never end.
            if (result != MessageBoxResultOk && result != MessageBoxResultCancel)
            {
                return false;
            }

            if (onScreen >= AccidentalDismissalInterval)
            {
                return result == MessageBoxResultOk;
            }

            previousDismissalTooFast = true;
        }
    }

    // MessageBoxW has no supported timeout, so the dialog runs on its own thread and the wait is bounded here.
    // The wait only stops the caller from blocking on a dialog nobody answers: when it ends with the dialog still
    // open there is no outcome (null, a timeout). A dialog that completed is judged by its own record, whichever
    // task the race happened to report first. An abandoned dialog dies with the process, which exits straight after
    // the Cancel path.
    ElevationNoticeOutcome? ShowElevationNotice(string text)
    {
        var started = new TaskCompletionSource<Task>();
        var dialog = Task.Factory.StartNew(
            () =>
            {
                var shownAt = _timeProvider.GetUtcNow();
                started.SetResult(Task.Delay(ElevationNoticeTimeout, _timeProvider));
                var result = _messageBox(
                    IntPtr.Zero,
                    text,
                    ElevationNoticeTitle,
                    MessageBoxOkCancel | MessageBoxIconInformation | MessageBoxSystemModal
                    | MessageBoxSetForeground | MessageBoxTopMost);
                return new ElevationNoticeOutcome(result, shownAt, _timeProvider.GetUtcNow());
            },
            CancellationToken.None,
            TaskCreationOptions.None,
            _dialogScheduler);

        // The console entry point has no synchronization context, so blocking here cannot deadlock, and the task
        // sets the result as its first action.
        var timeout = started.Task.GetAwaiter().GetResult();
        // Either task ending releases the wait, so it lasts at most the timeout.
        Task.WhenAny(dialog, timeout).GetAwaiter().GetResult();
        // A completed dialog task does not block here.
        return dialog.IsCompleted ? dialog.GetAwaiter().GetResult() : null;
    }

    string BuildElevationNotice(string[] arguments, string reason, bool previousDismissalTooFast)
    {
        var text = new StringBuilder();
        if (previousDismissalTooFast)
        {
            text.AppendLine("The previous dialog was dismissed too fast to be deliberate, so it is shown again.");
            text.AppendLine();
        }

        text.AppendLine("A Windows elevation (UAC) prompt will appear after you press OK.");
        text.AppendLine("Reading a volume's MFT needs administrator rights, so " + reason);
        text.AppendLine();
        text.AppendLine("Executable: " + _getProcessPath());
        text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Arguments ({arguments.Length}), one per line exactly as received; <empty> marks an empty argument:"));
        foreach (var line in ArgumentLines(arguments))
        {
            text.AppendLine(line);
        }

        text.AppendLine();
        text.Append("Stop typing, then press OK to continue, or Cancel to run it from an elevated terminal yourself.");
        return text.ToString();
    }

    /// <summary>
    ///     Lists the arguments as data, one per line and verbatim, never as a command line: no quoting rule is
    ///     correct for every shell. An empty argument is shown as <c>&lt;empty&gt;</c>.
    /// </summary>
    static IEnumerable<string> ArgumentLines(string[] arguments)
    {
        return arguments.Select(argument => argument.Length == 0 ? "  <empty>" : $"  {argument}");
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    static extern int MessageBoxNative(IntPtr window, string text, string caption, uint type);

    [DllImport("user32.dll", EntryPoint = "MessageBeep")]
    static extern bool MessageBeepNative(uint type);
}
