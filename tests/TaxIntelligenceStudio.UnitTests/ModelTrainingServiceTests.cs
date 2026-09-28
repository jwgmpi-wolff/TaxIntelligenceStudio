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
        Assert.All(
            devices.Where(device => device.Kind == AcceleratorKind.DirectMlGpu),
            device => Assert.True(device.SupportsTraining));
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

            Assert.Contains("NPU", exception.Message);
            Assert.Contains("inference", exception.Message);
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
