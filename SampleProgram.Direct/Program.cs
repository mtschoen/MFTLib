using SampleProgram.Direct;

var host = new SampleHost(Console.Out, ConsoleKeyReader.Create(), AppContext.BaseDirectory);
return host.RunAndPause(args);
