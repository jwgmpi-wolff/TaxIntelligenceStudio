using System.Diagnostics;
using System.Reflection;
using System.Management;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;

namespace TaxIntelligenceStudio.Infrastructure.Training;

public sealed partial class AgentModelService(
    IHardwareAccelerationService hardwareService,
    TimeProvider timeProvider) : IAgentModelService
{
    public const string TransformersRuntime = "Transformers / PEFT";
    public const string OllamaRuntime = "Ollama";

    private const string EmbeddedRuntimeName =
        "TaxIntelligenceStudio.Infrastructure.Training.agent_model_runtime.py";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static string ApplicationDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaxIntelligenceStudio");

    private static string ActiveModelPath =>
        Path.Combine(ApplicationDataDirectory, "active-agent-model.json");

    public IReadOnlyCollection<AgentModelCandidate> DiscoverModels(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
        {
            return [];
        }

        var candidates = new List<AgentModelCandidate>();
        foreach (var configPath in Directory.EnumerateFiles(
                     rootDirectory,
                     "adapter_config.json",
                     SearchOption.AllDirectories))
        {
            var adapterPath = Path.GetDirectoryName(configPath)!;
            if (!HasAdapterWeights(adapterPath))
            {
                continue;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            var baseModel = document.RootElement.TryGetProperty(
                "base_model_name_or_path",
                out var baseModelElement)
                ? baseModelElement.GetString()
                : null;
            baseModel ??= ReadBaseModelFromRunConfiguration(adapterPath);
            if (string.IsNullOrWhiteSpace(baseModel))
            {
                continue;
            }

            var directoryName = Path.GetFileName(adapterPath);
            var step = ParseCheckpointStep(directoryName);
            var artifactType = step is null ? "Completed adapter" : "Training checkpoint";
            var savedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(configPath), TimeSpan.Zero);
            candidates.Add(new AgentModelCandidate(
                $"{artifactType}: {baseModel}" +
                (step is null ? string.Empty : $" (step {step:N0})"),
                baseModel,
                adapterPath,
                artifactType,
                step,
                savedAt,
                true));
        }

        AddRunningTrainingSessions(rootDirectory, candidates);
        return candidates
            .GroupBy(
                candidate => (candidate.BaseModel, candidate.AdapterPath, candidate.ArtifactType),
                StringTupleComparer.Instance)
            .Select(group => group.First())
            .OrderByDescending(candidate => candidate.ArtifactType == "Training in progress")
            .ThenByDescending(candidate => candidate.ArtifactType == "Completed adapter")
            .ThenByDescending(candidate => candidate.TrainingStep)
            .ThenByDescending(candidate => candidate.SavedAt)
            .ToList();
    }

    public ActiveAgentModel? GetActiveModel()
    {
        if (!File.Exists(ActiveModelPath))
        {
            return null;
        }

        return JsonSerializer.Deserialize<ActiveAgentModel>(
            File.ReadAllText(ActiveModelPath),
            JsonOptions);
    }

    public async Task<ActiveAgentModel> ActivateAsync(
        AgentModelCandidate candidate,
        string runtime,
        string? runtimeModelName = null,
        CancellationToken cancellationToken = default)
    {
        ValidateCandidate(candidate);
        if (runtime is not TransformersRuntime and not OllamaRuntime)
        {
            throw new ArgumentException($"Unsupported agent runtime: {runtime}", nameof(runtime));
        }

        if (runtime == OllamaRuntime && string.IsNullOrWhiteSpace(runtimeModelName))
        {
            throw new ArgumentException(
                "An Ollama model name is required for the Ollama runtime.",
                nameof(runtimeModelName));
        }

        var activeModel = new ActiveAgentModel(
            candidate.BaseModel,
            candidate.AdapterPath,
            runtime,
            runtimeModelName,
            timeProvider.GetUtcNow());
        Directory.CreateDirectory(ApplicationDataDirectory);
        var temporaryPath = $"{ActiveModelPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(activeModel, JsonOptions),
                cancellationToken);
            File.Move(temporaryPath, ActiveModelPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return activeModel;
    }

    public async Task<string> TestPromptAsync(
        ActiveAgentModel model,
        string prompt,
        string configuredPython,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        if (model.Runtime == OllamaRuntime)
        {
            var ollamaStartInfo = CreateStartInfo("ollama");
            ollamaStartInfo.ArgumentList.Add("run");
            ollamaStartInfo.ArgumentList.Add(model.RuntimeModelName!);
            ollamaStartInfo.ArgumentList.Add(prompt);
            var ollamaOutput = await ExecuteAsync(
                ollamaStartInfo,
                progress,
                cancellationToken);
            return ollamaOutput.Trim();
        }

        var candidate = new AgentModelCandidate(
            model.BaseModel,
            model.BaseModel,
            model.AdapterPath,
            "Active adapter",
            null,
            model.ActivatedAt,
            true);
        ValidateCandidate(candidate);
        var devices = await hardwareService.DetectAsync(cancellationToken);
        var accelerator = LocalModelTrainingService.SelectTrainingAccelerator(
                devices,
                allowCpuFallback: true)
            ?? throw new InvalidOperationException("No local inference device was detected.");
        var python = await LocalModelTrainingService.ResolvePythonExecutableAsync(
            configuredPython,
            accelerator.Kind,
            progress,
            cancellationToken);
        var scriptPath = await ExtractRuntimeAsync(cancellationToken);
        var startInfo = CreateStartInfo(python);
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("infer");
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(model.BaseModel);
        startInfo.ArgumentList.Add("--adapter");
        startInfo.ArgumentList.Add(model.AdapterPath);
        startInfo.ArgumentList.Add("--prompt");
        startInfo.ArgumentList.Add(prompt);
        startInfo.ArgumentList.Add("--device");
        startInfo.ArgumentList.Add(accelerator.Kind switch
        {
            AcceleratorKind.CudaGpu => "cuda",
            AcceleratorKind.DirectMlGpu => "directml",
            _ => "cpu"
        });
        var output = await ExecuteAsync(startInfo, progress, cancellationToken);
        var resultLine = output.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => line.StartsWith("TIS_AGENT_RESULT|", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "The local model completed without returning an inference result.");
        return JsonSerializer.Deserialize<string>(resultLine["TIS_AGENT_RESULT|".Length..])
            ?? string.Empty;
    }

    public async Task<ActiveAgentModel> CreateOllamaModelAsync(
        AgentModelCandidate candidate,
        string modelName,
        string configuredPython,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateCandidate(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        if (!OllamaModelNamePattern().IsMatch(modelName))
        {
            throw new ArgumentException(
                "Ollama model names must contain safe alphanumeric path segments separated by optional slashes.",
                nameof(modelName));
        }

        var python = await LocalModelTrainingService.ResolvePythonExecutableAsync(
            configuredPython,
            AcceleratorKind.Cpu,
            progress,
            cancellationToken);
        var scriptPath = await ExtractRuntimeAsync(cancellationToken);
        var exportDirectory = Path.Combine(
            Path.GetDirectoryName(candidate.AdapterPath)!,
            "ollama",
            modelName.Replace('/', '-'));
        var mergedDirectory = Path.Combine(exportDirectory, "merged");
        var mergeInfo = CreateStartInfo(python);
        mergeInfo.ArgumentList.Add(scriptPath);
        mergeInfo.ArgumentList.Add("merge");
        mergeInfo.ArgumentList.Add("--model");
        mergeInfo.ArgumentList.Add(candidate.BaseModel);
        mergeInfo.ArgumentList.Add("--adapter");
        mergeInfo.ArgumentList.Add(candidate.AdapterPath);
        mergeInfo.ArgumentList.Add("--output");
        mergeInfo.ArgumentList.Add(mergedDirectory);
        await ExecuteAsync(mergeInfo, progress, cancellationToken);

        Directory.CreateDirectory(exportDirectory);
        var modelfilePath = Path.Combine(exportDirectory, "Modelfile");
        await File.WriteAllTextAsync(
            modelfilePath,
            $"FROM \"{mergedDirectory}\"{Environment.NewLine}PARAMETER temperature 0{Environment.NewLine}",
            cancellationToken);
        var ollamaInfo = CreateStartInfo("ollama");
        ollamaInfo.ArgumentList.Add("create");
        ollamaInfo.ArgumentList.Add(modelName);
        ollamaInfo.ArgumentList.Add("--file");
        ollamaInfo.ArgumentList.Add(modelfilePath);
        await ExecuteAsync(ollamaInfo, progress, cancellationToken);
        return await ActivateAsync(
            candidate,
            OllamaRuntime,
            modelName,
            cancellationToken);
    }

    private static void ValidateCandidate(AgentModelCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.BaseModel);
        if (!Directory.Exists(candidate.AdapterPath) ||
            !File.Exists(Path.Combine(candidate.AdapterPath, "adapter_config.json")) ||
            !HasAdapterWeights(candidate.AdapterPath))
        {
            throw new DirectoryNotFoundException(
                $"The selected adapter is missing or incomplete: {candidate.AdapterPath}");
        }

        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(candidate.AdapterPath, "adapter_config.json")));
        if (document.RootElement.TryGetProperty("base_model_name_or_path", out var element) &&
            !string.IsNullOrWhiteSpace(element.GetString()) &&
            !string.Equals(
                element.GetString(),
                candidate.BaseModel,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The selected adapter does not match the selected base model.");
        }
    }

    private static bool HasAdapterWeights(string directory) =>
        File.Exists(Path.Combine(directory, "adapter_model.safetensors"));

    private static void AddRunningTrainingSessions(
        string rootDirectory,
        ICollection<AgentModelCandidate> candidates)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var rootPath = Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        using var searcher = new ManagementObjectSearcher(
            "SELECT CommandLine FROM Win32_Process WHERE Name = 'python.exe'");
        using var processes = searcher.Get();
        foreach (ManagementObject process in processes)
        {
            var commandLine = process["CommandLine"] as string;
            if (string.IsNullOrWhiteSpace(commandLine) ||
                !commandLine.Contains("local_lora_trainer.py", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var baseModel = ReadCommandLineArgument(commandLine, "model");
            var outputPath = ReadCommandLineArgument(commandLine, "output");
            if (string.IsNullOrWhiteSpace(baseModel) ||
                string.IsNullOrWhiteSpace(outputPath))
            {
                continue;
            }

            var fullOutputPath = Path.GetFullPath(outputPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!fullOutputPath.Equals(rootPath, StringComparison.OrdinalIgnoreCase) &&
                !fullOutputPath.StartsWith(
                    $"{rootPath}{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var savedAt = Directory.Exists(fullOutputPath)
                ? new DateTimeOffset(
                    Directory.GetLastWriteTimeUtc(fullOutputPath),
                    TimeSpan.Zero)
                : DateTimeOffset.UtcNow;
            candidates.Add(new AgentModelCandidate(
                $"Training in progress: {baseModel}",
                baseModel,
                fullOutputPath,
                "Training in progress",
                null,
                savedAt,
                false));
        }
    }

    private static string? ReadCommandLineArgument(string commandLine, string argument)
    {
        var match = Regex.Match(
            commandLine,
            $@"(?:^|\s)--{Regex.Escape(argument)}\s+(?:""(?<value>[^""]+)""|(?<value>\S+))",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static int? ParseCheckpointStep(string directoryName)
    {
        const string prefix = "checkpoint-";
        return directoryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(directoryName[prefix.Length..], out var step)
            ? step
            : null;
    }

    private static string? ReadBaseModelFromRunConfiguration(string adapterPath)
    {
        var directory = new DirectoryInfo(adapterPath);
        for (var index = 0; index < 2 && directory is not null; index++, directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "training_run.json");
            if (!File.Exists(path))
            {
                continue;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("model", out var model)
                ? model.GetString()
                : null;
        }

        return null;
    }

    private static ProcessStartInfo CreateStartInfo(string executable) => new()
    {
        FileName = executable,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    private static async Task<string> ExecuteAsync(
        ProcessStartInfo startInfo,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                lock (output)
                {
                    output.AppendLine(args.Data);
                }
                progress?.Report(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                lock (output)
                {
                    output.AppendLine($"stderr: {args.Data}");
                }
                progress?.Report($"stderr: {args.Data}");
            }
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"{startInfo.FileName} could not be started.");
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException(
                $"Unable to start {startInfo.FileName}. Ensure it is installed and available on PATH.",
                exception);
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
        var captured = output.ToString();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{startInfo.FileName} exited with code {process.ExitCode}.{Environment.NewLine}{captured.Trim()}");
        }

        return captured;
    }

    private static async Task<string> ExtractRuntimeAsync(CancellationToken cancellationToken)
    {
        var runtimeDirectory = Path.Combine(ApplicationDataDirectory, "Runtime");
        Directory.CreateDirectory(runtimeDirectory);
        var path = Path.Combine(runtimeDirectory, "agent_model_runtime.py");
        await using var resource = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(EmbeddedRuntimeName)
            ?? throw new InvalidOperationException("The embedded agent runtime was not found.");
        await using var output = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            useAsync: true);
        await resource.CopyToAsync(output, cancellationToken);
        return path;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*(/[A-Za-z0-9][A-Za-z0-9._-]*)*$")]
    private static partial Regex OllamaModelNamePattern();

    private sealed class StringTupleComparer :
        IEqualityComparer<(string BaseModel, string AdapterPath, string ArtifactType)>
    {
        public static StringTupleComparer Instance { get; } = new();

        public bool Equals(
            (string BaseModel, string AdapterPath, string ArtifactType) x,
            (string BaseModel, string AdapterPath, string ArtifactType) y) =>
            string.Equals(x.BaseModel, y.BaseModel, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.AdapterPath, y.AdapterPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.ArtifactType, y.ArtifactType, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(
            (string BaseModel, string AdapterPath, string ArtifactType) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.BaseModel),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.AdapterPath),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.ArtifactType));
    }
}
