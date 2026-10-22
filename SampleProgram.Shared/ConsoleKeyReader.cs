#if SAMPLE_WATCH
namespace SampleProgram.Watch;
#else
namespace SampleProgram.Direct;
#endif

sealed class ConsoleKeyReader : TextReader
{
    internal static TextReader Create() => Console.IsInputRedirected ? Console.In : new ConsoleKeyReader();
    public override int Read() => Console.ReadKey(intercept: true).KeyChar;
}
