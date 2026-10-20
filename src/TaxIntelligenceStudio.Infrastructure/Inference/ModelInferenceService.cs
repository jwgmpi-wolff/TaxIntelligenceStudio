using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;

namespace TaxIntelligenceStudio.Infrastructure.Inference;

public sealed class ModelInferenceService(
    IModelProviderResolver providerResolver,
    IAuditRepository auditRepository,
    ModelProviderOptions options,
    TimeProvider timeProvider,
    IKnowledgeContextSearchService? knowledgeSearch = null) : IModelInferenceService
{
    public async Task<ModelInferenceResponse> InferAsync(
        ModelInferenceRequest request,
        string? providerId = null,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        OpenAiCompatibleProtocol.ValidateRequest(request);
        if (knowledgeSearch is not null && (request.SourceContext is null || request.SourceContext.Count == 0))
        {
            progress?.Report("Searching current documents in the local IRS cache...");
            var query = string.Join(
                "\n",
                request.Messages
                    .Where(message => message.Role == "user")
                    .Select(message => message.Content));
            var retrievedContext = await knowledgeSearch.SearchAsync(
                query,
                Math.Clamp(options.MaximumRetrievedDocuments, 1, 10),
                Math.Clamp(options.MaximumRetrievedCharacters, 1, 100_000),
                cancellationToken);
            progress?.Report(
                retrievedContext.Count == 0
                    ? "No matching cached IRS sources found. Checking the selected model provider..."
                    : $"Found {retrievedContext.Count} cached IRS source(s). Checking the selected model provider...");
            if (retrievedContext.Count > 0)
            {
                request = request with { SourceContext = retrievedContext };
            }
        }

        var sources = request.SourceContext ?? [];
        if (options.OfflineMode && sources.Any(source => !source.IsCached))
        {
            throw new InvalidOperationException(
                "Offline mode accepts only locally cached authoritative-source context.");
        }

        progress?.Report("Checking availability of the explicitly selected provider...");
        var provider = await providerResolver.ResolveAsync(providerId, cancellationToken);
        progress?.Report(
            $"Sending request to {provider.DisplayName} ({provider.ModelId ?? "configured model"}). " +
            "The first response may take longer while a local model loads.");
        var response = await provider.InferAsync(request, cancellationToken, progress);
        progress?.Report("Checking response currency formatting and possible magnitude changes...");
        InferenceNumericIntegrity.Validate(request.Messages, response.Content);
        progress?.Report("Model response received. Recording privacy-safe audit metadata...");
        var warnings = response.Warnings.ToList();
        if (sources.Count == 0)
        {
            warnings.Add("No verified authoritative-source context was supplied; this response is not verified tax guidance.");
        }
        else if (response.Citations.Count == 0)
        {
            warnings.Add("The response did not include an exact citation to the supplied source URLs.");
        }
        if (sources.Count > 0 && sources.All(source => source.IsCached))
        {
            warnings.Add("The response used cached authoritative-source excerpts; their content may be outdated.");
        }
        if (options.OfflineMode)
        {
            warnings.Add(sources.Count == 0
                ? "Offline mode: no cached authoritative-source context was supplied; tax facts may be incomplete or outdated."
                : "Offline mode: authoritative-source information may be outdated.");
        }
        warnings.Add("Numeric screening is limited: it does not verify calculations, tax law, eligibility, or every factual inconsistency.");
        if (response.FinishReason == "length")
        {
            warnings.Add("The model reached its output limit; this answer may be incomplete.");
        }

        response = response with
        {
            Warnings = warnings,
            SourceDataMode = options.OfflineMode ? "offline" : response.SourceDataMode
        };
        await AuditInferenceAsync(request, response, cancellationToken);
        progress?.Report("Inference complete.");
        return response;
    }

    private async Task AuditInferenceAsync(
        ModelInferenceRequest request,
        ModelInferenceResponse response,
        CancellationToken cancellationToken)
    {
        var inputMetadata = JsonSerializer.Serialize(new
        {
            response.ProviderId,
            TaxYear = request.TaxYear,
            MessageCount = request.Messages.Count,
            SourceCount = request.SourceContext?.Count ?? 0,
            SourceMode = GetSourceMode(request.SourceContext),
            Offline = options.OfflineMode
        });
        var outputMetadata = JsonSerializer.Serialize(new
        {
            response.ModelId,
            response.FinishReason,
            response.Elapsed,
            response.Usage,
            response.SourceDataMode,
            CitationCount = response.Citations.Count,
            WarningCount = response.Warnings.Count
        });
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{inputMetadata}\n{outputMetadata}")));
        await auditRepository.AppendAsync(
            new AuditEntry
            {
                EventType = "ModelInference",
                InputJson = inputMetadata,
                OutputJson = outputMetadata,
                ModelVersion = $"{response.ProviderId}/{response.ModelId}",
                CreatedAt = timeProvider.GetUtcNow(),
                IntegrityHash = hash
            },
            cancellationToken);
    }

    private static string GetSourceMode(IReadOnlyCollection<RetrievedSourceContext>? sources)
    {
            var cachedCount = sources?.Count(source => source.IsCached) ?? 0;
            return cachedCount switch
            {
                0 when sources is { Count: > 0 } => "live",
                0 => "none",
                var count when count == sources!.Count => "cached",
                _ => "mixed"
            };
    }
}
