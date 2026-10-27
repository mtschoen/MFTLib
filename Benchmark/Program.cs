using Benchmark;

if (args.Length == 1 && args[0] == "--measure-memory")
{
    return await BenchmarkRunner.RunMemoryWorkerAsync(Console.In, Console.Out, Console.Error);
}

var runner = new BenchmarkRunner(Console.Out);
return await runner.RunAsync(args);
