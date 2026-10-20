using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;
using TaxIntelligenceStudio.Infrastructure.Inference;

namespace TaxIntelligenceStudio.Infrastructure.Sources;

public sealed class LiveAuthoritativeSourceVerifier(
    HttpClient httpClient,
    TimeProvider timeProvider,
    ModelProviderOptions? modelProviderOptions = null) : IAuthoritativeSourceVerifier
{
    private static readonly Regex TitlePattern = new(
        @"<title\b[^>]*>(?<title>.*?)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public async Task<VerifiedSource> VerifyAsync(
        AuthoritativeSource source,
        CancellationToken cancellationToken = default)
    {
        if (modelProviderOptions?.OfflineMode == true)
        {
            return Failed(source, "Live source verification is unavailable while offline mode is enabled.");
        }

        if (!AuthoritativeSourcePolicy.IsAllowedHttps(source.SourceUrl))
        {
            return Failed(source, "Only HTTPS IRS, Treasury, and Federal Register sources are authoritative.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source.SourceUrl);
            request.Headers.CacheControl = new()
            {
                NoCache = true,
                NoStore = true,
                MaxAge = TimeSpan.Zero
            };
            request.Headers.Pragma.ParseAdd("no-cache");
            request.Headers.UserAgent.ParseAdd("TaxIntelligenceStudio/1.0 (+local desktop source verifier)");

            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return Failed(source, $"The source returned HTTP {(int)response.StatusCode}.");
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
            {
                return Failed(source, "The source returned no verifiable content.");
            }

            var titleMatch = TitlePattern.Match(content);
            var title = titleMatch.Success
                ? WebUtility.HtmlDecode(titleMatch.Groups["title"].Value.Trim())
                : source.Publication;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

            return new VerifiedSource(
                source,
                title,
                hash,
                timeProvider.GetUtcNow(),
                1.0m,
                true,
                null);
        }
        catch (HttpRequestException exception)
        {
            return Failed(source, $"Live source request failed: {exception.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(source, "Live source request timed out.");
        }
    }

    private VerifiedSource Failed(AuthoritativeSource source, string reason) =>
        new(
            source,
            source.Publication,
            string.Empty,
            timeProvider.GetUtcNow(),
            0m,
            false,
            reason);
}
