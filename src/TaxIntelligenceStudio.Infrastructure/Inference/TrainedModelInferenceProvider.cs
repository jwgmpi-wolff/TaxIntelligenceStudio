using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;
using TaxIntelligenceStudio.Infrastructure.Training;

namespace TaxIntelligenceStudio.Infrastructure.Inference;

public sealed class TrainedModelInferenceProvider(
    IAgentModelService agentModels,
    ModelProviderOptions options,
    TimeProvider timeProvider) : IModelInferenceProvider
{
    public string Id => "Trained";
    public string DisplayName => "Project-trained adapter";
    public bool IsCloud => false;
    public string? ModelId => agentModels.GetActiveModel() is { } model
        ? $"{model.BaseModel} + {model.AdapterPath}"
        : null;

    public Task<ModelProviderAvailability> CheckAvailabilityAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var model = agentModels.GetActiveModel();
        var diagnostic = model is null
            ? "Activate your trained adapter in the Agent Model tab first."
            : model.Runtime != AgentModelService.TransformersRuntime
                ? "Select and activate Transformers / PEFT in Agent Model for direct trained-adapter chat."
                : !File.Exists(Path.Combine(model.AdapterPath, "adapter_config.json")) ||
                  !File.Exists(Path.Combine(model.AdapterPath, "adapter_model.safetensors"))
                    ? "The active adapter configuration or weights are missing."
                    : null;
        return Task.FromResult(new ModelProviderAvailability(
            Id, DisplayName, false, diagnostic is null, ModelId,
            diagnostic ?? "Active adapter files found. Uses local Python/PEFT and cached base weights; no Ollama or cloud endpoint."));
    }

    public async Task<ModelInferenceResponse> InferAsync(
        ModelInferenceRequest request,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        OpenAiCompatibleProtocol.ValidateRequest(request);
        if (OpenAiCompatibleProtocol.GetRequestCharacterCount(request) > 1_000_000)
        {
            throw new ArgumentException("The trained-model request exceeds the size limit.", nameof(request));
        }
        var availability = await CheckAvailabilityAsync(cancellationToken);
        if (!availability.IsAvailable)
        {
            throw new InvalidOperationException(availability.Diagnostic);
        }
        var model = agentModels.GetActiveModel()
            ?? throw new InvalidOperationException("The active trained adapter was cleared.");
        var startedAt = timeProvider.GetTimestamp();
        var content = await agentModels.InferChatAsync(
            model, request, options.TrainedPythonExecutable, cancellationToken, progress);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidDataException("The trained adapter returned an empty response.");
        }
        var sources = request.SourceContext ?? [];
        return new ModelInferenceResponse(
            content, Id, $"{model.BaseModel} + {model.AdapterPath}", "unknown", null,
            timeProvider.GetElapsedTime(startedAt),
            sources.Select(source => source.SourceUrl)
                .Where(url => content.Contains(url, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal).ToArray(),
            sources.Any(source => source.IsCached),
            OpenAiCompatibleProtocol.GetSourceDataMode(sources),
            []);
    }
}
