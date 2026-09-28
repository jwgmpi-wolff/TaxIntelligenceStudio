namespace TaxIntelligenceStudio.Domain;

public sealed record AuthoritativeSource(
    Uri SourceUrl,
    string Publication,
    string? Section,
    DateOnly? RevisionDate,
    DateOnly? EffectiveDate);

public sealed record VerifiedSource(
    AuthoritativeSource Source,
    string Title,
    string ContentHash,
    DateTimeOffset VerifiedAt,
    decimal ConfidenceScore,
    bool IsVerified,
    string? FailureReason);
