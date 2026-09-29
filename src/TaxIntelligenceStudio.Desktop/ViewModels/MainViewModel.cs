using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;
using TaxIntelligenceStudio.Infrastructure.Training;

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
    IAgentModelService agentModelService) : ObservableObject
{
    private TaxAnalysisResult? _analysis;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
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
    private bool _isBusy;

    [ObservableProperty]
    private int _maximumDocuments = 100;

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
    [NotifyCanExecuteChangedFor(nameof(TrainModelCommand))]
    private string _trainingDatasetPath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TrainModelCommand))]
    private string _baseModel = "Qwen/Qwen2.5-Coder-1.5B-Instruct";

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
        "Detect hardware, select the exported JSONL dataset, and enter a local path or Hugging Face model ID.";

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
    [NotifyPropertyChangedFor(nameof(ActiveAgentModelSummary))]
    [NotifyCanExecuteChangedFor(nameof(TestAgentModelCommand))]
    private ActiveAgentModel? _activeAgentModel = agentModelService.GetActiveModel();

    public bool IsNotBusy => !IsBusy;
    public ObservableCollection<FinancialTransaction> Transactions { get; } = [];
    public ObservableCollection<FinancialFileImport> FinancialFileImports { get; } = [];
    public ObservableCollection<TaxMapping> Mappings { get; } = [];
    public ObservableCollection<TaxOpportunity> Opportunities { get; } = [];
    public ObservableCollection<ComputeAccelerator> Accelerators { get; } = [];
    public ObservableCollection<ModelTrainingCheckpoint> TrainingCheckpoints { get; } =
        [NewTrainingCheckpoint];
    public ObservableCollection<AgentModelCandidate> AgentModels { get; } =
        new(agentModelService.DiscoverModels(DefaultAgentModelRoot));
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
            var records = await datasetExportService.ExportJsonLinesAsync(
                dialog.FileName,
                cancellationToken);
            KnowledgeStatus = $"Exported {records:N0} grounded training records to {dialog.FileName}.";
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

        [RelayCommand]
        private void ChooseTrainingDataset()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select grounded IRS training dataset",
                Filter = "JSON Lines (*.jsonl)|*.jsonl",
                CheckFileExists = true
            };
            if (dialog.ShowDialog() == true)
            {
                TrainingDatasetPath = dialog.FileName;
            }
        }

        [RelayCommand]
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
            IsNotBusy && !string.IsNullOrWhiteSpace(TrainingOutputDirectory);

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
            IsNotBusy &&
            !string.IsNullOrWhiteSpace(BaseModel) &&
            File.Exists(TrainingDatasetPath) &&
            !string.IsNullOrWhiteSpace(TrainingOutputDirectory);

        [RelayCommand(CanExecute = nameof(CanTrainModel))]
        private async Task TrainModelAsync(CancellationToken cancellationToken)
        {
            IsBusy = true;
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
                var result = await modelTrainingService.TrainAsync(
                    new ModelTrainingRequest(
                        BaseModel.Trim(),
                        TrainingDatasetPath,
                        TrainingOutputDirectory,
                        PythonExecutable,
                        Epochs: 1,
                        BatchSize: 1,
                        LearningRate: 0.0002m,
                        AllowCpuFallback,
                        SelectedCheckpoint?.Path),
                    progress,
                    cancellationToken);
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
            catch (Exception exception)
            {
                TrainingStatus = $"Training did not start or failed: {exception.Message}";
                TrainingProgressText = "Training failed";
            }
            finally
            {
                IsBusy = false;
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
