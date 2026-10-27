using System.Diagnostics;
using System.Text.Json;

namespace Benchmark;

internal static class ScenarioMemoryProcess
{
    internal static ProcessStartInfo CreateStartInfo(string assemblyPath)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.ChangeExtension(assemblyPath, ".runtimeconfig.json"));
        start.ArgumentList.Add(assemblyPath);
        start.ArgumentList.Add("--measure-memory");
        return start;
    }

    internal static async Task<ScenarioMemoryResult> MeasureAsync(
        ScenarioMemoryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var process = new Process();
        process.StartInfo = CreateStartInfo(typeof(BenchmarkRunner).Assembly.Location);
        process.Start();
        var processId = process.Id;
        var standardOutputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var standardErrorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request)).ConfigureAwait(false);
        process.StandardInput.Close();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
            throw;
        }

        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = (await standardErrorTask.ConfigureAwait(false)).Trim();
        return ParseWorkerResult(request.Scenario, processId, process.ExitCode, standardOutput, standardError);
    }

    internal static ScenarioMemoryResult ParseWorkerResult(
        string scenario, int expectedProcessId, int exitCode, string standardOutput, string standardError)
    {
        if (exitCode != 0)
        {
            throw CreateWorkerFailure(scenario, $"exited with code {exitCode}", standardError);
        }

        ScenarioMemoryResult? result;
        try
        {
            result = JsonSerializer.Deserialize<ScenarioMemoryResult>(standardOutput);
        }
        catch (JsonException exception)
        {
            throw CreateWorkerFailure(scenario, $"returned invalid JSON ({exception.Message})", standardError, exception);
        }

        if (result is null || result.PeakPrivateBytes <= 0 || result.PeakWorkingSetBytes <= 0 ||
            string.IsNullOrWhiteSpace(result.Method) || result.ProcessId != expectedProcessId)
        {
            throw CreateWorkerFailure(scenario, "returned invalid memory measurements", standardError);
        }

        return result;
    }

    static InvalidOperationException CreateWorkerFailure(
        string scenario, string reason, string standardError, Exception? innerException = null)
    {
        var details = string.IsNullOrEmpty(standardError) ? "" : $": {standardError}";
        return new InvalidOperationException(
            $"Memory worker for scenario '{scenario}' {reason}{details}", innerException);
    }
}
