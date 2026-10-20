using TaxIntelligenceStudio.Domain;

namespace TaxIntelligenceStudio.Application;

public interface IAuthoritativeSourceVerifier
{
    Task<VerifiedSource> VerifyAsync(
        AuthoritativeSource source,
        CancellationToken cancellationToken = default);
}

public interface IAuditRepository
{
    Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}

public interface IGuidanceService
{
    Task<GuidanceResponse> VerifyGuidanceAsync(
        GuidanceRequest request,
        CancellationToken cancellationToken = default);
}

public interface IIrsKnowledgeIngestionService
{
    Task<CrawlResult> CrawlAsync(
        int maximumDocuments,
        IProgress<CrawlProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IDatasetExportService
{
    Task<DatasetExportResult> ExportJsonLinesAsync(
        string outputPath,
        CancellationToken cancellationToken = default);

    Task<DatasetExportResult> ExportJsonLinesAsync(
        string outputPath,
        int datasetFileCount,
        CancellationToken cancellationToken = default);
}

public sealed record DatasetExportResult(
    int RecordCount,
    IReadOnlyList<string> UploadFiles,
    string? ManifestPath = null,
    string? Sha256 = null,
    DatasetTrainingSplitResult? TrainingSplits = null);

public sealed record DatasetTrainingSplitResult(
    string TrainingFile,
    string ValidationFile,
    string TestFile,
    string ExampleMetadataFile,
    string ManifestPath,
    int TrainingRecordCount,
    int ValidationRecordCount,
    int TestRecordCount,
    int DuplicateExcludedCount,
    int PiiExcludedCount,
    int UntrustedSourceExcludedCount,
    int Seed);

public interface IFinancialImportService
{
    Task<FinancialImportResult> ImportAsync(
        IReadOnlyCollection<string> filePaths,
        IProgress<FinancialImportProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IPdfOcrService
{
    Task<IReadOnlyCollection<OcrPageText>> RecognizeAsync(
        string filePath,
        IProgress<PdfOcrProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface ITaxAnalysisService
{
    Task<TaxAnalysisResult> AnalyzeAsync(
        IReadOnlyCollection<FinancialTransaction> transactions,
        IProgress<TaxAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface ITaxReportService
{
    Task ExportAsync(
        TaxAnalysisResult analysis,
        string outputPath,
        ReportFormat format,
        CancellationToken cancellationToken = default);
}

public interface IInferenceReportService
{
    Task ExportAsync(
        IReadOnlyList<ModelInferenceTurn> turns,
        string outputPath,
        ReportFormat format,
        CancellationToken cancellationToken = default);
}

public interface IInferencePromptHistory
{
    Task<IReadOnlyList<SavedInferencePrompt>> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(string prompt, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public interface IHardwareAccelerationService
{
    Task<IReadOnlyCollection<ComputeAccelerator>> DetectAsync(
        CancellationToken cancellationToken = default);
}

public interface IModelTrainingService
{
    IReadOnlyCollection<ModelTrainingCheckpoint> GetCheckpoints(string outputDirectory);

    Task<ModelTrainingResult> TrainAsync(
        ModelTrainingRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IModelCatalogService
{
    IReadOnlyCollection<string> GetFamilies();
    IReadOnlyCollection<BaseModelOption> GetModels(string family);
}

public interface IAcceleratorSelfTestService
{
    Task<AcceleratorSelfTestResult> RunAsync(
        string configuredPython,
        CancellationToken cancellationToken = default);
}

public interface IAgentModelService
{
    IReadOnlyCollection<AgentModelCandidate> DiscoverModels(string rootDirectory);
    ActiveAgentModel? GetActiveModel();
    Task<ActiveAgentModel> ActivateAsync(
        AgentModelCandidate candidate,
        string runtime,
        string? runtimeModelName = null,
        CancellationToken cancellationToken = default);
    Task<string> TestPromptAsync(
        ActiveAgentModel model,
        string prompt,
        string configuredPython,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
    Task<string> InferChatAsync(
        ActiveAgentModel model,
        ModelInferenceRequest request,
        string configuredPython,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null);
    Task<ActiveAgentModel> CreateOllamaModelAsync(
        AgentModelCandidate candidate,
        string modelName,
        string configuredPython,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IModelInferenceProvider
{
    string Id { get; }
    string DisplayName { get; }
    bool IsCloud { get; }
    string? ModelId { get; }

    Task<ModelProviderAvailability> CheckAvailabilityAsync(
        CancellationToken cancellationToken = default);

    Task<ModelInferenceResponse> InferAsync(
        ModelInferenceRequest request,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null);
}

public interface IModelProviderResolver
{
    Task<IReadOnlyCollection<ModelProviderAvailability>> GetAvailabilityAsync(
        CancellationToken cancellationToken = default);

    Task<IModelInferenceProvider> ResolveAsync(
        string? providerId = null,
        CancellationToken cancellationToken = default);
}

public interface IModelInferenceService
{
    Task<ModelInferenceResponse> InferAsync(
        ModelInferenceRequest request,
        string? providerId = null,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null);
}

public interface IKnowledgeContextSearchService
{
    Task<IReadOnlyList<RetrievedSourceContext>> SearchAsync(
        string query,
        int maximumDocuments,
        int maximumCharacters,
        CancellationToken cancellationToken = default);
}
