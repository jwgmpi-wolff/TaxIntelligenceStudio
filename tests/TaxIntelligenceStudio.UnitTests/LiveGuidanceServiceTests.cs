using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;

namespace TaxIntelligenceStudio.UnitTests;

public sealed class LiveGuidanceServiceTests
{
    [Fact]
    public async Task VerifyGuidanceAsync_RefusesWhenNoEvidenceIsProvided()
    {
        var auditRepository = new RecordingAuditRepository();
        var service = new LiveGuidanceService(
            new StubVerifier(isVerified: true),
            auditRepository,
            new FixedTimeProvider());

        var response = await service.VerifyGuidanceAsync(
            new GuidanceRequest("Can I claim this deduction?", []));

        Assert.False(response.IsVerified);
        Assert.Contains(GuidanceResponse.VerificationFailureMessage, response.Guidance);
        Assert.Single(auditRepository.Entries);
    }

    [Fact]
    public async Task VerifyGuidanceAsync_RefusesWhenAnySourceCannotBeVerified()
    {
        var service = new LiveGuidanceService(
            new StubVerifier(isVerified: false),
            new RecordingAuditRepository(),
            new FixedTimeProvider());
        var request = new GuidanceRequest(
            "Can I claim this deduction?",
            [CreateSource()]);

        var response = await service.VerifyGuidanceAsync(request);

        Assert.False(response.IsVerified);
        Assert.StartsWith(GuidanceResponse.VerificationFailureMessage, response.Guidance);
        Assert.Contains(GuidanceResponse.Disclaimer, response.Guidance);
    }

    [Fact]
    public async Task VerifyGuidanceAsync_ReturnsOnlyVerifiedEvidence()
    {
        var service = new LiveGuidanceService(
            new StubVerifier(isVerified: true),
            new RecordingAuditRepository(),
            new FixedTimeProvider());
        var request = new GuidanceRequest(
            "What does Publication 17 cover?",
            [CreateSource()]);

        var response = await service.VerifyGuidanceAsync(request);

        Assert.True(response.IsVerified);
        Assert.All(response.Sources, source => Assert.True(source.IsVerified));
        Assert.Contains(GuidanceResponse.Disclaimer, response.Guidance);
    }

    [Fact]
    public async Task VerifyGuidanceAsync_AuditsMetadataWithoutQuestionText()
    {
        const string sensitiveQuestion = "Taxpayer identifier 12-3456789";
        var auditRepository = new RecordingAuditRepository();
        var service = new LiveGuidanceService(
            new StubVerifier(isVerified: true),
            auditRepository,
            new FixedTimeProvider());

        await service.VerifyGuidanceAsync(
            new GuidanceRequest(sensitiveQuestion, [CreateSource()]));

        var audit = Assert.Single(auditRepository.Entries);
        Assert.DoesNotContain(sensitiveQuestion, audit.InputJson, StringComparison.Ordinal);
        Assert.Contains("\"SourceCount\":1", audit.InputJson, StringComparison.Ordinal);
    }

    private static AuthoritativeSource CreateSource() =>
        new(
            new Uri("https://www.irs.gov/publications/p17"),
            "IRS Publication 17",
            null,
            null,
            null);

    private sealed class StubVerifier(bool isVerified) : IAuthoritativeSourceVerifier
    {
        public Task<VerifiedSource> VerifyAsync(
            AuthoritativeSource source,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new VerifiedSource(
                source,
                source.Publication,
                isVerified ? "ABC123" : string.Empty,
                DateTimeOffset.UnixEpoch,
                isVerified ? 1m : 0m,
                isVerified,
                isVerified ? null : "Unavailable"));
    }

    private sealed class RecordingAuditRepository : IAuditRepository
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }
}
