using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;

namespace TaxIntelligenceStudio.Infrastructure.Training;

public sealed class LocalModelTrainingService(
    IHardwareAccelerationService hardwareService,
    TimeProvider timeProvider) : IModelTrainingService
{
    private const string EmbeddedTrainerName =
        "TaxIntelligenceStudio.Infrastructure.Training.local_lora_trainer.py";

    public IReadOnlyCollection<ModelTrainingCheckpoint> GetCheckpoints(string outputDirectory)
    {
        var checkpoints = new List<ModelTrainingCheckpoint>
        {
            new("Start new training", null, null, null)
        };
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            return checkpoints;
        }

        checkpoints.AddRange(
            Directory.EnumerateDirectories(outputDirectory, "checkpoint-*", SearchOption.TopDirectoryOnly)
                .Select(path => new
                {
                    Path = path,
                    Step = ParseCheckpointStep(path),
                    StatePath = Path.Combine(path, "trainer_state.json")
                })
                .Where(item => item.Step is not null && File.Exists(item.StatePath))
                .OrderByDescending(item => item.Step)
                .Select(item =>
                {
                    var savedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(item.StatePath), TimeSpan.Zero);
                    return new ModelTrainingCheckpoint(
                        $"Step {item.Step:N0} - {savedAt.LocalDateTime:g}",
                        item.Path,
                        item.Step,
                        savedAt);
                }));
        return checkpoints;
    }

    public async Task<ModelTrainingResult> TrainAsync(
        ModelTrainingRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var accelerators = await hardwareService.DetectAsync(cancellationToken);
        var selected = SelectTrainingAccelerator(accelerators, request.AllowCpuFallback);

        if (selected is null)
        {
            throw new InvalidOperationException(
                BuildNoTrainingDeviceMessage(
                    accelerators,
                    request.AllowCpuFallback,
                    TrainingPlatform.CurrentArchitecture));
        }

        Directory.CreateDirectory(request.OutputDirectory);
        var scriptPath = Path.Combine(request.OutputDirectory, "local_lora_trainer.py");
        await ExtractTrainerAsync(scriptPath, cancellationToken);
        progress?.Report($"Selected {selected.Name} through {selected.Runtime}.");
        if (!string.IsNullOrWhiteSpace(request.ResumeCheckpoint))
        {
            progress?.Report($"Resuming from checkpoint: {request.ResumeCheckpoint}");
        }

        var pythonExecutable = await ResolvePythonExecutableAsync(
            request.PythonExecutable,
            selected.Kind,
            progress,
            cancellationToken);

        var log = new StringBuilder();
        void AppendLog(string line)
        {
            lock (log)
            {
                log.AppendLine(line);
            }

            progress?.Report(line);
        }

        using var process = new Process
        {
            StartInfo = BuildStartInfo(
                request,
                scriptPath,
                selected.Kind,
                pythonExecutable)
        };
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                AppendLog(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                AppendLog($"stderr: {args.Data}");
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("The local model trainer could not be started.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        process.WaitForExit();
        var logPath = Path.Combine(request.OutputDirectory, "training.log");
        var capturedLog = log.ToString();
        await File.WriteAllTextAsync(logPath, capturedLog, cancellationToken);
        var success = process.ExitCode == 0;
        var summary = success
            ? $"LoRA training completed. Full log: {logPath}"
            : $"Trainer exited with code {process.ExitCode}. Full log: {logPath}";
        var message = string.IsNullOrWhiteSpace(capturedLog)
            ? summary
            : $"{capturedLog.TrimEnd()}{Environment.NewLine}{summary}";
        return new ModelTrainingResult(
            success,
            selected.Name,
            request.OutputDirectory,
            message,
            timeProvider.GetUtcNow());
    }

    public static ComputeAccelerator? SelectTrainingAccelerator(
        IReadOnlyCollection<ComputeAccelerator> accelerators,
        bool allowCpuFallback) =>
        accelerators.FirstOrDefault(item =>
            item.SupportsTraining && item.Kind == AcceleratorKind.CudaGpu)
        ?? accelerators.FirstOrDefault(item =>
            item.SupportsTraining && item.Kind == AcceleratorKind.DirectMlGpu)
        ?? (allowCpuFallback
            ? accelerators.FirstOrDefault(item =>
                item.SupportsTraining && item.Kind == AcceleratorKind.Cpu)
            : null);

    /// <summary>
    /// Explains why no training device could be selected, using the reasons
    /// reported by detection so the message is accurate for this machine.
    /// </summary>
    internal static string BuildNoTrainingDeviceMessage(
        IReadOnlyCollection<ComputeAccelerator> accelerators,
        bool allowCpuFallback,
        Architecture architecture)
    {
        var blocked = accelerators
            .Where(item => !item.SupportsTraining &&
                           !string.IsNullOrWhiteSpace(item.TrainingUnavailableReason))
            .Select(item => $"{item.Name}: {item.TrainingUnavailableReason}")
            .Distinct()
            .ToList();

        var cpuAvailable = accelerators.Any(item =>
            item.Kind == AcceleratorKind.Cpu && item.SupportsTraining);
        var npuDetected = accelerators.Any(item => item.Kind == AcceleratorKind.Npu);

        var message = npuDetected
            ? "An NPU was detected, but current Windows NPU execution providers support " +
              "inference rather than model-weight training. No usable CUDA or DirectML " +
              "GPU was detected."
            : "No CUDA or DirectML training GPU was detected.";

        if (blocked.Count > 0)
        {
            message += $" {string.Join(" ", blocked)}";
        }

        if (cpuAvailable && !allowCpuFallback)
        {
            message += " CPU training is available on this machine; enable CPU fallback to use it.";
        }

        var hostGuidance = TrainingPlatform.HostGuidance(architecture);
        if (hostGuidance is not null)
        {
            message += $" {hostGuidance}";
        }

        return message;
    }

    private static ProcessStartInfo BuildStartInfo(
        ModelTrainingRequest request,
        string scriptPath,
        AcceleratorKind accelerator,
        string pythonExecutable)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(request.BaseModel);
        startInfo.ArgumentList.Add("--dataset");
        startInfo.ArgumentList.Add(request.DatasetPath);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(request.OutputDirectory);
        startInfo.ArgumentList.Add("--device");
        startInfo.ArgumentList.Add(accelerator switch
        {
            AcceleratorKind.CudaGpu => "cuda",
            AcceleratorKind.DirectMlGpu => "directml",
            _ => "cpu"
        });
        startInfo.ArgumentList.Add("--epochs");
        startInfo.ArgumentList.Add(request.Epochs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--batch-size");
        startInfo.ArgumentList.Add(request.BatchSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--learning-rate");
        startInfo.ArgumentList.Add(request.LearningRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(request.ResumeCheckpoint))
        {
            startInfo.ArgumentList.Add("--resume-checkpoint");
            startInfo.ArgumentList.Add(request.ResumeCheckpoint);
        }

        return startInfo;
    }

    private static int? ParseCheckpointStep(string path)
    {
        const string prefix = "checkpoint-";
        var name = Path.GetFileName(path);
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(
                   name[prefix.Length..],
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out var step)
            ? step
            : null;
    }

    internal static async Task<string> ResolvePythonExecutableAsync(
        string configuredPython,
        AcceleratorKind accelerator,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        if (!configuredPython.Equals("python", StringComparison.OrdinalIgnoreCase) &&
            !configuredPython.Equals("python.exe", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(configuredPython);
        }
        else
        {
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TaxIntelligenceStudio",
                ".venv-training",
                "Scripts",
                "python.exe"));
            candidates.AddRange(FindSiblingTrainerEnvironments());
            candidates.Add(configuredPython);
        }

        var failures = new List<string>();
        var probedCandidates = new List<string>();
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Path.IsPathFullyQualified(candidate) && !File.Exists(candidate))
            {
                continue;
            }

            probedCandidates.Add(candidate);
            var probe = await ProbePythonAsync(candidate, accelerator, cancellationToken);
            if (probe.Succeeded)
            {
                progress?.Report($"Using compatible Python environment: {candidate}");
                return candidate;
            }

            failures.Add($"{candidate}: {probe.Message}");
        }

        var host = TrainingPlatform.CurrentArchitecture;
        var install =
            $" Install: {TrainingPlatform.InstallCommand(accelerator == AcceleratorKind.DirectMlGpu)}";

        // An Arm64-native interpreter can never resolve a PyTorch wheel. Say so
        // explicitly, otherwise the install command above is advice that cannot
        // succeed no matter how many times it is run.
        var interpreterBlockers = new List<string>();
        foreach (var candidate in probedCandidates)
        {
            var interpreterArchitecture =
                await DetectInterpreterArchitectureAsync(candidate, cancellationToken);
            if (interpreterArchitecture is null)
            {
                continue;
            }

            var reason = TrainingPlatform.InterpreterUnavailableReason(
                interpreterArchitecture.Value,
                host);
            if (reason is not null)
            {
                interpreterBlockers.Add($"{candidate}: {reason}");
            }
        }

        if (interpreterBlockers.Count > 0)
        {
            install = $" {string.Join(" ", interpreterBlockers.Distinct())}";
        }

        throw new InvalidOperationException(
            $"No compatible local Python training environment was found. " +
            $"{string.Join(" | ", failures)}{install}");
    }

    /// <summary>
    /// Reports the architecture of a Python interpreter, which determines which
    /// wheels it can install. Returns null when the interpreter cannot be run.
    /// </summary>
    /// <remarks>
    /// <c>platform.machine()</c> reports the architecture of the interpreter
    /// process rather than of the machine, which is exactly what matters here:
    /// an x64 interpreter emulated on Windows on Arm reports <c>AMD64</c> and
    /// installs x64 wheels successfully.
    /// </remarks>
    internal static async Task<Architecture?> DetectInterpreterArchitectureAsync(
        string pythonExecutable,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = pythonExecutable,
                ArgumentList = { "-c", "import platform; print(platform.machine())" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 ? ParseMachine(output) : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    internal static Architecture? ParseMachine(string machine) =>
        machine.Trim().ToUpperInvariant() switch
        {
            "AMD64" or "X86_64" => Architecture.X64,
            "ARM64" or "AARCH64" => Architecture.Arm64,
            "X86" => Architecture.X86,
            _ => null
        };

    private static IEnumerable<string> FindSiblingTrainerEnvironments()
    {
        var roots = new[]
        {
            Environment.CurrentDirectory,
            AppContext.BaseDirectory
        };

        foreach (var root in roots)
        {
            var directory = new DirectoryInfo(root);
            while (directory is not null)
            {
                var candidate = Path.Combine(
                    directory.FullName,
                    "modeltrainer",
                    ".venv-training",
                    "Scripts",
                    "python.exe");
                if (File.Exists(candidate))
                {
                    yield return candidate;
                }

                directory = directory.Parent;
            }
        }
    }

    private static async Task<(bool Succeeded, string Message)> ProbePythonAsync(
        string pythonExecutable,
        AcceleratorKind accelerator,
        CancellationToken cancellationToken)
    {
        var probeCode = accelerator switch
        {
            AcceleratorKind.DirectMlGpu =>
                "import torch,torch_directml,transformers,datasets,peft; assert torch_directml.is_available()",
            AcceleratorKind.CudaGpu =>
                "import torch,transformers,datasets,peft; assert torch.cuda.is_available()",
            _ => "import torch,transformers,datasets,peft"
        };
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = pythonExecutable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(probeCode);

        try
        {
            if (!process.Start())
            {
                return (false, "process did not start");
            }

            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0
                ? (true, "compatible")
                : (false, (await standardError).Trim());
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return (false, exception.Message);
        }
    }

    private static async Task ExtractTrainerAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var resource = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(EmbeddedTrainerName)
            ?? throw new InvalidOperationException("The embedded local trainer was not found.");
        await using var output = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            useAsync: true);
        await resource.CopyToAsync(output, cancellationToken);
    }

    private static void ValidateRequest(ModelTrainingRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BaseModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DatasetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PythonExecutable);
        if (!File.Exists(request.DatasetPath))
        {
            throw new FileNotFoundException("The training dataset was not found.", request.DatasetPath);
        }

        if (!string.IsNullOrWhiteSpace(request.ResumeCheckpoint))
        {
            var checkpointPath = ResolvePath(request.ResumeCheckpoint);
            var outputPath = ResolvePath(request.OutputDirectory);
            var relativePath = Path.GetRelativePath(outputPath, checkpointPath);
            if (relativePath.Equals("..", StringComparison.Ordinal) ||
                relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal) ||
                Path.IsPathFullyQualified(relativePath))
            {
                throw new ArgumentException(
                    "The resume checkpoint must be inside the selected output directory.",
                    nameof(request));
            }

            if (!Directory.Exists(checkpointPath) ||
                !File.Exists(Path.Combine(checkpointPath, "trainer_state.json")))
            {
                throw new DirectoryNotFoundException(
                    $"The selected training checkpoint is missing or incomplete: {checkpointPath}");
            }

            if (!File.Exists(Path.Combine(checkpointPath, "training_run.json")))
            {
                throw new InvalidOperationException(
                    "The selected checkpoint has no compatibility manifest and cannot be resumed safely.");
            }
        }

        if (request.Epochs <= 0 || request.BatchSize <= 0 || request.LearningRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Epochs, batch size, and learning rate must be positive.");
        }
    }

    private static string ResolvePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            return fullPath;
        }

        var directory = new DirectoryInfo(fullPath);
        var segments = new Stack<string>();
        while (directory.Parent is not null && directory.LinkTarget is null)
        {
            segments.Push(directory.Name);
            directory = directory.Parent;
        }

        if (directory.LinkTarget is not null)
        {
            var resolvedTarget = directory.ResolveLinkTarget(returnFinalTarget: true)
                ?? throw new IOException($"Could not resolve directory link: {directory.FullName}");
            directory = new DirectoryInfo(resolvedTarget.FullName);
        }

        var resolved = directory.FullName;
        while (segments.TryPop(out var segment))
        {
            resolved = Path.Combine(resolved, segment);
        }

        return Path.GetFullPath(resolved);
    }
}
