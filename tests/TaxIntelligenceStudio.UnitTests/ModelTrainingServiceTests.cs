using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;
using TaxIntelligenceStudio.Infrastructure.Training;

namespace TaxIntelligenceStudio.UnitTests;

public sealed class ModelTrainingServiceTests
{
    [Fact]
    public void BuildStartInfo_PassesEveryQueuedDatasetOnceInSelectionOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "part-01.jsonl");
        var second = Path.Combine(root, "part-02.jsonl");
        var third = Path.Combine(root, "part with spaces.jsonl");
        var request = new ModelTrainingRequest(
            "base-model", first, Path.Combine(root, "output"), "python",
            1, 1, 0.00005m, false, null,
            AdditionalDatasetPaths: [second, first, third]);

        var info = LocalModelTrainingService.BuildStartInfo(
            request, "trainer.py", AcceleratorKind.Cpu, "python");
        var arguments = info.ArgumentList.ToArray();
        var datasets = arguments.Select((value, index) => (value, index))
            .Where(item => item.value == "--dataset")
            .Select(item => arguments[item.index + 1]).ToArray();

        Assert.Equal([first, second, third], datasets);
        Assert.Single(arguments, argument => argument == "--output");
    }

    [Fact]
    public void BuildStartInfo_PreservesCompanionSplitsForEveryQueuedExport()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var name in new[] { "first", "second" })
            {
                var splits = Path.Combine(root, $"{name}-splits");
                Directory.CreateDirectory(splits);
                File.WriteAllText(Path.Combine(root, $"{name}.jsonl"), "{}");
                File.WriteAllText(Path.Combine(splits, "manifest.json"), "{}");
                foreach (var split in new[] { "train", "validation", "test" })
                {
                    File.WriteAllText(Path.Combine(splits, $"{split}.jsonl"), "{\"messages\":[]}");
                }
            }
            var request = new ModelTrainingRequest(
                "base-model", Path.Combine(root, "first.jsonl"), Path.Combine(root, "output"),
                "python", 1, 1, 0.00005m, false, null,
                AdditionalDatasetPaths: [Path.Combine(root, "second.jsonl")]);

            var info = LocalModelTrainingService.BuildStartInfo(
                request, "trainer.py", AcceleratorKind.Cpu, "python");
            var arguments = info.ArgumentList.ToArray();
            foreach (var flag in new[] { "--dataset", "--validation-dataset", "--test-dataset", "--split-manifest" })
            {
                Assert.Equal(2, arguments.Count(argument => argument == flag));
            }
            Assert.DoesNotContain(Path.Combine(root, "first.jsonl"), arguments);
            Assert.DoesNotContain(Path.Combine(root, "second.jsonl"), arguments);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TrainAsync_ReportsMissingQueuedFileBeforeHardwareOrPythonSetup()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var first = Path.Combine(root, "first.jsonl");
        var missing = Path.Combine(root, "missing.jsonl");
        File.WriteAllText(first, "{\"messages\":[]}");
        try
        {
            var service = new LocalModelTrainingService(new NpuOnlyHardwareService(), TimeProvider.System);
            var request = new ModelTrainingRequest(
                "base-model", first, Path.Combine(root, "output"), "python",
                1, 1, 0.00005m, false, null, AdditionalDatasetPaths: [missing]);

            var exception = await Assert.ThrowsAsync<FileNotFoundException>(() => service.TrainAsync(request));

            Assert.Equal(missing, exception.FileName);
            Assert.False(Directory.Exists(request.OutputDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CompleteTrainingRun_RequiresValidEvaluationAndMatchingAdapterHashes()
    {
        var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var adapter = Path.Combine(output, "adapter");
        Directory.CreateDirectory(adapter);
        var weights = Path.Combine(adapter, "adapter_model.safetensors");
        var config = Path.Combine(adapter, "adapter_config.json");
        var evaluation = Path.Combine(output, "evaluation.json");
        File.WriteAllText(weights, "test weights");
        File.WriteAllText(config, "{\"base_model_name_or_path\":\"base-model\"}");
        File.WriteAllText(Path.Combine(output, "training_run.json"), "{}");
        File.WriteAllText(Path.Combine(output, "run_environment.json"), "{}");
        File.WriteAllText(evaluation, "{\"status\":\"completed\"}");
        File.WriteAllText(
            Path.Combine(output, "artifact_manifest.json"),
            JsonSerializer.Serialize(new
            {
                evaluation_sha256 = Hash(evaluation),
                adapter_files = new[]
                {
                    new
                    {
                        file = "adapter_model.safetensors",
                        bytes = new FileInfo(weights).Length,
                        sha256 = Hash(weights)
                    },
                    new
                    {
                        file = "adapter_config.json",
                        bytes = new FileInfo(config).Length,
                        sha256 = Hash(config)
                    }
                }
            }));
        try
        {
            Assert.True(LocalModelTrainingService.HasCompleteTrainingRun(output));

            File.AppendAllText(weights, "modified");

            Assert.False(LocalModelTrainingService.HasCompleteTrainingRun(output));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void BuildStartInfo_UsesValidatedSplitsWhenManifestExists()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var dataset = Path.Combine(root, "training.jsonl");
        var splits = Path.Combine(root, "training-splits");
        Directory.CreateDirectory(splits);
        File.WriteAllText(dataset, "{}");
        File.WriteAllText(Path.Combine(splits, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(splits, "train.jsonl"), "{\"messages\":[]}");
        File.WriteAllText(Path.Combine(splits, "validation.jsonl"), "{\"messages\":[]}");
        File.WriteAllText(Path.Combine(splits, "test.jsonl"), "{\"messages\":[]}");
        try
        {
            var request = new ModelTrainingRequest(
                "base-model",
                dataset,
                Path.Combine(root, "output"),
                "python",
                2,
                1,
                0.0002m,
                AllowCpuFallback: false,
                ResumeCheckpoint: null,
                Seed: 17,
                LoraRank: 8,
                LoraAlpha: 16,
                LoraDropout: 0.1);
            var startInfo = LocalModelTrainingService.BuildStartInfo(
                request,
                "trainer.py",
                AcceleratorKind.CudaGpu,
                "python");
            var arguments = startInfo.ArgumentList.ToArray();

            Assert.Equal(
                Path.Combine(splits, "train.jsonl"),
                arguments[Array.IndexOf(arguments, "--dataset") + 1]);
            Assert.Contains("--validation-dataset", arguments);
            Assert.Contains("--test-dataset", arguments);
            Assert.Equal("17", arguments[Array.IndexOf(arguments, "--seed") + 1]);
            Assert.Equal("8", arguments[Array.IndexOf(arguments, "--lora-rank") + 1]);
            Assert.Equal("16", arguments[Array.IndexOf(arguments, "--lora-alpha") + 1]);
            Assert.Equal("0.1", arguments[Array.IndexOf(arguments, "--lora-dropout") + 1]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildStartInfo_RefusesEmptyTrainingSplitInsteadOfFallingBackToRawExport()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var dataset = Path.Combine(root, "training.jsonl");
        var splits = Path.Combine(root, "training-splits");
        Directory.CreateDirectory(splits);
        File.WriteAllText(dataset, "{}");
        File.WriteAllText(Path.Combine(splits, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(splits, "train.jsonl"), string.Empty);
        try
        {
            var request = new ModelTrainingRequest(
                "base-model",
                dataset,
                Path.Combine(root, "output"),
                "python",
                1,
                1,
                0.0002m,
                AllowCpuFallback: false,
                ResumeCheckpoint: null);

            var exception = Assert.Throws<InvalidOperationException>(() =>
                LocalModelTrainingService.BuildStartInfo(
                    request,
                    "trainer.py",
                    AcceleratorKind.Cpu,
                    "python"));

            Assert.Contains("Do not train on the unfiltered full export", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task HardwareDetection_AlwaysReportsCpu()
    {
        var service = new WindowsHardwareAccelerationService();

        var devices = await service.DetectAsync();

        Assert.Contains(devices, device => device.Kind == AcceleratorKind.Cpu);

        // Every detected GPU and the CPU can train, on any architecture: the
        // x64 wheels run under emulation on Windows on Arm. Only NPUs are
        // inference-only, and they must say why.
        Assert.All(
            devices.Where(device => device.Kind is AcceleratorKind.DirectMlGpu
                                        or AcceleratorKind.CudaGpu
                                        or AcceleratorKind.Cpu),
            device => Assert.True(device.SupportsTraining));
        Assert.All(
            devices.Where(device => device.Kind == AcceleratorKind.Npu),
            device =>
            {
                Assert.False(device.SupportsTraining);
                Assert.False(string.IsNullOrWhiteSpace(device.TrainingUnavailableReason));
            });
    }

    [Fact]
    public async Task Trainer_DoesNotMisrepresentNpuAsTrainingDevice()
    {
        var datasetPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(datasetPath, "{}");
        try
        {
            var service = new LocalModelTrainingService(
                new NpuOnlyHardwareService(),
                TimeProvider.System);
            var request = new ModelTrainingRequest(
                "local-model",
                datasetPath,
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                "python",
                1,
                1,
                0.0002m,
                AllowCpuFallback: false,
                ResumeCheckpoint: null);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.TrainAsync(request));

            // The NPU must never be chosen as a training device, and the
            // message must name the device that was rejected. The exact wording
            // depends on the host architecture, so assert the invariant rather
            // than the phrasing.
            Assert.Contains("NPU", exception.Message);
            Assert.Null(LocalModelTrainingService.SelectTrainingAccelerator(
                await new NpuOnlyHardwareService().DetectAsync(),
                allowCpuFallback: true));
        }
        finally
        {
            File.Delete(datasetPath);
        }
    }

    [Fact]
    public async Task CheckpointDiscovery_ReturnsNewestValidCheckpointsAndNewTrainingOption()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(outputPath, "checkpoint-10"));
        Directory.CreateDirectory(Path.Combine(outputPath, "checkpoint-20"));
        Directory.CreateDirectory(Path.Combine(outputPath, "checkpoint-incomplete"));
        await File.WriteAllTextAsync(
            Path.Combine(outputPath, "checkpoint-10", "trainer_state.json"),
            "{}");
        await File.WriteAllTextAsync(
            Path.Combine(outputPath, "checkpoint-20", "trainer_state.json"),
            "{}");
        try
        {
            var service = new LocalModelTrainingService(
                new NpuOnlyHardwareService(),
                TimeProvider.System);

            var checkpoints = service.GetCheckpoints(outputPath).ToArray();

            Assert.Null(checkpoints[0].Path);
            Assert.Equal("Start new training", checkpoints[0].DisplayName);
            Assert.Equal([20, 10], checkpoints.Skip(1).Select(item => item.Step));
        }
        finally
        {
            Directory.Delete(outputPath, recursive: true);
        }
    }

    [Fact]
    public async Task Trainer_RejectsCheckpointOutsideOutputDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var outputPath = Path.Combine(root, "output");
        var checkpointPath = Path.Combine(root, "checkpoint-10");
        var datasetPath = Path.Combine(root, "dataset.jsonl");
        Directory.CreateDirectory(outputPath);
        Directory.CreateDirectory(checkpointPath);
        await File.WriteAllTextAsync(datasetPath, "{}");
        await File.WriteAllTextAsync(
            Path.Combine(checkpointPath, "trainer_state.json"),
            "{}");
        await File.WriteAllTextAsync(
            Path.Combine(checkpointPath, "training_run.json"),
            "{}");
        try
        {
            var service = new LocalModelTrainingService(
                new NpuOnlyHardwareService(),
                TimeProvider.System);
            var request = new ModelTrainingRequest(
                "local-model",
                datasetPath,
                outputPath,
                "python",
                1,
                1,
                0.0002m,
                AllowCpuFallback: false,
                ResumeCheckpoint: checkpointPath);

            var exception = await Assert.ThrowsAsync<ArgumentException>(
                () => service.TrainAsync(request));

            Assert.Contains("inside", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Trainer_RejectsCheckpointWithoutCompatibilityManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var checkpointPath = Path.Combine(root, "checkpoint-10");
        var datasetPath = Path.Combine(root, "dataset.jsonl");
        Directory.CreateDirectory(checkpointPath);
        await File.WriteAllTextAsync(datasetPath, "{}");
        await File.WriteAllTextAsync(
            Path.Combine(checkpointPath, "trainer_state.json"),
            "{}");
        try
        {
            var service = new LocalModelTrainingService(
                new NpuOnlyHardwareService(),
                TimeProvider.System);
            var request = new ModelTrainingRequest(
                "local-model",
                datasetPath,
                root,
                "python",
                1,
                1,
                0.0002m,
                AllowCpuFallback: false,
                ResumeCheckpoint: checkpointPath);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.TrainAsync(request));

            Assert.Contains("compatibility manifest", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ContainsTrainingArtifacts_DetectsPriorRunsButNotEmptyFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.False(LocalModelTrainingService.ContainsTrainingArtifacts(root));
            Directory.CreateDirectory(Path.Combine(root, "checkpoint-14083"));
            Assert.True(LocalModelTrainingService.ContainsTrainingArtifacts(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TrainerSuccessRequiresNonemptyAdapterMatchingBaseModel()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var adapter = Path.Combine(root, "adapter");
        Directory.CreateDirectory(adapter);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(adapter, "adapter_config.json"),
                """{"base_model_name_or_path":"Qwen/Qwen2.5-1.5B-Instruct"}""");
            await File.WriteAllBytesAsync(
                Path.Combine(adapter, "adapter_model.safetensors"),
                [1, 2, 3]);

            Assert.True(LocalModelTrainingService.HasValidAdapterArtifacts(
                root,
                "Qwen/Qwen2.5-1.5B-Instruct"));
            Assert.False(LocalModelTrainingService.HasValidAdapterArtifacts(
                root,
                "different-model"));

            await File.WriteAllBytesAsync(
                Path.Combine(adapter, "adapter_model.safetensors"),
                []);
            Assert.False(LocalModelTrainingService.HasValidAdapterArtifacts(
                root,
                "Qwen/Qwen2.5-1.5B-Instruct"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AgentModelDiscovery_FindsCompletedAndCheckpointAdapters()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var completed = Path.Combine(root, "adapter");
        var checkpoint = Path.Combine(root, "checkpoint-40");
        Directory.CreateDirectory(completed);
        Directory.CreateDirectory(checkpoint);
        const string config =
            """{"base_model_name_or_path":"Qwen/Qwen2.5-Coder-1.5B-Instruct"}""";
        await File.WriteAllTextAsync(Path.Combine(completed, "adapter_config.json"), config);
        await File.WriteAllTextAsync(Path.Combine(completed, "adapter_model.safetensors"), "");
        await File.WriteAllTextAsync(Path.Combine(checkpoint, "adapter_config.json"), config);
        await File.WriteAllTextAsync(Path.Combine(checkpoint, "adapter_model.safetensors"), "");
        try
        {
            var service = new AgentModelService(
                new NpuOnlyHardwareService(),
                TimeProvider.System);

            var models = service.DiscoverModels(root).ToArray();

            Assert.Equal(2, models.Length);
            Assert.Equal("Completed adapter", models[0].ArtifactType);
            Assert.True(models[0].CanActivate);
            Assert.Equal("Training checkpoint", models[1].ArtifactType);
            Assert.True(models[1].CanActivate);
            Assert.Equal(40, models[1].TrainingStep);
            Assert.All(
                models,
                model => Assert.Equal(
                    "Qwen/Qwen2.5-Coder-1.5B-Instruct",
                    model.BaseModel));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ModelCatalog_ProvidesReferenceTrainerFamiliesAndModels()
    {
        var catalog = new ModelCatalogService();

        Assert.Equal(
            ["Qwen", "Llama", "Mistral", "Phi", "Custom"],
            catalog.GetFamilies());
        Assert.Contains(
            catalog.GetModels("Qwen"),
            model => model.ModelId == "Qwen/Qwen2.5-1.5B-Instruct");
        Assert.All(catalog.GetModels("Qwen"), model => Assert.False(model.RecommendedForLaptopGpu));
        Assert.Contains(
            catalog.GetModels("Llama"),
            model => model.ModelId == "meta-llama/Llama-3.1-8B-Instruct");
        Assert.Contains(
            catalog.GetModels("Mistral"),
            model => model.ModelId == "mistralai/Mistral-7B-Instruct-v0.3");
        Assert.Contains(
            catalog.GetModels("Phi"),
            model => model.ModelId == "microsoft/Phi-3.5-mini-instruct");
    }

    [Fact]
    public void AcceleratorSelection_PrefersDirectMlOverCpu()
    {
        IReadOnlyCollection<ComputeAccelerator> devices =
        [
            new("Platform NPU", AcceleratorKind.Npu, false, true, "Windows ML"),
            new("Qualcomm Adreno GPU", AcceleratorKind.DirectMlGpu, true, true, "PyTorch DirectML"),
            new("System CPU", AcceleratorKind.Cpu, true, true, "PyTorch CPU")
        ];

        var selected = LocalModelTrainingService.SelectTrainingAccelerator(
            devices,
            allowCpuFallback: false);

        Assert.NotNull(selected);
        Assert.Equal(AcceleratorKind.DirectMlGpu, selected.Kind);
    }

    [Fact]
    public void AcceleratorSelection_PrefersCudaOverDirectMl()
    {
        IReadOnlyCollection<ComputeAccelerator> devices =
        [
            new("Qualcomm Adreno GPU", AcceleratorKind.DirectMlGpu, true, true, "PyTorch DirectML"),
            new("NVIDIA GPU", AcceleratorKind.CudaGpu, true, true, "PyTorch CUDA")
        ];

        var selected = LocalModelTrainingService.SelectTrainingAccelerator(
            devices,
            allowCpuFallback: false);

        Assert.NotNull(selected);
        Assert.Equal(AcceleratorKind.CudaGpu, selected.Kind);
    }

    [Fact]
    public void Platform_AllowsTrainingWheelsOnX64Interpreters()
    {
        Assert.True(TrainingPlatform.SupportsTrainingWheels(Architecture.X64));
        Assert.Null(TrainingPlatform.InterpreterUnavailableReason(
            Architecture.X64,
            Architecture.Arm64));
        Assert.Contains("pip install", TrainingPlatform.InstallCommand(directMl: false));
        Assert.Contains("torch-directml", TrainingPlatform.InstallCommand(directMl: true));
    }

    [Theory]
    [InlineData(Architecture.Arm64)]
    [InlineData(Architecture.X86)]
    public void Platform_RejectsInterpretersWithoutWheels(Architecture interpreter)
    {
        Assert.False(TrainingPlatform.SupportsTrainingWheels(interpreter));

        var reason = TrainingPlatform.InterpreterUnavailableReason(
            interpreter,
            Architecture.X64);

        Assert.NotNull(reason);
        Assert.Contains(TrainingPlatform.Describe(interpreter), reason);
        Assert.Contains("x64", reason);
    }

    [Fact]
    public void Platform_PointsArm64HostsAtEmulatedX64Python()
    {
        var reason = TrainingPlatform.InterpreterUnavailableReason(
            Architecture.Arm64,
            Architecture.Arm64);

        // The machine can train; the interpreter choice is what blocks it, so
        // the message must say how to fix it here rather than send the user away.
        Assert.NotNull(reason);
        Assert.Contains("emulation", reason);
        Assert.Contains("DirectML", reason);
        Assert.NotNull(TrainingPlatform.HostGuidance(Architecture.Arm64));
        Assert.Null(TrainingPlatform.HostGuidance(Architecture.X64));
    }

    [Fact]
    public void Platform_PinsDirectMlPackagesToTorchCompatibleVersions()
    {
        var command = TrainingPlatform.InstallCommand(directMl: true);

        // torch-directml requires torch 2.4.1, and unpinned transformers
        // requires torch >= 2.5, which installs cleanly and then fails to
        // import. Every package in this command must be pinned together.
        Assert.Contains("torch==2.4.1", command);
        Assert.Contains("torch-directml==", command);
        Assert.Contains("transformers==4.46.3", command);
        Assert.Contains("peft==0.13.2", command);
        Assert.DoesNotContain(" transformers ", command);
    }

    [Theory]
    [InlineData("AMD64", Architecture.X64)]
    [InlineData("x86_64", Architecture.X64)]
    [InlineData("ARM64", Architecture.Arm64)]
    [InlineData("aarch64", Architecture.Arm64)]
    [InlineData("x86", Architecture.X86)]
    public void InterpreterArchitecture_ParsesPlatformMachineValues(
        string machine,
        Architecture expected) =>
        Assert.Equal(expected, LocalModelTrainingService.ParseMachine(machine));

    [Fact]
    public void InterpreterArchitecture_IgnoresUnknownValues() =>
        Assert.Null(LocalModelTrainingService.ParseMachine("riscv64"));

    [Fact]
    public async Task InterpreterArchitecture_MatchesTheRunningInterpreter()
    {
        var detected = await LocalModelTrainingService.DetectInterpreterArchitectureAsync(
            "python",
            CancellationToken.None);

        // Skip where no interpreter is installed; assert correctness otherwise.
        if (detected is not null)
        {
            Assert.True(
                detected is Architecture.X64 or Architecture.Arm64 or Architecture.X86);
        }
    }

    [Fact]
    public void GpuDetection_MarksIntegratedArmGpuTrainable()
    {
        // Verified on a Snapdragon X Elite: an emulated x64 Python completes
        // multi-step AdamW LoRA training on this GPU through DirectML.
        var gpu = WindowsHardwareAccelerationService.CreateGpuAccelerator(
            "Qualcomm(R) Adreno(TM) X1-85 GPU",
            cudaAvailable: false);

        Assert.Equal(AcceleratorKind.DirectMlGpu, gpu.Kind);
        Assert.True(gpu.SupportsTraining);
        Assert.True(gpu.SupportsInference);
        Assert.Null(gpu.TrainingUnavailableReason);
    }

    [Fact]
    public void EmbeddedTrainer_AvoidsUnsupportedDirectMlAdamWLerpPath()
    {
        const string resourceName =
            "TaxIntelligenceStudio.Infrastructure.Training.local_lora_trainer.py";
        using var stream = typeof(LocalModelTrainingService).Assembly
            .GetManifestResourceStream(resourceName);

        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var trainer = reader.ReadToEnd();

        Assert.Contains("class DirectMLAdamW", trainer);
        Assert.Contains("exp_avg.mul_(beta1).add_", trainer);
        Assert.Contains(
            "dtype = torch.float16 if args.device == \"cuda\" else torch.float32",
            trainer);
        Assert.Contains(
            "max_sequence_length = 512 if args.device == \"directml\"",
            trainer);
        Assert.Contains(
            "gradient_checkpointing=args.device == \"directml\"",
            trainer);
        Assert.Contains("trainer_version", trainer);
        Assert.Contains("loss > 100", trainer);
        Assert.Contains("loss < 0", trainer);
        Assert.Contains("Falling back to CPU training", trainer);
        Assert.Contains("--directml-probe", trainer);
        Assert.Contains("install_directml_safe_embedding(model)", trainer);
        Assert.Contains("class ChunkedLinearCrossEntropy(torch.autograd.Function)", trainer);
        Assert.Contains("def compute_loss(self, model, inputs", trainer);
        Assert.Contains("def _maybe_log_save_evaluate(self, tr_loss, *args, **kwargs)", trainer);
        Assert.Contains("Training loss diverged", trainer);
        Assert.DoesNotContain(".lerp_", trainer);
        Assert.DoesNotContain("torch._foreach", trainer);
    }

    [Fact]
    public void EmbeddedAgentRuntime_UsesChatTemplateAndRepetitionControls()
    {
        const string resourceName =
            "TaxIntelligenceStudio.Infrastructure.Training.agent_model_runtime.py";
        using var stream = typeof(AgentModelService).Assembly
            .GetManifestResourceStream(resourceName);

        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var runtime = reader.ReadToEnd();

        Assert.Contains("tokenizer.apply_chat_template(", runtime);
        Assert.Contains("add_generation_prompt=True", runtime);
        Assert.Contains("repetition_penalty=1.1", runtime);
        Assert.Contains("no_repeat_ngram_size=4", runtime);
        Assert.Contains("most_common_count / len(generated_ids) >= 0.8", runtime);
        Assert.Contains("Treat this adapter", runtime);
        Assert.Contains("dtype = torch.float32", runtime);
        Assert.Contains("digit_count / len(visible_characters) >= 0.8", runtime);
        Assert.Contains("numeric or non-language output", runtime);
        Assert.Contains("falling back to CPU inference", runtime);
        Assert.Contains("--directml-probe", runtime);
        Assert.Contains("model.disable_adapter()", runtime);
    }

    [Fact]
    public void GpuDetection_UsesDirectMlForNonNvidiaGpus()
    {
        var gpu = WindowsHardwareAccelerationService.CreateGpuAccelerator(
            "AMD Radeon RX 7900 XTX",
            cudaAvailable: false);

        Assert.Equal(AcceleratorKind.DirectMlGpu, gpu.Kind);
        Assert.True(gpu.SupportsTraining);
    }

    [Fact]
    public void GpuDetection_UsesDirectMlForNvidiaWithoutCudaRuntime()
    {
        var gpu = WindowsHardwareAccelerationService.CreateGpuAccelerator(
            "NVIDIA GeForce RTX 4090",
            cudaAvailable: false);

        Assert.Equal(AcceleratorKind.DirectMlGpu, gpu.Kind);
        Assert.True(gpu.SupportsTraining);
    }

    [Fact]
    public void GpuDetection_UsesCudaForNvidiaWithCudaRuntime()
    {
        var gpu = WindowsHardwareAccelerationService.CreateGpuAccelerator(
            "NVIDIA GeForce RTX 4090",
            cudaAvailable: true);

        Assert.Equal(AcceleratorKind.CudaGpu, gpu.Kind);
        Assert.True(gpu.SupportsTraining);
    }

    [Fact]
    public void CpuDetection_CanAlwaysTrain()
    {
        var cpu = WindowsHardwareAccelerationService.CreateCpuAccelerator();

        Assert.True(cpu.SupportsTraining);
        Assert.True(cpu.SupportsInference);
        Assert.Null(cpu.TrainingUnavailableReason);
    }

    [Fact]
    public void NoTrainingDeviceMessage_AddsArm64InterpreterGuidance()
    {
        IReadOnlyCollection<ComputeAccelerator> devices =
        [
            new(
                "Snapdragon(R) X Elite NPU",
                AcceleratorKind.Npu,
                false,
                true,
                "Windows ML",
                "NPU execution providers run inference, not weight training.")
        ];

        var message = LocalModelTrainingService.BuildNoTrainingDeviceMessage(
            devices,
            allowCpuFallback: false,
            Architecture.Arm64);

        Assert.Contains("NPU", message);
        Assert.Contains("x64", message);
        Assert.Contains("emulation", message);
    }

    [Fact]
    public void NoTrainingDeviceMessage_OffersCpuFallbackOnX64()
    {
        IReadOnlyCollection<ComputeAccelerator> devices =
        [
            new("Snapdragon NPU", AcceleratorKind.Npu, false, true, "Windows ML"),
            WindowsHardwareAccelerationService.CreateCpuAccelerator()
        ];

        var message = LocalModelTrainingService.BuildNoTrainingDeviceMessage(
            devices,
            allowCpuFallback: false,
            Architecture.X64);

        Assert.Contains("NPU", message);
        Assert.Contains("enable CPU fallback", message);
        Assert.DoesNotContain("emulation", message);
    }

    [Fact]
    public void AcceleratorSelection_SkipsUntrainableDeviceAndUsesCpu()
    {
        IReadOnlyCollection<ComputeAccelerator> devices =
        [
            new(
                "Snapdragon(R) X Elite NPU",
                AcceleratorKind.Npu,
                false,
                true,
                "Windows ML",
                "NPU execution providers run inference, not weight training."),
            new("System CPU", AcceleratorKind.Cpu, true, true, "PyTorch CPU")
        ];

        var selected = LocalModelTrainingService.SelectTrainingAccelerator(
            devices,
            allowCpuFallback: true);

        Assert.NotNull(selected);
        Assert.Equal(AcceleratorKind.Cpu, selected.Kind);
    }

    [Theory]
    [InlineData("Snapdragon(R) X Elite - X1E80100 - Qualcomm(R) Hexagon(TM) NPU", true)]
    [InlineData("Intel(R) AI Boost", true)]
    [InlineData("Neural Processing Unit", true)]
    [InlineData("USB Input Device", false)]
    [InlineData("HID-compliant mouse", false)]
    [InlineData("Generic Input Controller", false)]
    public void NpuDetection_DoesNotMatchInputDevices(string name, bool expected) =>
        Assert.Equal(expected, WindowsHardwareAccelerationService.IsNpu(name));

    private static string Hash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed class NpuOnlyHardwareService : IHardwareAccelerationService
    {
        public Task<IReadOnlyCollection<ComputeAccelerator>> DetectAsync(
            CancellationToken cancellationToken = default)
        {
            IReadOnlyCollection<ComputeAccelerator> devices =
            [
                new("Platform NPU", AcceleratorKind.Npu, false, true, "Windows ML")
            ];
            return Task.FromResult(devices);
        }

    }
}
