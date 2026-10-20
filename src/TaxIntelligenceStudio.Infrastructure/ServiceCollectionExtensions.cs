using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Infrastructure.Inference;
using TaxIntelligenceStudio.Infrastructure.Persistence;
using TaxIntelligenceStudio.Infrastructure.Financial;
using TaxIntelligenceStudio.Infrastructure.Knowledge;
using TaxIntelligenceStudio.Infrastructure.Reporting;
using TaxIntelligenceStudio.Infrastructure.Training;
using TaxIntelligenceStudio.Infrastructure.Sources;

namespace TaxIntelligenceStudio.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTaxIntelligenceInfrastructure(
        this IServiceCollection services,
        string databasePath,
        IConfiguration? configuration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var modelProviderOptions = ModelProviderOptions.FromConfiguration(configuration);
        services.AddSingleton(modelProviderOptions);
        services.AddSingleton(modelProviderOptions.Local);
        services.AddSingleton(modelProviderOptions.Azure);
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<TaxIntelligenceDbContext>(
            options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddScoped<IAuditRepository, AuditRepository>();
        services.AddScoped<IGuidanceService, LiveGuidanceService>();
        services.AddScoped<IIrsKnowledgeIngestionService, IrsKnowledgeIngestionService>();
        services.AddScoped<IDatasetExportService, DatasetExportService>();
        services.AddScoped<IKnowledgeContextSearchService, KnowledgeContextSearchService>();
        services.AddScoped<IFinancialImportService, FinancialImportService>();
        services.AddScoped<ITaxAnalysisService, TaxAnalysisService>();
        services.AddScoped<ITaxReportService, TaxReportService>();
        services.AddScoped<IInferenceReportService, InferenceReportService>();
        services.AddScoped<IInferencePromptHistory, InferencePromptHistory>();
        services.AddSingleton<IHardwareAccelerationService, WindowsHardwareAccelerationService>();
        services.AddSingleton<IAcceleratorSelfTestService, AcceleratorSelfTestService>();
        services.AddSingleton<IModelCatalogService, ModelCatalogService>();
        services.AddScoped<IModelTrainingService, LocalModelTrainingService>();
        services.AddScoped<IAgentModelService, AgentModelService>();
        services.AddHttpClient<LocalModelInferenceProvider>((serviceProvider, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(
                Math.Clamp(
                    serviceProvider.GetRequiredService<LocalModelProviderOptions>().RequestTimeoutSeconds,
                    1,
                    1800));
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        });
        services.AddHttpClient<AzureModelInferenceProvider>((serviceProvider, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(
                Math.Clamp(
                    serviceProvider.GetRequiredService<AzureModelProviderOptions>().RequestTimeoutSeconds,
                    1,
                    1800));
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        });
        services.AddTransient<IModelInferenceProvider>(
            serviceProvider => serviceProvider.GetRequiredService<LocalModelInferenceProvider>());
        services.AddTransient<IModelInferenceProvider>(
            serviceProvider => serviceProvider.GetRequiredService<AzureModelInferenceProvider>());
        services.AddScoped<IModelInferenceProvider, TrainedModelInferenceProvider>();
        services.AddScoped<IModelProviderResolver, ModelProviderResolver>();
        services.AddScoped<IModelInferenceService, ModelInferenceService>();
        services.AddHttpClient<ILocalVisionService, LocalVisionService>(client =>
        {
            client.Timeout = TimeSpan.FromMinutes(10);
            client.MaxResponseContentBufferSize = 128 * 1024;
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        });
        services.AddHttpClient("LiveGovernment", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
        }).ConfigurePrimaryHttpMessageHandler(CreateLiveHandler);
        services.AddHttpClient<IAuthoritativeSourceVerifier, LiveAuthoritativeSourceVerifier>(
            client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(CreateLiveHandler);

        return services;
    }

    public static async Task InitializeTaxIntelligenceDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TaxIntelligenceDbContext>();
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "InferencePrompts" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_InferencePrompts" PRIMARY KEY,
                "SavedAt" TEXT NOT NULL,
                "Prompt" TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "KnowledgeDocuments" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_KnowledgeDocuments" PRIMARY KEY,
                "SourceUrl" TEXT NOT NULL,
                "Title" TEXT NOT NULL,
                "Content" TEXT NOT NULL,
                "ContentHash" TEXT NOT NULL,
                "Version" INTEGER NOT NULL,
                "IsCurrent" INTEGER NOT NULL,
                "RetrievedAt" TEXT NOT NULL,
                "LastVerifiedAt" TEXT NOT NULL,
                "ContentType" TEXT NULL,
                "PublishedDate" TEXT NULL,
                "EffectiveDate" TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_KnowledgeDocuments_SourceUrl_Version"
                ON "KnowledgeDocuments" ("SourceUrl", "Version");
            CREATE INDEX IF NOT EXISTS "IX_KnowledgeDocuments_SourceUrl_IsCurrent"
                ON "KnowledgeDocuments" ("SourceUrl", "IsCurrent");
            """,
            cancellationToken);
    }

    private static SocketsHttpHandler CreateLiveHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        UseCookies = false
    };
}
