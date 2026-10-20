using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;

namespace TaxIntelligenceStudio.Infrastructure.Inference;

public sealed class LocalModelInferenceProvider(
    HttpClient httpClient,
    LocalModelProviderOptions options,
    TimeProvider timeProvider) : IModelInferenceProvider
{
    public string Id => "Local";
    public string DisplayName => "Local model";
    public bool IsCloud => false;
    public string? ModelId => string.IsNullOrWhiteSpace(options.ModelId) ? null : options.ModelId;

    public async Task<ModelProviderAvailability> CheckAvailabilityAsync(
        CancellationToken cancellationToken = default)
    {
        if (!TryGetConfiguration(out var baseAddress, out var apiKey, out var diagnostic))
        {
            return Availability(false, diagnostic);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(baseAddress, "models"));
            AddAuthorization(request, apiKey);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            return response.IsSuccessStatusCode
                ? Availability(true, "Local inference endpoint is responding.")
                : Availability(false, $"Local inference endpoint returned HTTP {(int)response.StatusCode}.");
        }
        catch (HttpRequestException)
        {
            return Availability(false, "Local inference endpoint is not reachable.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Availability(false, "Local inference endpoint health check timed out.");
        }
    }

    public async Task<ModelInferenceResponse> InferAsync(
        ModelInferenceRequest request,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        OpenAiCompatibleProtocol.ValidateRequest(request);
        if (!TryGetConfiguration(out var baseAddress, out var apiKey, out var diagnostic))
        {
            throw new InvalidOperationException(diagnostic);
        }

        EnsureRequestSize(request, options.MaximumRequestCharacters);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(baseAddress, "chat/completions"));
        AddAuthorization(httpRequest, apiKey);
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(OpenAiCompatibleProtocol.CreatePayload(
                request,
                options.ModelId,
                options.SupportsSeed)),
            Encoding.UTF8,
            "application/json");
        return await OpenAiCompatibleProtocol.SendAsync(
            httpClient,
            httpRequest,
            request,
            Id,
            options.ModelId,
            timeProvider,
            cancellationToken);
    }

    private bool TryGetConfiguration(
        out Uri baseAddress,
        out string? apiKey,
        out string diagnostic)
    {
        baseAddress = null!;
        apiKey = null;
        diagnostic = string.Empty;
        if (!options.Enabled)
        {
            diagnostic = "Local inference is disabled in configuration.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(options.ModelId))
        {
            diagnostic = "Configure a local model ID before using local inference.";
            return false;
        }
        if (!Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out var configuredAddress) ||
            configuredAddress is null ||
            configuredAddress.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(configuredAddress.UserInfo) ||
            !string.IsNullOrEmpty(configuredAddress.Query) ||
            !string.IsNullOrEmpty(configuredAddress.Fragment))
        {
            diagnostic = "Configure a valid local inference endpoint without embedded credentials.";
            return false;
        }

        baseAddress = configuredAddress;
        var isLoopback = configuredAddress.IsLoopback;
        if (!isLoopback && !options.AllowNonLoopback)
        {
            diagnostic = "The local inference endpoint must bind to loopback unless network exposure is explicitly enabled.";
            return false;
        }
        if (!isLoopback && baseAddress.Scheme != Uri.UriSchemeHttps)
        {
            diagnostic = "Non-loopback inference endpoints require HTTPS.";
            return false;
        }
        if (options.RequestTimeoutSeconds is < 1 or > 1800 ||
            options.MaximumRequestCharacters is < 1 or > 10_000_000)
        {
            diagnostic = "Local inference request limits are invalid.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(options.ApiKeyEnvironmentVariable))
        {
            apiKey = Environment.GetEnvironmentVariable(options.ApiKeyEnvironmentVariable);
        }
        if (!isLoopback && string.IsNullOrWhiteSpace(apiKey))
        {
            diagnostic = "A configured API key is required for non-loopback local inference.";
            return false;
        }

        var normalizedPath = baseAddress.AbsolutePath.EndsWith("/", StringComparison.Ordinal)
            ? baseAddress.AbsolutePath
            : $"{baseAddress.AbsolutePath}/";
        baseAddress = new UriBuilder(baseAddress) { Path = normalizedPath }.Uri;
        return true;
    }

    private static void EnsureRequestSize(ModelInferenceRequest request, int maximumCharacters)
    {
        if (OpenAiCompatibleProtocol.GetRequestCharacterCount(request) > maximumCharacters)
        {
            throw new ArgumentException(
                "The inference request exceeds the configured size limit.",
                nameof(request));
        }
    }

    private static void AddAuthorization(HttpRequestMessage request, string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    private ModelProviderAvailability Availability(bool available, string diagnostic) =>
        new(Id, DisplayName, IsCloud, available, ModelId, diagnostic);
}
