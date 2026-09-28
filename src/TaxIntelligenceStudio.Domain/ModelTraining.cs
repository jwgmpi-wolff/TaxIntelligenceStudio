namespace TaxIntelligenceStudio.Domain;

public enum AcceleratorKind
{
    Npu,
    CudaGpu,
    DirectMlGpu,
    Cpu
}

public sealed record ComputeAccelerator(
    string Name,
    AcceleratorKind Kind,
    bool SupportsTraining,
    bool SupportsInference,
    string Runtime);

public sealed record BaseModelOption(
    string Family,
    string ModelId,
    string DisplayName,
    decimal ParameterCountBillions,
    bool RecommendedForLaptopGpu);

public sealed record ModelTrainingRequest(
    string BaseModel,
    string DatasetPath,
    string OutputDirectory,
    string PythonExecutable,
    int Epochs,
    int BatchSize,
    decimal LearningRate,
    bool AllowCpuFallback,
    string? ResumeCheckpoint);

public sealed record ModelTrainingCheckpoint(
    string DisplayName,
    string? Path,
    int? Step,
    DateTimeOffset? SavedAt);

public sealed record ModelTrainingResult(
    bool Succeeded,
    string Accelerator,
    string OutputDirectory,
    string Log,
    DateTimeOffset CompletedAt);

public sealed record AcceleratorSelfTestResult(
    bool GpuVerified,
    bool NpuVerified,
    string GpuDevice,
    string NpuProvider,
    string Details);

public sealed record AgentModelCandidate(
    string DisplayName,
    string BaseModel,
    string AdapterPath,
    string ArtifactType,
    int? TrainingStep,
    DateTimeOffset SavedAt,
    bool CanActivate);

public sealed record ActiveAgentModel(
    string BaseModel,
    string AdapterPath,
    string Runtime,
    string? RuntimeModelName,
    DateTimeOffset ActivatedAt);
