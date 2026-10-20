using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;

namespace TaxIntelligenceStudio.Infrastructure.Inference;

public sealed class ModelProviderResolver(
    IEnumerable<IModelInferenceProvider> providers,
    ModelProviderOptions options) : IModelProviderResolver
{
    private readonly IReadOnlyDictionary<string, IModelInferenceProvider> _providers =
        providers.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyCollection<ModelProviderAvailability>> GetAvailabilityAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<ModelProviderAvailability>(_providers.Count);
        foreach (var provider in _providers.Values)
        {
            if (options.OfflineMode && provider.IsCloud)
            {
                results.Add(new ModelProviderAvailability(
                    provider.Id,
                    provider.DisplayName,
                    true,
                    false,
                    provider.ModelId,
                    "Unavailable while offline mode is enabled."));
                continue;
            }

            results.Add(await provider.CheckAvailabilityAsync(cancellationToken));
        }

        return results;
    }

    public async Task<IModelInferenceProvider> ResolveAsync(
        string? providerId = null,
        CancellationToken cancellationToken = default)
    {
        var selectedId = string.IsNullOrWhiteSpace(providerId)
            ? options.DefaultProvider
            : providerId;
        if (!_providers.TryGetValue(selectedId, out var provider))
        {
            throw new InvalidOperationException(
                $"The configured model provider '{selectedId}' is not registered.");
        }
        if (options.OfflineMode && provider.IsCloud)
        {
            throw new InvalidOperationException(
                "Cloud inference is blocked because offline mode is enabled.");
        }

        var availability = await provider.CheckAvailabilityAsync(cancellationToken);
        if (!availability.IsAvailable)
        {
            throw new InvalidOperationException(
                $"{provider.DisplayName} is unavailable: {availability.Diagnostic}");
        }

        return provider;
    }
}
