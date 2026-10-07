using MFTLib;
using SampleProgram.Watch;

// A launch that carries --broker is the elevated child of a broker run, not a normal start.
return ElevatedEntryPoint.TryHandle(args) ? 0 : new SampleHost().Run(args);
