namespace TaxIntelligenceStudio.Domain;

public sealed class KnowledgeDocument
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string SourceUrl { get; init; }
    public required string Title { get; init; }
    public required string Content { get; init; }
    public required string ContentHash { get; init; }
    public int Version { get; init; }
    public bool IsCurrent { get; set; }
    public DateTimeOffset RetrievedAt { get; init; }
    public DateTimeOffset LastVerifiedAt { get; set; }
    public string? ContentType { get; init; }
    public DateOnly? PublishedDate { get; init; }
    public DateOnly? EffectiveDate { get; init; }
}

public sealed record CrawlProgress(
    int Discovered,
    int Processed,
    int Added,
    int Updated,
    int Unchanged,
    int Failed,
    string CurrentUrl);

public sealed record CrawlResult(
    int Discovered,
    int Processed,
    int Added,
    int Updated,
    int Unchanged,
    int Failed,
    DateTimeOffset CompletedAt);
