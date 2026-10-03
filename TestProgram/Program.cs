using MFTLib;
using TestProgram;

// A launch that carries --broker is the elevated child of a scan-drive run, not a normal start.
if (ElevatedEntryPoint.TryHandle(args, new DefaultElevatedEntryRunner()))
{
    return 0;
}

var scanner = new DriveScanner();
return scanner.Run(args);
