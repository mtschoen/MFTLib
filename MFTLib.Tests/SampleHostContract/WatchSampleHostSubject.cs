using MFTLib.Index;
using Microsoft.Extensions.Time.Testing;
using SampleProgram.Watch;

namespace MFTLib.Tests.SampleHostContract;

sealed class WatchSampleHostSubject(TextWriter output, TextReader input, FakeTimeProvider clock, Action elevated)
    : SampleHostSubject(clock)
{
    readonly SampleHost _host = new(output, input, clock)
    {
        _isElevated = () => false,
        _messageBeep = _ => true
    };

    public override string Title => SampleHost.ElevationNoticeTitle;

    public override uint ExpectedFlags =>
        SampleHost.MessageBoxOkCancel | SampleHost.MessageBoxIconInformation | SampleHost.MessageBoxSystemModal
        | SampleHost.MessageBoxSetForeground | SampleHost.MessageBoxTopMost;

    public override uint BeepIcon => SampleHost.MessageBeepIconExclamation;
    public override int ResultOk => SampleHost.MessageBoxResultOk;
    public override int ResultCancel => SampleHost.MessageBoxResultCancel;
    public override TimeSpan AccidentalInterval => SampleHost.AccidentalDismissalInterval;
    public override TimeSpan NoticeTimeout => SampleHost.ElevationNoticeTimeout;
    public override string UnattendedVariableName => SampleHost.UnattendedVariableName;
    public override uint ProgressRowInterval => SampleHost.PhaseReporter.ProgressRowInterval;
    public override string PausePrompt => SampleHost.PausePrompt;

    public override void SetMessageBox(Func<IntPtr, string, string, uint, int> messageBox) => _host._messageBox = messageBox;

    public override void SetMessageBeep(Func<uint, bool> messageBeep) => _host._messageBeep = messageBeep;

    public override void SetDialogScheduler(TaskScheduler scheduler) => _host._dialogScheduler = scheduler;

    public override void SetElevated(bool isElevated) => _host._isElevated = () => isElevated;

    // The Watch gate runs its continuation (the broker launch) once the dialog is acknowledged.
    public override int RunElevation(string[] arguments) =>
        _host.RunWithElevation(arguments, ElevationNeed.BrokerLaunch, () =>
        {
            elevated();
            return 0;
        });

    public override int RunGated(string[] arguments, Func<int> run) => _host.RunWithElevation(arguments, ElevationNeed.BrokerLaunch, run);

    public override void PauseBeforeExit(bool ownsWindow) => _host.PauseBeforeExit(ownsWindow);

    public override IProgress<IndexScanProgress> CreateReporter(TextWriter writer) => new SampleHost.PhaseReporter(writer);
}
