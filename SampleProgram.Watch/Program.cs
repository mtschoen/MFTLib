using MFTLib;
using SampleProgram.Watch;

// A launch that carries --broker is the elevated child of a broker run, not a normal start.
return ElevatedEntryPoint.RunBrokerIfRequested(args)
    ? 0
    : new SampleHost(Console.Out, ConsoleKeyReader.Create()).RunAndPause(args);
