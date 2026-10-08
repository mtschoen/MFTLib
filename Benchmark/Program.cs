using Benchmark;

if (args.Length > 0 && args[0] == "names")
{
    return await NameAccessorSpike.RunAsync(args[1..]);
}

var runner = new BenchmarkRunner();
return await runner.RunAsync(args);
