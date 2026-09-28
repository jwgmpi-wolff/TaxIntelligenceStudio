namespace TaxIntelligenceStudio.Domain;

public sealed record GuidanceRequest(
    string Question,
    IReadOnlyCollection<AuthoritativeSource> Sources);

public sealed record GuidanceResponse(
    bool IsVerified,
    string Guidance,
    IReadOnlyCollection<VerifiedSource> Sources,
    DateTimeOffset CompletedAt)
{
    public const string VerificationFailureMessage = "Current guidance could not be verified.";
    public const string Disclaimer =
        "This information is for educational purposes and should be reviewed with a qualified tax professional.";
}
