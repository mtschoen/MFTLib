using System.Runtime.InteropServices;

#if SAMPLE_WATCH
namespace SampleProgram.Watch;
#else
namespace SampleProgram.Direct;
#endif

// Console primitives and failure reporting: a key press before the window closes, asked
// only when this process owns the console window and somebody is attending. The entry point probes the window and
// passes the answer in, so every decision below runs in tests.
partial class SampleHost
{
    internal const string PausePrompt = "Press any key to close this window.";

    TextWriter _output;
    readonly TextReader _input;

    internal void PauseBeforeExit(bool ownsWindow)
    {
        if (IsUnattended() || !ownsWindow)
        {
            return;
        }
        _output.WriteLine(PausePrompt);
        _input.Read();
    }

    // The type and message of the failure and of everything it wraps, one line each; no stack trace.
    void WriteFailure(string prefix, Exception exception)
    {
        _output.WriteLine($"{prefix}{exception.GetType().Name}: {exception.Message}");
        IEnumerable<Exception> inner = exception is AggregateException aggregate ? aggregate.InnerExceptions : exception.InnerException is { } single ? [single] : [];
        foreach (var cause in inner)
        {
            WriteFailure("  caused by ", cause);
        }
    }

    // A console window launched from Explorer has this process alone attached; a shell run has the shell attached too.
    internal static bool OwnsConsoleWindowNative()
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            return false;
        }

        var attached = new uint[2];
        return GetConsoleProcessList(attached, (uint)attached.Length) == 1;
    }

    [DllImport("kernel32.dll")]
    static extern uint GetConsoleProcessList(uint[] processList, uint processCount);
}
