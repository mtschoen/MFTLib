using MFTLib.Index;
using Microsoft.Extensions.Time.Testing;

namespace MFTLib.Tests.SampleHostContract;

/// <summary>
///     One sample's copy of the shared <c>SampleHost</c> source, seen through the members the shared contract tests
///     use. <c>SampleProgram.Shared</c> compiles into both samples, so there are two distinct <c>SampleHost</c>
///     types; each subclass binds the contract tests to exactly one of them and reads that copy's own constants.
/// </summary>
abstract class SampleHostSubject(FakeTimeProvider clock)
{
    public FakeTimeProvider Clock { get; } = clock;

    public abstract string Title { get; }
    public abstract uint ExpectedFlags { get; }
    public abstract uint BeepIcon { get; }
    public abstract int ResultOk { get; }
    public abstract int ResultCancel { get; }
    public abstract TimeSpan AccidentalInterval { get; }
    public abstract TimeSpan NoticeTimeout { get; }
    public abstract string UnattendedVariableName { get; }
    public abstract uint ProgressRowInterval { get; }
    public abstract string PausePrompt { get; }

    public abstract void SetMessageBox(Func<IntPtr, string, string, uint, int> messageBox);
    public abstract void SetMessageBeep(Func<uint, bool> messageBeep);
    public abstract void SetDialogScheduler(TaskScheduler scheduler);
    public abstract void SetElevated(bool isElevated);

    /// <summary>Runs the sample's elevation gate; the elevated continuation reports through the callback the subject was built with.</summary>
    public abstract int RunElevation(string[] arguments);

    /// <summary>Runs the elevation gate with a caller-supplied continuation for the already-elevated path.</summary>
    public abstract int RunGated(string[] arguments, Func<int> run);

    public abstract void PauseBeforeExit(bool ownsWindow);
    public abstract IProgress<IndexScanProgress> CreateReporter(TextWriter writer);
}
