using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;

namespace TaxIntelligenceStudio.Infrastructure.Training;

public sealed class ModelCatalogService : IModelCatalogService
{
    private static readonly IReadOnlyCollection<BaseModelOption> Models =
    [
        new("Qwen", "Qwen/Qwen2.5-Coder-1.5B-Instruct",
            "Qwen 2.5 Coder 1.5B Instruct (recommended for laptop GPUs)", 1.5m, true),
        new("Qwen", "Qwen/Qwen2.5-Coder-3B-Instruct",
            "Qwen 2.5 Coder 3B Instruct", 3m, true),
        new("Qwen", "Qwen/Qwen2.5-Coder-7B-Instruct",
            "Qwen 2.5 Coder 7B Instruct (CUDA recommended)", 7m, false),
        new("Llama", "meta-llama/Llama-3.1-8B-Instruct",
            "Llama 3.1 8B Instruct (gated model)", 8m, false),
        new("Mistral", "mistralai/Mistral-7B-Instruct-v0.3",
            "Mistral 7B Instruct v0.3", 7m, false),
        new("Phi", "microsoft/Phi-3.5-mini-instruct",
            "Phi 3.5 Mini Instruct", 3.8m, true)
    ];

    private static readonly IReadOnlyCollection<string> Families =
        Models.Select(model => model.Family)
            .Distinct(StringComparer.Ordinal)
            .Append("Custom")
            .ToList();

    public IReadOnlyCollection<string> GetFamilies() => Families;

    public IReadOnlyCollection<BaseModelOption> GetModels(string family) =>
        Models.Where(model => model.Family.Equals(family, StringComparison.Ordinal))
            .OrderBy(model => model.ParameterCountBillions)
            .ToList();
}
