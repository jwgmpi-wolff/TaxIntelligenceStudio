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
    Task<int> ExportJsonLinesAsync(
        string outputPath,
        CancellationToken cancellationToken = default);
}

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
    Task<ActiveAgentModel> CreateOllamaModelAsync(
        AgentModelCandidate candidate,
        string modelName,
        string configuredPython,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}
