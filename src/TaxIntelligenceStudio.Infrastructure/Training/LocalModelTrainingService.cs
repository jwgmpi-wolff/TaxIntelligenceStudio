using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;

namespace TaxIntelligenceStudio.Infrastructure.Training;

public sealed class LocalModelTrainingService(
    IHardwareAccelerationService hardwareService,
    TimeProvider timeProvider) : IModelTrainingService
{
    private const string EmbeddedTrainerName =
        "TaxIntelligenceStudio.Infrastructure.Training.local_lora_trainer.py";
    private static readonly SemaphoreSlim EnvironmentInstallLock = new(1, 1);
    private static readonly HttpClient DownloadClient = new();

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

        if (string.IsNullOrWhiteSpace(request.ResumeCheckpoint) &&
            ContainsTrainingArtifacts(request.OutputDirectory))
        {
            // Checkpoint rotation would delete a new run's checkpoints when they share a
            // folder with older, higher-numbered checkpoints, so new runs get their own folder.
            var runDirectory = Path.Combine(
                request.OutputDirectory,
                $"run-{timeProvider.GetLocalNow():yyyyMMdd-HHmmss}");
            progress?.Report(
                $"The output folder already contains training artifacts; " +
                $"writing this new run to {runDirectory}");
            request = request with { OutputDirectory = runDirectory };
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
        var success = process.ExitCode == 0 &&
                      HasValidAdapterArtifacts(request.OutputDirectory, request.BaseModel) &&
                      HasCompleteTrainingRun(request.OutputDirectory);
        var summary = success
            ? $"LoRA training and held-out evaluation completed. Run manifests: " +
              $"{Path.Combine(request.OutputDirectory, "evaluation.json")}; full log: {logPath}"
            : process.ExitCode != 0
                ? $"Trainer exited with code {process.ExitCode}. Full log: {logPath}"
                : $"Trainer exited successfully but produced no valid adapter for the selected base model. " +
                  $"Full log: {logPath}";
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

    internal static bool HasValidAdapterArtifacts(string outputDirectory, string baseModel)
    {
        var adapterDirectory = Path.Combine(outputDirectory, "adapter");
        var configPath = Path.Combine(adapterDirectory, "adapter_config.json");
        var weightsPath = Path.Combine(adapterDirectory, "adapter_model.safetensors");
        if (!File.Exists(configPath) ||
            !File.Exists(weightsPath) ||
            new FileInfo(weightsPath).Length == 0)
        {
            return false;
        }

        try
        {
            using var configuration = JsonDocument.Parse(File.ReadAllText(configPath));
            return configuration.RootElement.TryGetProperty(
                       "base_model_name_or_path",
                       out var configuredModel) &&
                   configuredModel.ValueKind == JsonValueKind.String &&
                   string.Equals(
                       configuredModel.GetString(),
                       baseModel,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool HasCompleteTrainingRun(string outputDirectory)
    {
        var requiredFiles = new[]
        {
            "training_run.json",
            "run_environment.json",
            "evaluation.json",
            "artifact_manifest.json"
        };
        if (!requiredFiles.All(file =>
        {
            var path = Path.Combine(outputDirectory, file);
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }))
        {
            return false;
        }

        try
        {
            using var run = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(outputDirectory, "training_run.json")));
            using var environment = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(outputDirectory, "run_environment.json")));
            using var evaluation = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(outputDirectory, "evaluation.json")));
            using var manifest = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(outputDirectory, "artifact_manifest.json")));
            var evalPath = Path.Combine(outputDirectory, "evaluation.json");
            var manifestRoot = manifest.RootElement;
            if (run.RootElement.ValueKind != JsonValueKind.Object ||
                environment.RootElement.ValueKind != JsonValueKind.Object ||
                evaluation.RootElement.ValueKind != JsonValueKind.Object ||
                !evaluation.RootElement.TryGetProperty("status", out var evaluationStatus) ||
                evaluationStatus.ValueKind != JsonValueKind.String ||
                evaluationStatus.GetString() is not ("completed" or "not_run_no_held_out_data") ||
                !manifestRoot.TryGetProperty("evaluation_sha256", out var expectedEvaluationHash) ||
                expectedEvaluationHash.ValueKind != JsonValueKind.String ||
                !string.Equals(
                    expectedEvaluationHash.GetString(),
                    ComputeSha256(evalPath),
                    StringComparison.OrdinalIgnoreCase) ||
                !manifestRoot.TryGetProperty("adapter_files", out var adapterFiles) ||
                adapterFiles.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var adapterDirectory = Path.GetFullPath(Path.Combine(outputDirectory, "adapter"));
            var adapterPrefix = adapterDirectory + Path.DirectorySeparatorChar;
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in adapterFiles.EnumerateArray())
            {
                if (!entry.TryGetProperty("file", out var fileName) ||
                    fileName.ValueKind != JsonValueKind.String ||
                    !entry.TryGetProperty("bytes", out var expectedBytes) ||
                    !expectedBytes.TryGetInt64(out var byteCount) ||
                    !entry.TryGetProperty("sha256", out var expectedHash) ||
                    expectedHash.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                var filePath = Path.GetFullPath(
                    Path.Combine(adapterDirectory, fileName.GetString() ?? string.Empty));
                if (!filePath.StartsWith(adapterPrefix, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(filePath) ||
                    new FileInfo(filePath).Length != byteCount ||
                    !string.Equals(expectedHash.GetString(), ComputeSha256(filePath), StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                seenFiles.Add(Path.GetFileName(filePath));
            }

            return seenFiles.Contains("adapter_config.json") &&
                   seenFiles.Contains("adapter_model.safetensors");
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

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

    internal static ProcessStartInfo BuildStartInfo(
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
        foreach (var datasetPath in request.DatasetPaths.Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var splitDirectory = Path.Combine(
                Path.GetDirectoryName(datasetPath)!,
                $"{Path.GetFileNameWithoutExtension(datasetPath)}-splits");
            var splitManifest = Path.Combine(splitDirectory, "manifest.json");
            var trainingDataset = Path.Combine(splitDirectory, "train.jsonl");
            var useSplits = File.Exists(splitManifest);
            if (useSplits &&
                (!File.Exists(trainingDataset) || new FileInfo(trainingDataset).Length == 0))
            {
                throw new InvalidOperationException(
                    $"The validated training split for '{datasetPath}' is empty. " +
                    "Do not train on the unfiltered full export; inspect the split manifest exclusions first.");
            }

            startInfo.ArgumentList.Add("--dataset");
            startInfo.ArgumentList.Add(useSplits ? trainingDataset : datasetPath);
            if (useSplits)
            {
                startInfo.ArgumentList.Add("--split-manifest");
                startInfo.ArgumentList.Add(splitManifest);
                AddDatasetArgument(startInfo, "--validation-dataset", splitDirectory, "validation.jsonl");
                AddDatasetArgument(startInfo, "--test-dataset", splitDirectory, "test.jsonl");
            }
        }
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
        startInfo.ArgumentList.Add("--seed");
        startInfo.ArgumentList.Add(request.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--lora-rank");
        startInfo.ArgumentList.Add(request.LoraRank.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--lora-alpha");
        startInfo.ArgumentList.Add(request.LoraAlpha.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--lora-dropout");
        startInfo.ArgumentList.Add(request.LoraDropout.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (request.MaximumSequenceLength is not null)
        {
            startInfo.ArgumentList.Add("--max-sequence-length");
            startInfo.ArgumentList.Add(
                request.MaximumSequenceLength.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrWhiteSpace(request.ResumeCheckpoint))
        {
            startInfo.ArgumentList.Add("--resume-checkpoint");
            startInfo.ArgumentList.Add(request.ResumeCheckpoint);
        }

        return startInfo;
    }

    private static void AddDatasetArgument(
        ProcessStartInfo startInfo,
        string argumentName,
        string directory,
        string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            startInfo.ArgumentList.Add(argumentName);
            startInfo.ArgumentList.Add(path);
        }
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
        CancellationToken cancellationToken,
        bool allowRepair = true)
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

        if (!allowRepair)
        {
            throw new InvalidOperationException(
                "No compatible installed Python runtime was found for trained inference. " +
                "Configure ModelProviders:TrainedPythonExecutable with your training environment. " +
                string.Join(Environment.NewLine, failures));
        }

        progress?.Report(
            "No compatible training environment was found. Inspecting this machine and repairing the private environment...");
        try
        {
            var repairedPython = await EnsureTrainingEnvironmentAsync(
                configuredPython,
                accelerator,
                progress,
                cancellationToken);
            var repairedProbe = await ProbePythonAsync(
                repairedPython,
                accelerator,
                cancellationToken);
            if (repairedProbe.Succeeded)
            {
                progress?.Report($"Using repaired Python environment: {repairedPython}");
                return repairedPython;
            }

            failures.Add($"{repairedPython}: {repairedProbe.Message}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failures.Add($"automatic environment setup: {exception.Message}");
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

    private static async Task<string> EnsureTrainingEnvironmentAsync(
        string configuredPython,
        AcceleratorKind accelerator,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        await EnvironmentInstallLock.WaitAsync(cancellationToken);
        try
        {
            var dataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TaxIntelligenceStudio");
            var environmentPath = Path.Combine(dataPath, ".venv-training");
            var environmentPython = Path.Combine(environmentPath, "Scripts", "python.exe");

            var bootstrapPython = await FindBootstrapPythonAsync(
                configuredPython,
                environmentPython,
                cancellationToken);
            if (bootstrapPython is null)
            {
                progress?.Report(
                    $"No compatible x64 Python was found. Downloading private Python {TrainingPlatform.TrainingPythonVersion}...");
                bootstrapPython = await InstallPrivatePythonAsync(
                    dataPath,
                    progress,
                    cancellationToken);
            }
            else
            {
                progress?.Report($"Using {bootstrapPython} to build the private training environment.");
            }

            Directory.CreateDirectory(dataPath);
            await RunPythonAsync(
                bootstrapPython,
                ["-m", "venv", "--clear", environmentPath],
                "create the private training environment",
                progress,
                cancellationToken);
            await RunPythonAsync(
                environmentPython,
                ["-m", "pip", "install", "--disable-pip-version-check", "--upgrade", "pip"],
                "update pip",
                progress,
                cancellationToken);

            var packages = TrainingPlatform.Packages(
                accelerator == AcceleratorKind.DirectMlGpu);
            progress?.Report(
                $"Installing required training components: {string.Join(", ", packages)}");
            await RunPythonAsync(
                environmentPython,
                [
                    "-m",
                    "pip",
                    "install",
                    "--disable-pip-version-check",
                    "--upgrade",
                    .. packages
                ],
                "install the model-training components",
                progress,
                cancellationToken);
            return environmentPython;
        }
        finally
        {
            EnvironmentInstallLock.Release();
        }
    }

    private static async Task<string?> FindBootstrapPythonAsync(
        string configuredPython,
        string environmentPython,
        CancellationToken cancellationToken)
    {
        var localPrograms = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new[]
        {
            configuredPython,
            Path.Combine(localPrograms, "TaxIntelligenceStudio", "python311-x64", "python.exe"),
            Path.Combine(localPrograms, "Programs", "Python", "Python312", "python.exe"),
            Path.Combine(localPrograms, "Programs", "Python", "Python311", "python.exe"),
            Path.Combine(localPrograms, "Programs", "Python", "Python310", "python.exe"),
            Path.Combine(programFiles, "Python312", "python.exe"),
            Path.Combine(programFiles, "Python311", "python.exe"),
            Path.Combine(programFiles, "Python310", "python.exe"),
            "python"
        };

        foreach (var candidate in candidates
                     .Where(candidate =>
                         !string.Equals(candidate, environmentPython, StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Path.IsPathFullyQualified(candidate) && !File.Exists(candidate))
            {
                continue;
            }

            if (await IsCompatibleBootstrapPythonAsync(candidate, cancellationToken))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<bool> IsCompatibleBootstrapPythonAsync(
        string pythonExecutable,
        CancellationToken cancellationToken)
    {
        try
        {
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
            process.StartInfo.ArgumentList.Add(
                "import platform,sys; print(platform.machine()); print(f'{sys.version_info.major}.{sys.version_info.minor}')");
            if (!process.Start())
            {
                return false;
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                return false;
            }

            var lines = output.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.Length >= 2 &&
                   ParseMachine(lines[0]) == Architecture.X64 &&
                   Version.TryParse(lines[1], out var version) &&
                   version >= new Version(3, 10) &&
                   version < new Version(3, 13);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task<string> InstallPrivatePythonAsync(
        string dataPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var pythonRoot = Path.Combine(dataPath, "python311-x64");
        var pythonExecutable = Path.Combine(pythonRoot, "python.exe");
        var version = TrainingPlatform.TrainingPythonVersion;
        var installerName = $"python-{version}-amd64.exe";
        var installerPath = Path.Combine(Path.GetTempPath(), installerName);
        var installerUri = new Uri(
            $"https://www.python.org/ftp/python/{version}/{installerName}");

        Directory.CreateDirectory(dataPath);
        try
        {
            await using (var destination = new FileStream(
                             installerPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             useAsync: true))
            await using (var source = await DownloadClient.GetStreamAsync(
                             installerUri,
                             cancellationToken))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = installerPath,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            foreach (var argument in new[]
                     {
                         "/quiet",
                         "InstallAllUsers=0",
                         $"TargetDir={pythonRoot}",
                         "Include_core=1",
                         "Include_exe=1",
                         "Include_lib=1",
                         "Include_pip=1",
                         "Include_launcher=0",
                         "Include_test=0",
                         "Include_tools=0",
                         "AssociateFiles=0",
                         "Shortcuts=0",
                         "PrependPath=0",
                         "CompileAll=0"
                     })
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (!process.Start())
            {
                throw new InvalidOperationException("The private Python installer could not be started.");
            }

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"The private Python installer exited with code {process.ExitCode}.");
            }
        }
        finally
        {
            File.Delete(installerPath);
        }

        if (!File.Exists(pythonExecutable))
        {
            throw new InvalidOperationException(
                $"Python installation completed without producing '{pythonExecutable}'.");
        }

        progress?.Report($"Installed private x64 Python {version} at {pythonRoot}.");
        return pythonExecutable;
    }

    private static async Task RunPythonAsync(
        string pythonExecutable,
        IReadOnlyList<string> arguments,
        string operation,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
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
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not {operation}.");
        }

        var outputTask = RelayOutputAsync(process.StandardOutput, progress, cancellationToken);
        var errorTask = RelayOutputAsync(process.StandardError, progress, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            var detail = string.Join(
                Environment.NewLine,
                new[] { output, error }.Where(value => !string.IsNullOrWhiteSpace(value)));
            throw new InvalidOperationException(
                $"Could not {operation} (exit code {process.ExitCode}). {detail}".Trim());
        }
    }

    private static async Task<string> RelayOutputAsync(
        StreamReader reader,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            output.AppendLine(line);
            progress?.Report(line);
        }

        return output.ToString().Trim();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
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
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PythonExecutable);
        foreach (var datasetPath in request.DatasetPaths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(datasetPath);
            if (!File.Exists(datasetPath))
            {
                throw new FileNotFoundException("The training dataset was not found.", datasetPath);
            }
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

        if (request.Epochs <= 0 ||
            request.BatchSize <= 0 ||
            request.LearningRate <= 0 ||
            request.Seed < 0 ||
            request.LoraRank <= 0 ||
            request.LoraAlpha <= 0 ||
            !double.IsFinite(request.LoraDropout) ||
            request.LoraDropout is < 0 or >= 1 ||
            request.MaximumSequenceLength is <= 0 or > 32_768)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Training settings are outside their supported ranges.");
        }
    }

    internal static bool ContainsTrainingArtifacts(string outputDirectory) =>
        Directory.Exists(outputDirectory) &&
        (File.Exists(Path.Combine(outputDirectory, "training_run.json")) ||
         Directory.Exists(Path.Combine(outputDirectory, "adapter")) ||
         Directory.EnumerateDirectories(outputDirectory, "checkpoint-*").Any());

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
