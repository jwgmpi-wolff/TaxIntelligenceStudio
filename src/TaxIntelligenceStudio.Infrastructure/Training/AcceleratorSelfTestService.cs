using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;

namespace TaxIntelligenceStudio.Infrastructure.Training;

public sealed class AcceleratorSelfTestService : IAcceleratorSelfTestService
{
    private const string EmbeddedProbeName =
        "TaxIntelligenceStudio.Infrastructure.Training.accelerator_probe.py";

    public async Task<AcceleratorSelfTestResult> RunAsync(
        string configuredPython,
        CancellationToken cancellationToken = default)
    {
        var python = await LocalModelTrainingService.ResolvePythonExecutableAsync(
            configuredPython,
            AcceleratorKind.DirectMlGpu,
            progress: null,
            cancellationToken);
        var toolsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TaxIntelligenceStudio",
            "Tools");
        Directory.CreateDirectory(toolsDirectory);
        var scriptPath = Path.Combine(toolsDirectory, "accelerator_probe.py");
        await ExtractProbeAsync(scriptPath, cancellationToken);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = python,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add(scriptPath);
        if (!process.Start())
        {
            throw new InvalidOperationException("The accelerator self-test could not be started.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Accelerator self-test failed with exit code {process.ExitCode}: {error}");
        }

        var jsonLine = output.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith('{'))
            ?? throw new InvalidOperationException(
                $"Accelerator self-test returned no result. {output} {error}");
        var result = JsonSerializer.Deserialize<ProbeResult>(
            jsonLine,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Accelerator self-test returned invalid JSON.");

        return new AcceleratorSelfTestResult(
            result.GpuVerified,
            result.NpuVerified,
            result.GpuDevice ?? "Unavailable",
            result.NpuProvider ?? "Unavailable",
            string.IsNullOrWhiteSpace(error) || (result.GpuVerified && result.NpuVerified)
                ? result.Details ?? string.Empty
                : $"{result.Details}{Environment.NewLine}{error.Replace("\0", string.Empty).Trim()}");
    }

    private static async Task ExtractProbeAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var resource = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(EmbeddedProbeName)
            ?? throw new InvalidOperationException("The embedded accelerator probe was not found.");
        await using var output = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            useAsync: true);
        await resource.CopyToAsync(output, cancellationToken);
    }

    private sealed record ProbeResult(
        bool GpuVerified,
        bool NpuVerified,
        string? GpuDevice,
        string? NpuProvider,
        string? Details);
}
