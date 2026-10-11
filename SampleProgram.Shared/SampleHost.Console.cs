using System.Runtime.InteropServices;

#if SAMPLE_WATCH
namespace SampleProgram.Watch;
#else
namespace SampleProgram.Direct;
#endif

// What a double-clicked sample needs so its window stays readable: a key press before the window closes, asked
// only when this process owns the console window and somebody is attending. Both natives are bare imports behind
// seams, so every decision below runs in tests.
partial class SampleHost
{
    internal const string PausePrompt = "Press any key to close this window.";

    internal Func<bool> _ownsConsoleWindow = OwnsConsoleWindowNative;
    internal Action _readKey = ReadKeyNative;

    // An elevated relaunch writes to output.log and its window closes at once; the unelevated parent holds the window.
    bool _outputRedirectedToFile;

    /// <summary>Runs the command line, then holds an attended, self-owned console window open until a key press.</summary>
    internal int RunAndPause(string[] arguments)
    {
        var exitCode = Run(arguments);
        if (!_outputRedirectedToFile && !IsUnattended() && _ownsConsoleWindow())
        {
            _writeLine(PausePrompt);
            _readKey();
        }

        return exitCode;
    }

    // A console window launched from Explorer has this process alone attached; a shell run has the shell attached too.
    static bool OwnsConsoleWindowNative()
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            return false;
        }

        var attached = new uint[2];
        return GetConsoleProcessList(attached, (uint)attached.Length) == 1;
    }

    static void ReadKeyNative() => Console.ReadKey(true);

    [DllImport("kernel32.dll")]
    static extern uint GetConsoleProcessList(uint[] processList, uint processCount);
}
