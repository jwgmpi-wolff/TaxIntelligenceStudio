using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;

namespace TaxIntelligenceStudio.Infrastructure.Inference;

public sealed class AzureModelInferenceProvider(
    HttpClient httpClient,
    AzureModelProviderOptions options,
    TimeProvider timeProvider) : IModelInferenceProvider
{
    private static readonly string[] SupportedEndpointSuffixes =
    [
        ".openai.azure.com",
        ".openai.azure.us",
        ".openai.azure.cn"
    ];

    public string Id => "Azure";
    public string DisplayName => "Azure OpenAI";
    public bool IsCloud => true;
    public string? ModelId =>
        string.IsNullOrWhiteSpace(options.DeploymentName) ? null : options.DeploymentName;

    public Task<ModelProviderAvailability> CheckAvailabilityAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(TryGetConfiguration(out _, out _, out var diagnostic)
            ? Availability(true, "Azure OpenAI configuration is present.")
            : Availability(false, diagnostic));

    public async Task<ModelInferenceResponse> InferAsync(
        ModelInferenceRequest request,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        OpenAiCompatibleProtocol.ValidateRequest(request);
        if (!TryGetConfiguration(out var endpoint, out var apiKey, out var diagnostic))
        {
            throw new InvalidOperationException(diagnostic);
        }

        if (OpenAiCompatibleProtocol.GetRequestCharacterCount(request) >
            options.MaximumRequestCharacters)
        {
            throw new ArgumentException(
                "The inference request exceeds the configured size limit.",
                nameof(request));
        }

        var deployment = Uri.EscapeDataString(options.DeploymentName);
        var apiVersion = Uri.EscapeDataString(options.ApiVersion);
        var requestUri = new Uri(
            endpoint,
            $"openai/deployments/{deployment}/chat/completions?api-version={apiVersion}");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUri);
        httpRequest.Headers.Add("api-key", apiKey);
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(
                OpenAiCompatibleProtocol.CreatePayload(
                    request,
                    options.DeploymentName,
                    options.SupportsSeed)),
            Encoding.UTF8,
            "application/json");
        return await OpenAiCompatibleProtocol.SendAsync(
            httpClient,
            httpRequest,
            request,
            Id,
            options.DeploymentName,
            timeProvider,
            cancellationToken);
    }

    private bool TryGetConfiguration(
        out Uri endpoint,
        out string? apiKey,
        out string diagnostic)
    {
        endpoint = null!;
        apiKey = null;
        diagnostic = string.Empty;
        if (!options.Enabled)
        {
            diagnostic = "Azure OpenAI is disabled in configuration.";
            return false;
        }
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var configuredEndpoint) ||
            configuredEndpoint is null ||
            configuredEndpoint.Scheme != Uri.UriSchemeHttps ||
            !SupportedEndpointSuffixes.Any(suffix =>
                configuredEndpoint.IdnHost.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrEmpty(configuredEndpoint.UserInfo) ||
            !string.IsNullOrEmpty(configuredEndpoint.Query) ||
            !string.IsNullOrEmpty(configuredEndpoint.Fragment) ||
            string.IsNullOrWhiteSpace(options.DeploymentName) ||
            string.IsNullOrWhiteSpace(options.ApiVersion) ||
            string.IsNullOrWhiteSpace(options.ApiKeyEnvironmentVariable) ||
            options.RequestTimeoutSeconds is < 1 or > 1800 ||
            options.MaximumRequestCharacters is < 1 or > 10_000_000)
        {
            diagnostic =
                "Configure a supported HTTPS Azure OpenAI resource endpoint, deployment, API version, and limits.";
            return false;
        }

        endpoint = configuredEndpoint;
        apiKey = Environment.GetEnvironmentVariable(options.ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            diagnostic =
                $"Azure OpenAI credential is unavailable in environment variable " +
                $"{options.ApiKeyEnvironmentVariable}.";
            return false;
        }

        endpoint = new UriBuilder(configuredEndpoint)
        {
            Path = configuredEndpoint.AbsolutePath.EndsWith("/", StringComparison.Ordinal)
                ? configuredEndpoint.AbsolutePath
                : $"{configuredEndpoint.AbsolutePath}/"
        }.Uri;
        return true;
    }

    private ModelProviderAvailability Availability(bool available, string diagnostic) =>
        new(Id, DisplayName, IsCloud, available, ModelId, diagnostic);
}
