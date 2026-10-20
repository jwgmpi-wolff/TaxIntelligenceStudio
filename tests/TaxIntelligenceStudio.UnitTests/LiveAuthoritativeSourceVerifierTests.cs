using System.Net;
using TaxIntelligenceStudio.Domain;
using TaxIntelligenceStudio.Infrastructure.Sources;
using TaxIntelligenceStudio.Infrastructure.Inference;

namespace TaxIntelligenceStudio.UnitTests;

public sealed class LiveAuthoritativeSourceVerifierTests
{
    [Fact]
    public async Task VerifyAsync_SendsExplicitNoCacheHeaders()
    {
        var handler = new RecordingHandler();
        var verifier = new LiveAuthoritativeSourceVerifier(
            new HttpClient(handler),
            TimeProvider.System);

        var result = await verifier.VerifyAsync(CreateSource("https://www.irs.gov/publications/p17"));

        Assert.True(result.IsVerified);
        Assert.True(handler.CacheControl?.NoCache);
        Assert.True(handler.CacheControl?.NoStore);
        Assert.Equal(TimeSpan.Zero, handler.CacheControl?.MaxAge);
        Assert.Contains(handler.Pragma!, value => value.Name == "no-cache");
        Assert.Equal(64, result.ContentHash.Length);
    }

    [Theory]
    [InlineData("http://www.irs.gov/publications/p17")]
    [InlineData("https://irs.gov.example.com/fake")]
    [InlineData("https://example.com/tax")]
    public async Task VerifyAsync_RejectsNonAuthoritativeSources(string url)
    {
        var handler = new RecordingHandler();
        var verifier = new LiveAuthoritativeSourceVerifier(
            new HttpClient(handler),
            TimeProvider.System);

        var result = await verifier.VerifyAsync(CreateSource(url));

        Assert.False(result.IsVerified);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task VerifyAsync_DoesNotContactSourcesInOfflineMode()
    {
        var handler = new RecordingHandler();
        var verifier = new LiveAuthoritativeSourceVerifier(
            new HttpClient(handler),
            TimeProvider.System,
            new ModelProviderOptions { OfflineMode = true });

        var result = await verifier.VerifyAsync(
            CreateSource("https://www.irs.gov/publications/p17"));

        Assert.False(result.IsVerified);
        Assert.Contains("offline mode", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
    }

    private static AuthoritativeSource CreateSource(string url) =>
        new(new Uri(url), "IRS Publication 17", null, null, null);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public System.Net.Http.Headers.CacheControlHeaderValue? CacheControl { get; private set; }
        public IEnumerable<System.Net.Http.Headers.NameValueHeaderValue>? Pragma { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            CacheControl = request.Headers.CacheControl;
            Pragma = request.Headers.Pragma.ToArray();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><head><title>Publication 17</title></head><body>Verified</body></html>")
            });
        }
    }
}
