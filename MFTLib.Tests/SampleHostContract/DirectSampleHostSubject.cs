using MFTLib.Index;
using Microsoft.Extensions.Time.Testing;
using SampleProgram.Direct;

namespace MFTLib.Tests.SampleHostContract;

sealed class DirectSampleHostSubject(TextWriter output, TextReader input, string directory, FakeTimeProvider clock, Action elevated)
    : SampleHostSubject(clock)
{
    readonly SampleHost _host = new(output, input, directory, clock)
    {
        _isElevated = () => false,
        _canSelfElevate = () => true,
        _messageBeep = _ => true,
        _tryRunElevated = (_, _) =>
        {
            elevated();
            return true;
        }
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

    public override int RunElevation(string[] arguments) => _host.RunWithElevation(arguments, ElevationNeed.SelfElevate, () => 0);

    public override int RunGated(string[] arguments, Func<int> run) => _host.RunWithElevation(arguments, ElevationNeed.SelfElevate, run);

    public override void PauseBeforeExit(bool ownsWindow) => _host.PauseBeforeExit(ownsWindow);

    public override IProgress<IndexScanProgress> CreateReporter(TextWriter writer) => new SampleHost.PhaseReporter(writer);
}
