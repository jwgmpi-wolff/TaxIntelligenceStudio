using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;
using TaxIntelligenceStudio.Infrastructure.Training;
using TaxIntelligenceStudio.Infrastructure.Inference;

namespace TaxIntelligenceStudio.Desktop.ViewModels;

public partial class MainViewModel(
    IIrsKnowledgeIngestionService ingestionService,
    IDatasetExportService datasetExportService,
    IFinancialImportService financialImportService,
    ITaxAnalysisService taxAnalysisService,
    ITaxReportService taxReportService,
    IHardwareAccelerationService hardwareAccelerationService,
    IModelTrainingService modelTrainingService,
    IModelCatalogService modelCatalogService,
    IAcceleratorSelfTestService acceleratorSelfTestService,
    IAgentModelService agentModelService,
    IModelInferenceService modelInferenceService,
    IModelProviderResolver modelProviderResolver,
    IInferenceAttachmentService inferenceAttachments,
    IInferenceReportService inferenceReports,
    IInferencePromptHistory promptHistory,
    ITrainingDatasetHistory trainingHistory,
    ModelProviderOptions modelProviderOptions) : ObservableObject
{
    private TaxAnalysisResult? _analysis;
    private readonly List<ModelChatMessage> _modelInferenceConversation = [];
    private readonly List<ModelInferenceTurn> _modelInferenceTurns = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    [NotifyPropertyChangedFor(nameof(CanEditTraining))]
    [NotifyCanExecuteChangedFor(nameof(CrawlCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportDatasetCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportPdfCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportWordCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunAcceleratorSelfTestCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCheckpointsCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscoverAgentModelsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ActivateAgentModelCommand))]
    [NotifyCanExecuteChangedFor(nameof(TestAgentModelCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshModelProvidersCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunModelInferenceCommand))]
    [NotifyCanExecuteChangedFor(nameof(NewModelConversationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChooseTrainingDatasetCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveTrainingDatasetCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearTrainingDatasetsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChooseTrainingOutputCommand))]
    [NotifyCanExecuteChangedFor(nameof(TrainModelCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddInferenceAttachmentsCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveInferenceAttachmentCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearInferenceAttachmentsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportInferencePdfCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportInferenceWordCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadPromptHistoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReusePromptCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeletePromptCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearPromptHistoryCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private int _maximumDocuments = 100;

    [ObservableProperty]
    private string _datasetFileCount = "0";

    [ObservableProperty]
    private string _knowledgeStatus =
        "Ready. Set Maximum documents to 0 to crawl every discoverable IRS sitemap URL.";

    [ObservableProperty]
    private string _financialStatus = "Import PDF, CSV, XLSX, or TXT financial exports.";

    [ObservableProperty]
    private double _financialProgress;

    [ObservableProperty]
    private string _financialProgressText = "No import running";

    [ObservableProperty]
    private double _analysisProgress;

    [ObservableProperty]
    private string _analysisProgressText = "No analysis running";

    [ObservableProperty]
    private string _reportStatus = "Analyze imported records before exporting a report.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveTrainingDatasetCommand))]
    private string? _selectedTrainingDataset;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TrainModelCommand))]
    private string _baseModel = "Qwen/Qwen2.5-1.5B-Instruct";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableBaseModels))]
    private string _selectedModelFamily = "Qwen";

    [ObservableProperty]
    private string _pythonExecutable = "python";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TrainModelCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCheckpointsCommand))]
    private string _trainingOutputDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaxIntelligenceStudio",
        "Models");

    private static readonly ModelTrainingCheckpoint NewTrainingCheckpoint =
        new("Start new training", null, null, null);

    [ObservableProperty]
    private ModelTrainingCheckpoint? _selectedCheckpoint = NewTrainingCheckpoint;

    [ObservableProperty]
    private bool _allowCpuFallback;

    [ObservableProperty]
    private string _trainingStatus =
        "Detect hardware, add JSONL datasets, and enter a local path or Hugging Face model ID. All queued files feed one training run.";

    [ObservableProperty]
    private double _trainingProgress;

    [ObservableProperty]
    private string _trainingProgressText = "Not started";

    private static readonly string DefaultAgentModelRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaxIntelligenceStudio",
        "Models");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DiscoverAgentModelsCommand))]
    private string _agentModelRoot = DefaultAgentModelRoot;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ActivateAgentModelCommand))]
    private AgentModelCandidate? _selectedAgentModel;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ActivateAgentModelCommand))]
    private string _selectedAgentRuntime = "Transformers / PEFT";

    [ObservableProperty]
    private string _ollamaModelName = "tax-intelligence";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestAgentModelCommand))]
    private string _agentPrompt =
        "Summarize the evidence requirements for a federal tax recommendation.";

    [ObservableProperty]
    private string _agentResponse = string.Empty;

    [ObservableProperty]
    private string _agentStatus =
        "Discover a completed adapter or training checkpoint, then activate it for local agent use.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunModelInferenceCommand))]
    private string _modelInferencePrompt = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReusePromptCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeletePromptCommand))]
    private SavedInferencePrompt? _selectedSavedPrompt;

    [ObservableProperty]
    private string _promptHistoryStatus =
        "Typed prompts are saved locally when sent. Attachment excerpts and replies are not saved here.";

    public ObservableCollection<SavedInferencePrompt> SavedPrompts { get; } = [];

    private bool CanUseSavedPrompt() => IsNotBusy && SelectedSavedPrompt is not null;

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task LoadPromptHistoryAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            Replace(SavedPrompts, await promptHistory.GetAsync(cancellationToken));
            PromptHistoryStatus = $"{SavedPrompts.Count} saved prompt(s). Stored locally, including any private text you typed.";
        }
        catch (Exception exception)
        {
            PromptHistoryStatus = $"Cannot load prompt history: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseSavedPrompt))]
    private void ReusePrompt()
    {
        ModelInferencePrompt = SelectedSavedPrompt!.Prompt;
        PromptHistoryStatus = "Saved prompt copied to your message. Review it before sending; no attachments were restored.";
    }

    [RelayCommand(CanExecute = nameof(CanUseSavedPrompt))]
    private Task DeletePromptAsync(CancellationToken cancellationToken) =>
        RemovePromptHistoryAsync(false, cancellationToken);

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private Task ClearPromptHistoryAsync(CancellationToken cancellationToken) =>
        RemovePromptHistoryAsync(true, cancellationToken);

    private async Task RemovePromptHistoryAsync(bool clearAll, CancellationToken cancellationToken)
    {
        if (clearAll && System.Windows.MessageBox.Show(
                "Delete all saved inference prompts? This cannot be undone.",
                "Clear prompt history", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }
        IsBusy = true;
        try
        {
            if (clearAll)
            {
                await promptHistory.ClearAsync(cancellationToken);
            }
            else
            {
                await promptHistory.DeleteAsync(SelectedSavedPrompt!.Id, cancellationToken);
            }
            SelectedSavedPrompt = null;
            Replace(SavedPrompts, await promptHistory.GetAsync(cancellationToken));
            PromptHistoryStatus = $"History updated: {SavedPrompts.Count} saved prompt(s).";
        }
        catch (Exception exception)
        {
            PromptHistoryStatus = $"Cannot delete prompt history: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [ObservableProperty]
    private string _selectedInferenceProvider = modelProviderOptions.DefaultProvider;

    [ObservableProperty]
    private string _modelInferenceResponse = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditTraining))]
    [NotifyCanExecuteChangedFor(nameof(TrainModelCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChooseTrainingDatasetCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveTrainingDatasetCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearTrainingDatasetsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChooseTrainingOutputCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCheckpointsCommand))]
    private bool _isInferenceRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditTraining))]
    [NotifyCanExecuteChangedFor(nameof(TrainModelCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChooseTrainingDatasetCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveTrainingDatasetCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearTrainingDatasetsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChooseTrainingOutputCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCheckpointsCommand))]
    private bool _isTraining;

    [ObservableProperty]
    private string _modelInferenceElapsed = string.Empty;

    [ObservableProperty]
    private bool _exportFullInferenceConversation;

    [ObservableProperty]
    private string _inferenceExportStatus =
        "Export the latest completed response, or include the full conversation. Exports may contain private information.";

    [ObservableProperty]
    private bool _detectAttachmentFaces = true;

    [ObservableProperty]
    private bool _describeAttachmentObjects;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveInferenceAttachmentCommand))]
    private InferenceAttachment? _selectedInferenceAttachment;

    [ObservableProperty]
    private string _inferenceAttachmentStatus =
        "Attach images, PDF, DOCX, XLSX, TXT or CSV. OCR is local. Optional face detection does not identify people.";

    [ObservableProperty]
    private string _modelProviderStatus =
        "Choose a configured provider and check its availability before sending a request.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveAgentModelSummary))]
    [NotifyCanExecuteChangedFor(nameof(TestAgentModelCommand))]
    private ActiveAgentModel? _activeAgentModel = agentModelService.GetActiveModel();

    public bool IsNotBusy => !IsBusy;
    public bool CanEditTraining => !IsTraining && (!IsBusy || IsInferenceRunning);
    public ObservableCollection<TrainingDatasetLog> RecentTrainingDatasets { get; } = [];

    [ObservableProperty]
    private TrainingDatasetLog? _selectedTrainingDatasetLog;

    [ObservableProperty]
    private string _trainingDatasetHistoryStatus = "Recent dataset selections are stored locally, not dataset contents.";

    [RelayCommand]
    private async Task RefreshTrainingDatasetHistoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            Replace(RecentTrainingDatasets, await trainingHistory.GetAsync(TrainingOutputDirectory, cancellationToken));
            SelectedTrainingDatasetLog = RecentTrainingDatasets.FirstOrDefault();
            TrainingDatasetHistoryStatus = $"{RecentTrainingDatasets.Count} training attempt(s) / existing manifest(s). " +
                "Submitted entries are not proof that datasets were consumed; an interrupted app may leave the outcome unrecorded.";
        }
        catch (Exception exception)
        {
            TrainingDatasetHistoryStatus = $"Cannot read dataset history: {exception.Message}";
        }
    }

    private async Task FinishTrainingDatasetLogAsync(Guid? id, string status, string? outputDirectory = null)
    {
        if (id is null)
        {
            return;
        }
        try
        {
            await trainingHistory.FinishAsync(id.Value, status, outputDirectory);
            await RefreshTrainingDatasetHistoryAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            TrainingDatasetHistoryStatus = $"Training outcome was not saved to history: {exception.Message}";
        }
    }
    public ObservableCollection<FinancialTransaction> Transactions { get; } = [];
    public ObservableCollection<FinancialFileImport> FinancialFileImports { get; } = [];
    public ObservableCollection<TaxMapping> Mappings { get; } = [];
    public ObservableCollection<TaxOpportunity> Opportunities { get; } = [];
    public ObservableCollection<ComputeAccelerator> Accelerators { get; } = [];
    public ObservableCollection<ModelTrainingCheckpoint> TrainingCheckpoints { get; } =
        [NewTrainingCheckpoint];
    public ObservableCollection<AgentModelCandidate> AgentModels { get; } =
        new(agentModelService.DiscoverModels(DefaultAgentModelRoot));
    public ObservableCollection<ModelProviderAvailability> ModelProviders { get; } = [];
    public ObservableCollection<InferenceAttachment> InferenceAttachmentItems { get; } = [];
    public ObservableCollection<string> TrainingDatasetPaths { get; } = [];
    public string TrainingDatasetSummary =>
        $"{TrainingDatasetPaths.Count} dataset file(s) queued for one training run";
    public IReadOnlyCollection<string> ModelProviderIds { get; } = ["Trained", "Local", "Azure"];
    public IReadOnlyCollection<string> AgentRuntimes { get; } =
        ["Transformers / PEFT", "Ollama"];
    public string ActiveAgentModelSummary => ActiveAgentModel is null
        ? "No active agent model"
        : $"{ActiveAgentModel.Runtime}: {ActiveAgentModel.BaseModel}" +
          (string.IsNullOrWhiteSpace(ActiveAgentModel.RuntimeModelName)
              ? Environment.NewLine + ActiveAgentModel.AdapterPath
              : $" ({ActiveAgentModel.RuntimeModelName})");
    public IReadOnlyCollection<string> ModelFamilies => modelCatalogService.GetFamilies();
    public IReadOnlyCollection<BaseModelOption> AvailableBaseModels =>
        modelCatalogService.GetModels(SelectedModelFamily);

    partial void OnSelectedModelFamilyChanged(string value)
    {
        BaseModel = modelCatalogService.GetModels(value).FirstOrDefault()?.ModelId ?? string.Empty;
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task CrawlAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            var progress = new Progress<CrawlProgress>(item =>
            {
                KnowledgeStatus =
                    $"Discovered {item.Discovered:N0}; processed {item.Processed:N0}; " +
                    $"added {item.Added:N0}; updated {item.Updated:N0}; " +
                    $"unchanged {item.Unchanged:N0}; failed {item.Failed:N0}\n{item.CurrentUrl}";
            });

            var result = await ingestionService.CrawlAsync(
                MaximumDocuments,
                progress,
                cancellationToken);
            KnowledgeStatus =
                $"IRS ingestion completed live at {result.CompletedAt:u}. " +
                $"Processed {result.Processed:N0}, added {result.Added:N0}, " +
                $"updated {result.Updated:N0}, unchanged {result.Unchanged:N0}, failed {result.Failed:N0}.";
        }
        catch (OperationCanceledException)
        {
            KnowledgeStatus = "IRS ingestion was canceled.";
        }
        catch (Exception exception)
        {
            KnowledgeStatus = $"IRS ingestion failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task ExportDatasetAsync(CancellationToken cancellationToken)
    {
        if (!int.TryParse(DatasetFileCount, out var datasetFileCount) || datasetFileCount < 0)
        {
            KnowledgeStatus = "Dataset file count must be a whole number of 0 or greater. Use 0 for automatic splitting.";
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "Export citation-grounded training dataset",
            Filter = "JSON Lines (*.jsonl)|*.jsonl",
            FileName = $"irs-training-{DateTime.Now:yyyyMMdd}.jsonl",
            AddExtension = true,
            DefaultExt = ".jsonl"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await datasetExportService.ExportJsonLinesAsync(
                dialog.FileName,
                datasetFileCount,
                cancellationToken);
            KnowledgeStatus = $"Exported {result.RecordCount:N0} grounded training records to {dialog.FileName}." +
                $"{Environment.NewLine}Manifest: {result.ManifestPath}{Environment.NewLine}SHA-256: {result.Sha256}" +
                $"{Environment.NewLine}Leakage-resistant training splits: {result.TrainingSplits?.TrainingFile}" +
                $"{Environment.NewLine}Validation/test records: {result.TrainingSplits?.ValidationRecordCount:N0}/" +
                $"{result.TrainingSplits?.TestRecordCount:N0}; duplicate/PII exclusions: " +
                $"{result.TrainingSplits?.DuplicateExcludedCount:N0}/{result.TrainingSplits?.PiiExcludedCount:N0} " +
                (datasetFileCount > 0 || result.UploadFiles.Count > 1
                    ? $"{Environment.NewLine}Created {result.UploadFiles.Count} dataset files in " +
                      $"{Path.GetDirectoryName(result.UploadFiles[0])}. Upload these byte/count parts if needed; " +
                      "they are not ML train/validation/test splits."
                    : string.Empty);
        }
        catch (Exception exception)
        {
            KnowledgeStatus = $"Dataset export failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task ImportFilesAsync(CancellationToken cancellationToken)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import financial exports",
            Filter = "Supported financial files|*.pdf;*.csv;*.xlsx;*.xlsm;*.txt|" +
                     "PDF files|*.pdf|CSV files|*.csv|Excel workbooks|*.xlsx;*.xlsm|Text files|*.txt",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;
        FinancialProgress = 0;
        FinancialProgressText = $"Preparing to import {dialog.FileNames.Length:N0} file(s)";
        try
        {
            var importProgress = new Progress<FinancialImportProgress>(item =>
            {
                FinancialProgress = item.TotalFiles == 0
                    ? 0
                    : 100d * item.ProcessedFiles / item.TotalFiles;
                FinancialProgressText =
                    $"{item.Stage}: {item.CurrentFile} — " +
                    $"{item.ProcessedFiles:N0}/{item.TotalFiles:N0} files, " +
                    $"{item.TransactionsFound:N0} financial/tax records ({FinancialProgress:0}%)";
            });
            var importResult = await financialImportService.ImportAsync(
                dialog.FileNames,
                importProgress,
                cancellationToken);
            Replace(Transactions, importResult.Transactions);
            Replace(FinancialFileImports, importResult.Files);

            var parsedFiles = importResult.Files.Count(file => file.Succeeded);
            var failedFiles = importResult.Files.Count - parsedFiles;
            FinancialStatus =
                $"Parsed {importResult.Transactions.Count:N0} financial/tax records from " +
                $"{parsedFiles:N0} of {importResult.Files.Count:N0} file(s). " +
                $"{failedFiles:N0} file(s) require attention." +
                (importResult.Transactions.Count == 0
                    ? " Analysis is unavailable because no actual transaction rows were extracted. See file diagnostics below."
                    : string.Empty);
            FinancialProgress = 100;
            FinancialProgressText =
                $"Import complete: {importResult.Transactions.Count:N0} financial/tax records from " +
                $"{parsedFiles:N0}/{importResult.Files.Count:N0} files";
            AnalyzeCommand.NotifyCanExecuteChanged();
            if (Transactions.Count > 0)
            {
                FinancialStatus += " Running live IRS-verified federal tax analysis...";
                await AnalyzeAsync(cancellationToken);
                FinancialStatus += _analysis is null
                    ? " Analysis did not complete; see the 1040 Analysis tab for details."
                    : $" Analysis completed with {Mappings.Count:N0} reporting mapping(s) and " +
                      $"{Opportunities.Count:N0} optimization review item(s).";
            }
        }
        catch (Exception exception)
        {
            FinancialStatus = $"Import failed: {exception.Message}";
            FinancialProgressText = "Import failed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanAnalyze() => IsNotBusy && Transactions.Count > 0;

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        AnalysisProgress = 0;
        AnalysisProgressText = "Starting federal tax analysis";
        try
        {
            var analysisProgress = new Progress<TaxAnalysisProgress>(item =>
            {
                AnalysisProgress = Math.Clamp(item.Percentage, 0, 100);
                AnalysisProgressText = $"{item.Stage} ({AnalysisProgress:0}%)";
            });
            _analysis = await taxAnalysisService.AnalyzeAsync(
                Transactions.ToList(),
                analysisProgress,
                cancellationToken);
            Replace(Mappings, _analysis.Mappings);
            Replace(Opportunities, _analysis.Opportunities);
            ReportStatus =
                $"Analysis completed at {_analysis.AnalyzedAt:u}. " +
                $"{Mappings.Count:N0} reporting categories and " +
                $"{Opportunities.Count:N0} optimization review items identified.";
            ExportPdfCommand.NotifyCanExecuteChanged();
            ExportWordCommand.NotifyCanExecuteChanged();
        }
        catch (Exception exception)
        {
            ReportStatus = $"Analysis failed: {exception.Message}";
            AnalysisProgressText = "Analysis failed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanExportReport() => IsNotBusy && _analysis is not null;

    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private Task ExportPdfAsync(CancellationToken cancellationToken) =>
        ExportReportAsync(ReportFormat.Pdf, cancellationToken);

    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private Task ExportWordAsync(CancellationToken cancellationToken) =>
        ExportReportAsync(ReportFormat.Word, cancellationToken);

    private async Task ExportReportAsync(
        ReportFormat format,
        CancellationToken cancellationToken)
    {
        if (_analysis is null)
        {
            return;
        }

        var extension = format == ReportFormat.Pdf ? ".pdf" : ".docx";
        var dialog = new SaveFileDialog
        {
            Title = "Export tax analysis workpaper",
            Filter = format == ReportFormat.Pdf
                ? "PDF report (*.pdf)|*.pdf"
                : "Microsoft Word document (*.docx)|*.docx",
            FileName = $"tax-analysis-{DateTime.Now:yyyyMMdd}{extension}",
            AddExtension = true,
            DefaultExt = extension
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;
        ReportStatus = "Revalidating every cited IRS source live before report generation...";
        try
        {
            await taxReportService.ExportAsync(
                _analysis,
                dialog.FileName,
                format,
                cancellationToken);
            ReportStatus = $"Verified report exported to {dialog.FileName}.";
        }
        catch (Exception exception)
        {
            ReportStatus = $"Report blocked: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void Replace<T>(
        ObservableCollection<T> target,
        IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
        private async Task DetectHardwareAsync(CancellationToken cancellationToken)
        {
            IsBusy = true;
            try
            {
                var devices = await hardwareAccelerationService.DetectAsync(cancellationToken);
                Replace(Accelerators, devices);
                var trainingDevice = devices.FirstOrDefault(device => device.SupportsTraining);
                var npus = devices.Count(device => device.Kind == AcceleratorKind.Npu);
                var platform = TrainingPlatform.Describe(TrainingPlatform.CurrentArchitecture);
                if (trainingDevice is null)
                {
                    var reasons = devices
                        .Where(device => !string.IsNullOrWhiteSpace(device.TrainingUnavailableReason))
                        .Select(device => device.TrainingUnavailableReason!)
                        .Distinct()
                        .ToList();
                    TrainingStatus =
                        $"Windows {platform}: detected {devices.Count} compute device(s), " +
                        $"including {npus} NPU(s), but no supported training device. " +
                        string.Join(" ", reasons);
                }
                else
                {
                    TrainingStatus =
                        $"Windows {platform} - preferred trainer: {trainingDevice.Name} " +
                        $"({trainingDevice.Runtime}). " +
                        $"{npus} NPU(s) available for inference/evaluation.";

                    var hostGuidance = TrainingPlatform.HostGuidance(
                        TrainingPlatform.CurrentArchitecture);
                    if (hostGuidance is not null)
                    {
                        TrainingStatus += $" {hostGuidance}";
                    }
                }
            }
            catch (Exception exception)
            {
                TrainingStatus = $"Hardware detection failed: {exception.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand(CanExecute = nameof(IsNotBusy))]
        private async Task RunAcceleratorSelfTestAsync(CancellationToken cancellationToken)
        {
            IsBusy = true;
            TrainingStatus = "Running real DirectML GPU and Qualcomm QNN NPU workloads...";
            try
            {
                var result = await acceleratorSelfTestService.RunAsync(
                    PythonExecutable,
                    cancellationToken);
                TrainingStatus =
                    $"GPU verified: {result.GpuVerified} ({result.GpuDevice}){Environment.NewLine}" +
                    $"NPU verified: {result.NpuVerified} ({result.NpuProvider}){Environment.NewLine}" +
                    result.Details;
            }
            catch (Exception exception)
            {
                TrainingStatus = $"Accelerator self-test failed: {exception.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand(CanExecute = nameof(CanEditTraining))]
        private void ChooseTrainingDataset()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Add JSONL datasets to the training queue",
                Filter = "JSON Lines (*.jsonl)|*.jsonl",
                CheckFileExists = true,
                Multiselect = true
            };
            if (dialog.ShowDialog() == true)
            {
                foreach (var path in dialog.FileNames)
                {
                    var fullPath = Path.GetFullPath(path);
                    if (!TrainingDatasetPaths.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                    {
                        TrainingDatasetPaths.Add(fullPath);
                    }
                }
                SelectedTrainingDataset = TrainingDatasetPaths.LastOrDefault();
                NotifyTrainingDatasetsChanged();
            }
        }

        private bool CanRemoveTrainingDataset() =>
            CanEditTraining && SelectedTrainingDataset is not null;

        [RelayCommand(CanExecute = nameof(CanRemoveTrainingDataset))]
        private void RemoveTrainingDataset()
        {
            if (SelectedTrainingDataset is not null)
            {
                TrainingDatasetPaths.Remove(SelectedTrainingDataset);
            }
            SelectedTrainingDataset = TrainingDatasetPaths.LastOrDefault();
            NotifyTrainingDatasetsChanged();
        }

        private bool CanClearTrainingDatasets() => CanEditTraining && TrainingDatasetPaths.Count > 0;

        [RelayCommand(CanExecute = nameof(CanClearTrainingDatasets))]
        private void ClearTrainingDatasets()
        {
            TrainingDatasetPaths.Clear();
            SelectedTrainingDataset = null;
            NotifyTrainingDatasetsChanged();
        }

        private void NotifyTrainingDatasetsChanged()
        {
            OnPropertyChanged(nameof(TrainingDatasetSummary));
            TrainModelCommand.NotifyCanExecuteChanged();
            RemoveTrainingDatasetCommand.NotifyCanExecuteChanged();
            ClearTrainingDatasetsCommand.NotifyCanExecuteChanged();
        }

        [RelayCommand(CanExecute = nameof(CanEditTraining))]
        private void ChooseTrainingOutput()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select model output folder",
                Multiselect = false
            };
            if (dialog.ShowDialog() == true)
            {
                TrainingOutputDirectory = dialog.FolderName;
                RefreshCheckpoints();
            }
        }

        private bool CanRefreshCheckpoints() =>
            CanEditTraining && !string.IsNullOrWhiteSpace(TrainingOutputDirectory);

        [RelayCommand(CanExecute = nameof(CanRefreshCheckpoints))]
        private void RefreshCheckpoints()
        {
            try
            {
                var selectedPath = SelectedCheckpoint?.Path;
                var checkpoints = modelTrainingService.GetCheckpoints(TrainingOutputDirectory);
                Replace(TrainingCheckpoints, checkpoints);
                SelectedCheckpoint = TrainingCheckpoints.FirstOrDefault(item =>
                        string.Equals(item.Path, selectedPath, StringComparison.OrdinalIgnoreCase))
                    ?? TrainingCheckpoints[0];
            }
            catch (Exception exception)
            {
                TrainingStatus = $"Checkpoint discovery failed: {exception.Message}";
            }
        }

        private bool CanTrainModel() =>
            CanEditTraining &&
            !string.IsNullOrWhiteSpace(BaseModel) &&
            TrainingDatasetPaths.Count > 0 &&
            !string.IsNullOrWhiteSpace(TrainingOutputDirectory);

        [RelayCommand(CanExecute = nameof(CanTrainModel), IncludeCancelCommand = true)]
        private async Task TrainModelAsync(CancellationToken cancellationToken)
        {
            IsTraining = true;
            Guid? datasetLogId = null;
            TrainingProgress = 0;
            TrainingProgressText = "Starting training preflight";
            try
            {
                TrainingStatus = "Starting training preflight...";
                var progress = new Progress<string>(message =>
                {
                    TrainingStatus = TrainingStatus.Length > 12_000
                        ? message
                        : $"{TrainingStatus}{Environment.NewLine}{message}";
                    TryApplyTrainingProgress(message);
                });
                var trainingRequest = new ModelTrainingRequest(
                        BaseModel.Trim(),
                        TrainingDatasetPaths[0],
                        TrainingOutputDirectory,
                        PythonExecutable,
                        Epochs: 1,
                        BatchSize: 1,
                        LearningRate: 0.00005m,
                        AllowCpuFallback,
                        SelectedCheckpoint?.Path,
                        AdditionalDatasetPaths: TrainingDatasetPaths.Skip(1).ToArray());
                datasetLogId = await trainingHistory.StartAsync(trainingRequest, cancellationToken);
                await RefreshTrainingDatasetHistoryAsync(cancellationToken);
                var result = await modelTrainingService.TrainAsync(
                    trainingRequest,
                    progress,
                    cancellationToken);
                await FinishTrainingDatasetLogAsync(datasetLogId,
                    result.Succeeded ? "Completed" : "Failed", result.OutputDirectory);
                TrainingStatus = result.Succeeded
                    ? $"{result.Log}{Environment.NewLine}Accelerator: {result.Accelerator}{Environment.NewLine}Output: {result.OutputDirectory}"
                    : $"{result.Log}{Environment.NewLine}Accelerator: {result.Accelerator}";
                if (result.Succeeded)
                {
                    TrainingProgress = 100;
                    TrainingProgressText = "Training complete";
                    AgentModelRoot = TrainingOutputDirectory;
                    DiscoverAgentModels();
                }
                else
                {
                    TrainingProgressText = "Training failed";
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await FinishTrainingDatasetLogAsync(datasetLogId, "Cancelled");
                TrainingStatus = "Training cancelled. Inference and earlier conversation were not cancelled.";
                TrainingProgressText = "Training cancelled";
            }
            catch (Exception exception)
            {
                await FinishTrainingDatasetLogAsync(datasetLogId, "Failed (preflight/training)");
                TrainingStatus = $"Training did not start or failed: {exception.Message}";
                TrainingProgressText = "Training failed";
            }
            finally
            {
                IsTraining = false;
                RefreshCheckpoints();
        }
    }

    [RelayCommand]
    private void ChooseAgentModelRoot()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select trained model output folder",
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
        {
            AgentModelRoot = dialog.FolderName;
            DiscoverAgentModels();
        }
    }

    private bool CanDiscoverAgentModels() =>
        IsNotBusy && !string.IsNullOrWhiteSpace(AgentModelRoot);

    [RelayCommand(CanExecute = nameof(CanDiscoverAgentModels))]
    private void DiscoverAgentModels()
    {
        try
        {
            var models = agentModelService.DiscoverModels(AgentModelRoot);
            Replace(AgentModels, models);
            SelectedAgentModel = AgentModels.FirstOrDefault();
            AgentStatus = models.Count == 0
                ? "No active training sessions or PEFT adapters were found in the selected folder."
                : $"Discovered {models.Count:N0} training/model artifact(s).";
        }
        catch (Exception exception)
        {
            AgentStatus = $"Model discovery failed: {exception.Message}";
        }
    }

    private bool CanActivateAgentModel() =>
        IsNotBusy && SelectedAgentModel?.CanActivate == true;

    [RelayCommand(CanExecute = nameof(CanActivateAgentModel))]
    private async Task ActivateAgentModelAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        AgentStatus = SelectedAgentRuntime == "Ollama"
            ? "Merging adapter and creating Ollama model. This can require substantial memory and disk space..."
            : "Validating and activating local PEFT model...";
        try
        {
            var progress = new Progress<string>(AppendAgentStatus);
            ActiveAgentModel = SelectedAgentRuntime == "Ollama"
                ? await agentModelService.CreateOllamaModelAsync(
                    SelectedAgentModel!,
                    OllamaModelName,
                    PythonExecutable,
                    progress,
                    cancellationToken)
                : await agentModelService.ActivateAsync(
                    SelectedAgentModel!,
                    SelectedAgentRuntime,
                    cancellationToken: cancellationToken);
            AgentStatus =
                $"Active agent model saved at {ActiveAgentModel.ActivatedAt.LocalDateTime:g}.{Environment.NewLine}" +
                ActiveAgentModelSummary;
        }
        catch (Exception exception)
        {
            AgentStatus = $"Model activation failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanTestAgentModel() =>
        IsNotBusy &&
        ActiveAgentModel is not null &&
        !string.IsNullOrWhiteSpace(AgentPrompt);

    [RelayCommand(CanExecute = nameof(CanTestAgentModel))]
    private async Task TestAgentModelAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        AgentResponse = string.Empty;
        AgentStatus = "Loading the active model for a local test prompt...";
        try
        {
            AgentResponse = await agentModelService.TestPromptAsync(
                ActiveAgentModel!,
                AgentPrompt,
                PythonExecutable,
                new Progress<string>(AppendAgentStatus),
                cancellationToken);
            AgentStatus = "The active agent model returned a local inference result.";
        }
        catch (Exception exception)
        {
            AgentStatus = $"Agent model test failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void AppendAgentStatus(string message)
    {
        AgentStatus = AgentStatus.Length > 12_000
            ? message
            : $"{AgentStatus}{Environment.NewLine}{message}";
    }

    private bool CanRunModelInference() =>
        IsNotBusy && (!string.IsNullOrWhiteSpace(ModelInferencePrompt) || InferenceAttachmentItems.Count > 0);

    [RelayCommand(CanExecute = nameof(IsNotBusy), IncludeCancelCommand = true)]
    private async Task AddInferenceAttachmentsAsync(CancellationToken cancellationToken)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add images or documents to the next inference message",
            Filter = "Supported attachments|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.pdf;*.docx;*.xlsx;*.txt;*.csv",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        if (InferenceAttachmentItems.Count + dialog.FileNames.Length > InferenceAttachmentContext.MaximumAttachments)
        {
            InferenceAttachmentStatus = $"At most {InferenceAttachmentContext.MaximumAttachments} attachments are allowed per message. Remove files first.";
            return;
        }
        IsBusy = true;
        var acceptingProgress = true;
        try
        {
            var progress = new Progress<string>(message =>
            {
                if (acceptingProgress)
                {
                    InferenceAttachmentStatus = message;
                }
            });
            foreach (var path in dialog.FileNames)
            {
                InferenceAttachmentStatus = $"Processing {Path.GetFileName(path)} locally. Vision model loading can take several minutes...";
                var attachment = await inferenceAttachments.ExtractAsync(
                    path, new InferenceAttachmentOptions(DetectAttachmentFaces, DescribeAttachmentObjects),
                    progress, cancellationToken);
                InferenceAttachmentContext.Append(string.Empty, [.. InferenceAttachmentItems, attachment]);
                InferenceAttachmentItems.Add(attachment);
                SelectedInferenceAttachment = attachment;
            }
            InferenceAttachmentStatus = $"{InferenceAttachmentItems.Count} attachment(s) ready. Review extracted text below before sending.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            InferenceAttachmentStatus = "Attachment processing cancelled. Already processed files are retained.";
        }
        catch (Exception exception)
        {
            InferenceAttachmentStatus = $"Attachment processing failed: {exception.Message} Already processed files are retained.";
        }
        finally
        {
            acceptingProgress = false;
            IsBusy = false;
            NotifyInferenceAttachmentsChanged();
        }
    }

    private bool CanRemoveInferenceAttachment() => IsNotBusy && SelectedInferenceAttachment is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveInferenceAttachment))]
    private void RemoveInferenceAttachment()
    {
        if (SelectedInferenceAttachment is not null)
        {
            InferenceAttachmentItems.Remove(SelectedInferenceAttachment);
        }
        SelectedInferenceAttachment = InferenceAttachmentItems.LastOrDefault();
        InferenceAttachmentStatus = $"{InferenceAttachmentItems.Count} attachment(s) ready for the next message.";
        NotifyInferenceAttachmentsChanged();
    }

    private bool CanClearInferenceAttachments() => IsNotBusy && InferenceAttachmentItems.Count > 0;

    [RelayCommand(CanExecute = nameof(CanClearInferenceAttachments))]
    private void ClearInferenceAttachments()
    {
        InferenceAttachmentItems.Clear();
        SelectedInferenceAttachment = null;
        InferenceAttachmentStatus = "No pending attachments. Sent excerpts remain in the current conversation.";
        NotifyInferenceAttachmentsChanged();
    }

    private void NotifyInferenceAttachmentsChanged()
    {
        RunModelInferenceCommand.NotifyCanExecuteChanged();
        RemoveInferenceAttachmentCommand.NotifyCanExecuteChanged();
        ClearInferenceAttachmentsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private void NewModelConversation()
    {
        _modelInferenceConversation.Clear();
        _modelInferenceTurns.Clear();
        NotifyInferenceExportChanged();
        InferenceExportStatus = "No completed response to export.";
        ModelInferencePrompt = string.Empty;
        ModelInferenceResponse = string.Empty;
        ModelInferenceElapsed = string.Empty;
        ClearInferenceAttachments();
        ModelProviderStatus = "New conversation ready. Ask a question to begin.";
    }

    private bool CanExportInference() => IsNotBusy && _modelInferenceTurns.Count > 0;

    private void NotifyInferenceExportChanged()
    {
        ExportInferencePdfCommand.NotifyCanExecuteChanged();
        ExportInferenceWordCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanExportInference))]
    private Task ExportInferencePdfAsync(CancellationToken cancellationToken) =>
        ExportInferenceAsync(ReportFormat.Pdf, cancellationToken);

    [RelayCommand(CanExecute = nameof(CanExportInference))]
    private Task ExportInferenceWordAsync(CancellationToken cancellationToken) =>
        ExportInferenceAsync(ReportFormat.Word, cancellationToken);

    private async Task ExportInferenceAsync(ReportFormat format, CancellationToken cancellationToken)
    {
        var extension = format == ReportFormat.Pdf ? ".pdf" : ".docx";
        var dialog = new SaveFileDialog
        {
            Title = "Export inference results",
            Filter = format == ReportFormat.Pdf ? "PDF (*.pdf)|*.pdf" : "Word document (*.docx)|*.docx",
            FileName = $"tax-inference-{DateTime.Now:yyyyMMdd-HHmmss}{extension}",
            DefaultExt = extension,
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        IsBusy = true;
        InferenceExportStatus = "Formatting inference results...";
        try
        {
            var turns = ExportFullInferenceConversation
                ? _modelInferenceTurns.ToArray()
                : new[] { _modelInferenceTurns[^1] };
            await inferenceReports.ExportAsync(turns, dialog.FileName, format, cancellationToken);
            InferenceExportStatus = $"Exported {turns.Length} response(s) to {dialog.FileName}. Sources were not reverified.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            InferenceExportStatus = "Inference export cancelled.";
        }
        catch (Exception exception)
        {
            InferenceExportStatus = $"Inference export failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task RefreshModelProvidersAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            var providers = await modelProviderResolver.GetAvailabilityAsync(cancellationToken);
            Replace(ModelProviders, providers);
            ModelProviderStatus = string.Join(
                Environment.NewLine,
                providers.Select(provider =>
                    $"{provider.DisplayName} ({provider.ProviderId}): " +
                    $"{(provider.IsAvailable ? "available" : "unavailable")} - {provider.Diagnostic}"));
        }
        catch (Exception exception)
        {
            ModelProviderStatus = $"Provider status check failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunModelInference), IncludeCancelCommand = true)]
    private async Task RunModelInferenceAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        IsInferenceRunning = true;
        ModelInferenceElapsed = "Elapsed: 0s";
        ModelProviderStatus = "Preparing inference request...";
        var startedAt = Stopwatch.GetTimestamp();
        var acceptingProgress = true;
        using var elapsedCancellation = new CancellationTokenSource();
        var elapsedProgress = new Progress<string>(elapsed =>
        {
            if (acceptingProgress)
            {
                ModelInferenceElapsed = elapsed;
            }
        });
        var elapsedTask = UpdateInferenceElapsedAsync(
            startedAt,
            elapsedProgress,
            elapsedCancellation.Token);
        try
        {
            var stageProgress = new Progress<string>(stage =>
            {
                if (acceptingProgress)
                {
                    ModelProviderStatus = stage;
                }
            });
            var messages = new List<ModelChatMessage>(_modelInferenceConversation);
            if (!string.IsNullOrWhiteSpace(ModelInferencePrompt))
            {
                ModelProviderStatus = "Saving typed prompt locally...";
                await promptHistory.SaveAsync(ModelInferencePrompt.Trim(), cancellationToken);
                Replace(SavedPrompts, await promptHistory.GetAsync(cancellationToken));
                PromptHistoryStatus = $"Prompt saved locally. {SavedPrompts.Count} saved prompt(s).";
            }
            var userMessage = InferenceAttachmentContext.Append(
                string.IsNullOrWhiteSpace(ModelInferencePrompt)
                    ? "Review the attached material, distinguish observations from facts, and ask what further analysis is needed."
                    : ModelInferencePrompt.Trim(),
                InferenceAttachmentItems);
            messages.Add(new ModelChatMessage("user", userMessage));
            var response = await modelInferenceService.InferAsync(
                new ModelInferenceRequest(messages),
                SelectedInferenceProvider,
                cancellationToken,
                stageProgress);
            acceptingProgress = false;
            _modelInferenceTurns.Add(new ModelInferenceTurn(userMessage, response));
            NotifyInferenceExportChanged();
            InferenceExportStatus = "Response ready to export as PDF or Word.";
            _modelInferenceConversation.Add(new ModelChatMessage("user", userMessage));
            _modelInferenceConversation.Add(new ModelChatMessage("assistant", response.Content));
            ModelInferenceResponse = string.Join(
                $"{Environment.NewLine}{Environment.NewLine}",
                _modelInferenceConversation.Select(message =>
                    $"{(message.Role == "user" ? "You" : "Assistant")}: {message.Content}"));
            ModelInferencePrompt = string.Empty;
            ClearInferenceAttachments();
            InferenceAttachmentStatus = "Attachments were included in the conversation. Add files for your next message if needed.";
            ModelProviderStatus =
                $"{response.ProviderId} / {response.ModelId}; source data: {response.SourceDataMode}; " +
                $"elapsed: {response.Elapsed.TotalSeconds:F1}s." +
                (response.Warnings.Count == 0
                    ? string.Empty
                    : $"{Environment.NewLine}Warning: {string.Join(" ", response.Warnings)}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            acceptingProgress = false;
            ModelProviderStatus = "Request cancelled. Your message and earlier conversation have been retained.";
        }
        catch (Exception exception)
        {
            acceptingProgress = false;
            ModelProviderStatus = $"Inference failed: {exception.Message}";
        }
        finally
        {
            acceptingProgress = false;
            elapsedCancellation.Cancel();
            await elapsedTask;
            ModelInferenceElapsed = $"Elapsed: {Stopwatch.GetElapsedTime(startedAt).TotalSeconds:F0}s";
            IsInferenceRunning = false;
            IsBusy = false;
        }
    }

    private static async Task UpdateInferenceElapsedAsync(
        long startedAt,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                progress.Report(
                    $"Elapsed: {Stopwatch.GetElapsedTime(startedAt).TotalSeconds:F0}s");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private bool TryApplyTrainingProgress(string message)
    {
        const string eventPrefix = "TIS_EVENT|";
        if (message.StartsWith(eventPrefix, StringComparison.Ordinal))
        {
            try
            {
                var trainingEvent = JsonSerializer.Deserialize<TrainingProgressEvent>(
                    message[eventPrefix.Length..],
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (trainingEvent is null)
                {
                    return false;
                }

                TrainingProgress = Math.Clamp(trainingEvent.Percent, 0, 100);
                TrainingProgressText =
                    $"{trainingEvent.Message} ({TrainingProgress:0.0}%)";
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        const string legacyPrefix = "TIS_PROGRESS|";
        if (!message.StartsWith(legacyPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = message.Split('|', 3);
        if (parts.Length != 3 ||
            !double.TryParse(
                parts[1],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var percent))
        {
            return false;
        }

        TrainingProgress = Math.Clamp(percent, 0, 100);
        TrainingProgressText = $"{parts[2]} ({TrainingProgress:0.0}%)";
        return true;
    }

    private sealed record TrainingProgressEvent(
        string Stage,
        double Percent,
        string Message);
}
