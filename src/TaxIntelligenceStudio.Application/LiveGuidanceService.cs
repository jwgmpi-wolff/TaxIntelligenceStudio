using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TaxIntelligenceStudio.Domain;

namespace TaxIntelligenceStudio.Application;

public sealed class LiveGuidanceService(
    IAuthoritativeSourceVerifier sourceVerifier,
    IAuditRepository auditRepository,
    TimeProvider timeProvider) : IGuidanceService
{
    public async Task<GuidanceResponse> VerifyGuidanceAsync(
        GuidanceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Question);

        if (request.Sources.Count == 0)
        {
            return await CreateAndAuditResponseAsync(
                request,
                [],
                false,
                GuidanceResponse.VerificationFailureMessage,
                cancellationToken);
        }

        var verificationTasks = request.Sources
            .Select(source => sourceVerifier.VerifyAsync(source, cancellationToken));
        var sources = await Task.WhenAll(verificationTasks);
        var isVerified = sources.All(source => source.IsVerified);

        var guidance = isVerified
            ? "All supplied authoritative sources were verified live. Review the cited sources before preparing or filing a return."
            : GuidanceResponse.VerificationFailureMessage;

        return await CreateAndAuditResponseAsync(
            request,
            sources,
            isVerified,
            guidance,
            cancellationToken);
    }

    private async Task<GuidanceResponse> CreateAndAuditResponseAsync(
        GuidanceRequest request,
        IReadOnlyCollection<VerifiedSource> sources,
        bool isVerified,
        string guidance,
        CancellationToken cancellationToken)
    {
        var response = new GuidanceResponse(
            isVerified,
            $"{guidance} {GuidanceResponse.Disclaimer}",
            sources,
            timeProvider.GetUtcNow());

        var inputJson = JsonSerializer.Serialize(new
        {
            RequestType = "LiveGuidanceVerification",
            SourceCount = request.Sources.Count
        });
        var outputJson = JsonSerializer.Serialize(new
        {
            response.IsVerified,
            VerifiedSourceCount = sources.Count(source => source.IsVerified),
            SourceCount = sources.Count
        });
        var integrityHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{inputJson}\n{outputJson}")));

        await auditRepository.AppendAsync(
            new AuditEntry
            {
                EventType = "LiveGuidanceVerification",
                InputJson = inputJson,
                OutputJson = outputJson,
                ModelVersion = "none-source-verification-only",
                CreatedAt = response.CompletedAt,
                IntegrityHash = integrityHash
            },
            cancellationToken);

        return response;
    }
}
