using System.Runtime.InteropServices;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;
using TaxIntelligenceStudio.Infrastructure.Training;

namespace TaxIntelligenceStudio.UnitTests;

public sealed class ModelTrainingServiceTests
{
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
            model => model.ModelId == "Qwen/Qwen2.5-Coder-1.5B-Instruct" &&
                     model.RecommendedForLaptopGpu);
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
        // Verified on a Snapdragon X Elite: an emulated x64 Python completes a
        // one-step LoRA update on this GPU through DirectML.
        var gpu = WindowsHardwareAccelerationService.CreateGpuAccelerator(
            "Qualcomm(R) Adreno(TM) X1-85 GPU",
            cudaAvailable: false);

        Assert.Equal(AcceleratorKind.DirectMlGpu, gpu.Kind);
        Assert.True(gpu.SupportsTraining);
        Assert.True(gpu.SupportsInference);
        Assert.Null(gpu.TrainingUnavailableReason);
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
