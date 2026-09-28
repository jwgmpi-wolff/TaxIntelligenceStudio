using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;

namespace TaxIntelligenceStudio.Infrastructure.Training;

public sealed class WindowsHardwareAccelerationService : IHardwareAccelerationService
{
    public async Task<IReadOnlyCollection<ComputeAccelerator>> DetectAsync(
        CancellationToken cancellationToken = default)
    {
        var accelerators = new List<ComputeAccelerator>();
        var cudaAvailable = await HasCudaRuntimeAsync(cancellationToken);

        if (OperatingSystem.IsWindows())
        {
            AddVideoControllers(accelerators, cudaAvailable);
            AddNpus(accelerators);
        }

        accelerators.Add(new ComputeAccelerator(
            "System CPU",
            AcceleratorKind.Cpu,
            true,
            true,
            "PyTorch CPU"));

        return accelerators
            .DistinctBy(item => new { item.Name, item.Kind })
            .OrderBy(item => item.Kind)
            .ToList();
    }

    [SupportedOSPlatform("windows")]
    private static void AddVideoControllers(
        ICollection<ComputeAccelerator> accelerators,
        bool cudaAvailable)
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name FROM Win32_VideoController");
        using var results = searcher.Get();
        foreach (ManagementObject item in results)
        {
            var name = item["Name"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var isNvidia = name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
            if (IsVirtualDisplayAdapter(name))
            {
                continue;
            }

            accelerators.Add(new ComputeAccelerator(
                name,
                isNvidia && cudaAvailable
                    ? AcceleratorKind.CudaGpu
                    : AcceleratorKind.DirectMlGpu,
                true,
                true,
                isNvidia && cudaAvailable ? "PyTorch CUDA" : "PyTorch DirectML"));
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AddNpus(ICollection<ComputeAccelerator> accelerators)
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name FROM Win32_PnPEntity WHERE Name IS NOT NULL");
        using var results = searcher.Get();
        foreach (ManagementObject item in results)
        {
            var name = item["Name"]?.ToString();
            if (string.IsNullOrWhiteSpace(name) || !IsNpu(name))
            {
                continue;
            }

            accelerators.Add(new ComputeAccelerator(
                name,
                AcceleratorKind.Npu,
                false,
                true,
                "Windows ML execution provider"));
        }
    }

    private static bool IsNpu(string name) =>
        name.Contains("NPU", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Neural Processing", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("AI Boost", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Hexagon", StringComparison.OrdinalIgnoreCase);

    private static bool IsVirtualDisplayAdapter(string name) =>
        name.Contains("External Graphics", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Virtual", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> HasCudaRuntimeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "nvidia-smi.exe",
                Arguments = "--query-gpu=name --format=csv,noheader",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null)
            {
                return false;
            }

            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
