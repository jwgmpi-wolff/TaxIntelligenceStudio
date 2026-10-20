namespace TaxIntelligenceStudio.Domain;

public sealed class TrainingDatasetLog
{
    public Guid Id { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string BaseModel { get; set; } = string.Empty;
    public string DatasetPaths { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Summary => $"{StartedAt.ToLocalTime():g} | {Status} | {BaseModel}";
    public string Details => $"{Summary}{Environment.NewLine}Output: {OutputDirectory}" +
        $"{Environment.NewLine}Completed: {CompletedAt?.ToLocalTime().ToString("g") ?? "Not recorded"}" +
        $"{Environment.NewLine}Datasets:{Environment.NewLine}{DatasetPaths}";
}
