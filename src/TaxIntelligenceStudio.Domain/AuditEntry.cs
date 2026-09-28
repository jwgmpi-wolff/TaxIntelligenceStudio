namespace TaxIntelligenceStudio.Domain;

public sealed class AuditEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string EventType { get; init; }
    public required string InputJson { get; init; }
    public required string OutputJson { get; init; }
    public required string ModelVersion { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string IntegrityHash { get; init; }
}
