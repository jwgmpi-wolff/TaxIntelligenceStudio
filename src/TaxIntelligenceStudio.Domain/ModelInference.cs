namespace TaxIntelligenceStudio.Domain;

public sealed record ModelChatMessage(string Role, string Content);

public sealed record ModelInferenceTurn(string Prompt, ModelInferenceResponse Response);

public sealed record RetrievedSourceContext(
    string Title,
    string SourceUrl,
    string Content,
    DateTimeOffset? RetrievedAt,
    bool IsVerified,
    bool IsCached,
    DateTimeOffset? LastVerifiedAt = null,
    DateOnly? PublishedDate = null,
    DateOnly? EffectiveDate = null);

public sealed record ModelInferenceRequest(
    IReadOnlyList<ModelChatMessage> Messages,
    int? TaxYear = null,
    string? ScenarioIdentifier = null,
    IReadOnlyList<RetrievedSourceContext>? SourceContext = null,
    int? MaximumOutputTokens = null,
    double? Temperature = null,
    int? Seed = null);

public sealed record ModelChatApiRequest(
    IReadOnlyList<ModelChatMessage> Messages,
    int? TaxYear = null,
    string? ScenarioIdentifier = null,
    int? MaximumOutputTokens = null,
    double? Temperature = null,
    int? Seed = null);

public sealed record ModelUsage(int? InputTokens, int? OutputTokens, int? TotalTokens);

public sealed record ModelProviderAvailability(
    string ProviderId,
    string DisplayName,
    bool IsCloud,
    bool IsAvailable,
    string? ModelId,
    string Diagnostic);

public sealed record ModelInferenceResponse(
    string Content,
    string ProviderId,
    string ModelId,
    string FinishReason,
    ModelUsage? Usage,
    TimeSpan Elapsed,
    IReadOnlyList<string> Citations,
    bool UsedCachedSources,
    string SourceDataMode,
    IReadOnlyList<string> Warnings);
