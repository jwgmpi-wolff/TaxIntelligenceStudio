namespace TaxIntelligenceStudio.Domain;

public sealed class SavedInferencePrompt
{
    public Guid Id { get; set; }
    public DateTimeOffset SavedAt { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public string DisplayText => $"{SavedAt.ToLocalTime():g} - {Prompt.ReplaceLineEndings(" ")}";
}
