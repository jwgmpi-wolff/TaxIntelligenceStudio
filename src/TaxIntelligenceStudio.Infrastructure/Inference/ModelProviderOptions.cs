using Microsoft.Extensions.Configuration;

namespace TaxIntelligenceStudio.Infrastructure.Inference;

public sealed class ModelProviderOptions
{
    public string DefaultProvider { get; set; } = "Local";
    public bool OfflineMode { get; set; }
    public int MaximumRetrievedDocuments { get; set; } = 3;
    public int MaximumRetrievedCharacters { get; set; } = 12_000;
    public LocalModelProviderOptions Local { get; set; } = new();
    public AzureModelProviderOptions Azure { get; set; } = new();
    public string TrainedPythonExecutable { get; set; } = "python";
    public string LocalVisionBaseAddress { get; set; } = "http://127.0.0.1:11434/";
    public string LocalVisionModelId { get; set; } = string.Empty;

    public static ModelProviderOptions FromConfiguration(IConfiguration? configuration)
    {
        var settings = new ModelProviderOptions();
        var section = configuration?.GetSection("ModelProviders");
        if (section is null)
        {
            return settings;
        }

        settings.DefaultProvider = section["DefaultProvider"] ?? settings.DefaultProvider;
        settings.TrainedPythonExecutable =
            section["TrainedPythonExecutable"] ?? settings.TrainedPythonExecutable;
        settings.LocalVisionBaseAddress =
            section["LocalVisionBaseAddress"] ?? settings.LocalVisionBaseAddress;
        settings.LocalVisionModelId = section["LocalVisionModelId"] ?? settings.LocalVisionModelId;
        settings.OfflineMode = ReadBool(section["OfflineMode"], settings.OfflineMode);
        settings.MaximumRetrievedDocuments = ReadInt(
            section["MaximumRetrievedDocuments"],
            settings.MaximumRetrievedDocuments);
        settings.MaximumRetrievedCharacters = ReadInt(
            section["MaximumRetrievedCharacters"],
            settings.MaximumRetrievedCharacters);
        var local = section.GetSection("Local");
        settings.Local.Enabled = ReadBool(local["Enabled"], settings.Local.Enabled);
        settings.Local.BaseAddress = local["BaseAddress"] ?? settings.Local.BaseAddress;
        settings.Local.ModelId = local["ModelId"] ?? settings.Local.ModelId;
        settings.Local.ApiKeyEnvironmentVariable =
            local["ApiKeyEnvironmentVariable"] ?? settings.Local.ApiKeyEnvironmentVariable;
        settings.Local.AllowNonLoopback =
            ReadBool(local["AllowNonLoopback"], settings.Local.AllowNonLoopback);
        settings.Local.SupportsSeed = ReadBool(local["SupportsSeed"], settings.Local.SupportsSeed);
        settings.Local.RequestTimeoutSeconds = ReadInt(
            local["RequestTimeoutSeconds"],
            settings.Local.RequestTimeoutSeconds);
        settings.Local.MaximumRequestCharacters = ReadInt(
            local["MaximumRequestCharacters"],
            settings.Local.MaximumRequestCharacters);

        var azure = section.GetSection("Azure");
        settings.Azure.Enabled = ReadBool(azure["Enabled"], settings.Azure.Enabled);
        settings.Azure.Endpoint = azure["Endpoint"] ?? settings.Azure.Endpoint;
        settings.Azure.DeploymentName = azure["DeploymentName"] ?? settings.Azure.DeploymentName;
        settings.Azure.ApiVersion = azure["ApiVersion"] ?? settings.Azure.ApiVersion;
        settings.Azure.ApiKeyEnvironmentVariable =
            azure["ApiKeyEnvironmentVariable"] ?? settings.Azure.ApiKeyEnvironmentVariable;
        settings.Azure.SupportsSeed = ReadBool(azure["SupportsSeed"], settings.Azure.SupportsSeed);
        settings.Azure.RequestTimeoutSeconds = ReadInt(
            azure["RequestTimeoutSeconds"],
            settings.Azure.RequestTimeoutSeconds);
        settings.Azure.MaximumRequestCharacters = ReadInt(
            azure["MaximumRequestCharacters"],
            settings.Azure.MaximumRequestCharacters);
        return settings;
    }

    private static bool ReadBool(string? value, bool fallback) =>
        bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static int ReadInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;
}

public sealed class LocalModelProviderOptions
{
    public bool Enabled { get; set; } = true;
    public string BaseAddress { get; set; } = "http://127.0.0.1:11434/v1/";
    public string ModelId { get; set; } = string.Empty;
    public string? ApiKeyEnvironmentVariable { get; set; }
    public bool AllowNonLoopback { get; set; }
    public bool SupportsSeed { get; set; }
    public int RequestTimeoutSeconds { get; set; } = 120;
    public int MaximumRequestCharacters { get; set; } = 1_000_000;
}

public sealed class AzureModelProviderOptions
{
    public bool Enabled { get; set; } = true;
    public string Endpoint { get; set; } = string.Empty;
    public string DeploymentName { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = "2024-10-21";
    public string ApiKeyEnvironmentVariable { get; set; } =
        "TAXINTELLIGENCE_AZURE_OPENAI_API_KEY";
    public bool SupportsSeed { get; set; }
    public int RequestTimeoutSeconds { get; set; } = 120;
    public int MaximumRequestCharacters { get; set; } = 1_000_000;
}
