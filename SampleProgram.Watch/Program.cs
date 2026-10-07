using MFTLib;
using SampleProgram.Watch;

// A launch that carries --broker is the elevated child of a scan-drive run, not a normal start.
if (ElevatedEntryPoint.TryHandle(args))
{
    return 0;
}

var host = new SampleHost();
return host.Run(args);
